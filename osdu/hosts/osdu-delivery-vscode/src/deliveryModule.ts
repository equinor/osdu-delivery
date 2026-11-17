import * as path from 'path';
import * as vscode from 'vscode';
import { IndexedDocument } from '../../../../sqlflow/tools/sqlflow-vscode/src/documents/documentIndex';
import { scalarKeys } from '../../../../sqlflow/tools/sqlflow-vscode/src/documents/yamlPath';
import { ModuleContext, VscodeModule } from '../../../../sqlflow/tools/sqlflow-vscode/src/host';
import { fetchTemplate } from './deliverySchemas';
import { MappingEditing, WorkingCopyTemplates } from './mappingEditing';
import { objectParts } from './objectNames';
import { recordPath } from './templates';

/** The page of a mapping being written, in the hosted build (osdu/gui/src/hosted/module.tsx). */
const WORKING_COPY_MAPPING_PAGE = '/delivery/working-copy/mapping';

/** The context key that says what kind of document the editor in front holds, which the manifest's menus read. */
const DOCUMENT_IN_FRONT = 'osduDelivery.documentInFront';

interface LineageObject {
    key: string;
    name: string;
    schema?: string | null;
    database?: string | null;
    kind?: string;
}

/** The OSDU Delivery module: what the module's kinds and documents add to the extension. */
export const deliveryModule: VscodeModule = {
    activate(context: ModuleContext): void {
        const { estate, pages, documents, references, lenses, command, extension } = context;
        const mcp = estate.mcp;
        const templates = new WorkingCopyTemplates();
        const editing = new MappingEditing(templates, documents);
        const mappingFiles = async (): Promise<IndexedDocument[]> =>
            (await documents.all()).filter((document) => document.documentType === 'mapping');
        const selector: vscode.DocumentSelector = [{ language: 'sqlflow-flow' }];
        extension.subscriptions.push(
            templates,
            editing,
            vscode.languages.registerCompletionItemProvider(selector, editing, '.', ' '),
            vscode.languages.registerHoverProvider(selector, editing)
        );

        /** Shows what a mapping makes, beside its file: the mapping's page, read from the file as it is saved. */
        const previewMapping = (uri: vscode.Uri): void => {
            pages.open(`${WORKING_COPY_MAPPING_PAGE}?document=${encodeURIComponent(uri.toString())}`, { beside: true });
        };

        // The menus offer a mapping's preview while a mapping is the document in front.
        const sayDocumentInFront = async (): Promise<void> => {
            const uri = vscode.window.activeTextEditor?.document.uri;
            const document = uri ? await documents.at(uri) : undefined;
            await vscode.commands.executeCommand('setContext', DOCUMENT_IN_FRONT, document?.documentType ?? document?.flowType ?? '');
        };
        extension.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(() => void sayDocumentInFront()));
        void sayDocumentInFront();

        // What a delivery flow's and a mapping's keys refer to.
        references.register({
            answers: (keyPath) => keyPath === 'render.mapping' || keyPath === 'interfaces.mapping' || keyPath.endsWith('.render.mapping'),
            values: async () => (await mappingFiles()).map((file) => ({
                value: path.basename(file.uri.fsPath).replace(/\.ya?ml$/, ''),
                detail: file.relativePath,
            })),
            target: async (value) => (await mappingFiles()).find((file) => path.basename(file.uri.fsPath).replace(/\.ya?ml$/, '') === value)?.uri,
        });
        references.register({
            answers: (keyPath) => keyPath === 'template.kind' || /^searches\..+\.schema\.kind$/.test(keyPath),
            values: async () => (await templates.all()).map((file) => ({ value: file.kind, detail: vscode.workspace.asRelativePath(file.uri) })),
            target: async (value, document) => (await templates.forDocument(value, document.uri))?.uri,
        });
        references.register({
            answers: (keyPath, document) => document?.kind === 'flow' && (keyPath === 'partitions.name' || keyPath === 'partition'),
            values: async () => {
                const answer = await mcp.call<{ partitions?: { name: string; description?: string }[] } | { name: string; description?: string }[]>('delivery_partitions');
                const partitions = Array.isArray(answer) ? answer : answer.partitions ?? [];
                return partitions.map((partition) => ({ value: partition.name, detail: partition.description }));
            },
        });

        /** The columns of the table a mapping's rows come from: the record table of the delivery flow that pins it. */
        const sourceColumns = async (mapping: IndexedDocument): Promise<{ value: string; detail?: string }[]> => {
            const reference = path.basename(mapping.uri.fsPath).replace(/\.ya?ml$/, '');
            for (const flow of (await documents.all()).filter((document) => document.flowType === 'delivery')) {
                const text = Buffer.from(await vscode.workspace.fs.readFile(flow.uri)).toString('utf8');
                if (!text.includes(reference)) {
                    continue;
                }
                const object = scalarKeys(text.split(/\r?\n/)).find((key) => key.path.endsWith('record.object'))?.value;
                if (!object) {
                    continue;
                }
                const parts = objectParts(object);
                const listed = await mcp.call<{ items: LineageObject[] } | LineageObject[]>('lineage_objects', { name: parts.name });
                const objects = (Array.isArray(listed) ? listed : listed.items).filter((candidate) => candidate.name === parts.name);
                const found = objects.find((candidate) => candidate.schema === parts.schema) ?? objects[0];
                if (found) {
                    const columns = await mcp.call<{ items: { name: string; dataType?: string | null }[] } | { name: string; dataType?: string | null }[]>('lineage_object_columns', { key: found.key });
                    return (Array.isArray(columns) ? columns : columns.items).map((column) => ({ value: column.name, detail: column.dataType ?? undefined }));
                }
            }
            return [];
        };
        references.register({
            answers: (keyPath, document) => document?.documentType === 'mapping'
                && (keyPath === 'dataset.key' || keyPath === 'dataset.identity' || keyPath.endsWith('.$from') || recordPath(keyPath) !== null),
            values: async (document) => (document ? sourceColumns(document) : []),
        });

        command('osduDelivery.previewMapping', async (arg: unknown) => {
            const uri = arg instanceof vscode.Uri ? arg : vscode.window.activeTextEditor?.document.uri;
            const document = uri ? await documents.at(uri) : undefined;
            if (document?.documentType !== 'mapping') {
                void vscode.window.showInformationMessage(`${context.product}: open a mapping document to preview what it makes.`);
                return;
            }
            previewMapping(document.uri);
        });

        command('osduDelivery.scaffoldMapping', async () => {
            const repos = await estate.repos();
            const listed = await mcp.call<{ repoId: string; repository?: string; path: string; kind?: string; version?: string }[] | { items: { repoId: string; path: string; kind?: string }[] }>('delivery_templates');
            const files = Array.isArray(listed) ? listed : listed.items;
            const template = await vscode.window.showQuickPick(
                files.filter((file) => file.kind).map((file) => ({ label: file.kind ?? file.path, description: file.path, file })),
                { placeHolder: 'Which template should the mapping fill?', matchOnDescription: true }
            );
            if (!template) {
                return;
            }
            const name = await vscode.window.showInputBox({ prompt: 'The mapping\'s name', value: /--([\w]+):/.exec(template.label)?.[1], ignoreFocusOut: true });
            const system = name && (await vscode.window.showInputBox({ prompt: 'The source system its records come from', ignoreFocusOut: true }));
            if (!name || !system) {
                return;
            }
            const answer = await mcp.call<{ yaml: string }>('delivery_scaffold_mapping', {
                repoId: template.file.repoId ?? repos[0]?.id,
                templatePath: template.file.path,
                name,
                mappingVersion: '1.0.0',
                system,
            });
            const document = await vscode.workspace.openTextDocument({ language: 'sqlflow-flow', content: answer.yaml });
            await vscode.window.showTextDocument(document);
        });

        command('osduDelivery.fetchTemplate', () => fetchTemplate(mcp, context.product));

        // The module's pages a person reaches for while writing: each opens in a tab, as the side bar's menu opens it.
        command('osduDelivery.openDelivery', async () => pages.open('/delivery'));
        command('osduDelivery.openTemplates', async () => pages.open('/delivery/templates'));
        command('osduDelivery.findRecord', async () => pages.open('/delivery/records'));

        lenses.register(async (document) => (document.documentType === 'mapping'
            ? [{ title: '$(open-preview) Preview', command: 'osduDelivery.previewMapping', arguments: [document.uri] }]
            : []));
    },
};

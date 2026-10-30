import * as path from 'path';
import * as vscode from 'vscode';
import { activateHost, deactivateHost, VscodeHost } from '../../../../sqlflow/tools/sqlflow-vscode/src/host';
import { Estate } from '../../../../sqlflow/tools/sqlflow-vscode/src/mcp/estate';
import { deliveryModule } from './deliveryModule';
import { SKELETONS } from './skeletons';

/** The repository's root from a checkout's extension folder, where the builds land. */
const repositoryRoot = (extensionPath: string): string => path.join(extensionPath, '..', '..', '..');

function builds(root: string): string[] {
    return ['release', 'debug'].map((profile) => path.join(root, 'target', profile));
}

/** A flow the verbs below work on: `check`, `preview` and `values` read a delivery flow. */
const isDelivery = (document: { flowType?: string }): boolean => document.flowType === 'delivery';

/** OSDU Delivery's extension: SQLFlow's with the delivery module, under the product's name and with its own server. */
const OSDU_DELIVERY: VscodeHost = {
    product: 'OSDU Delivery',
    mcp: {
        program: 'osdu-delivery-mcp',
        providerId: 'osduDelivery.mcp',
        searchPaths: (extensionPath) => builds(path.join(repositoryRoot(extensionPath), 'osdu', 'hosts', 'osdu-delivery-mcp')),
    },
    lspSearchPaths: (extensionPath) => builds(path.join(repositoryRoot(extensionPath), 'sqlflow', 'tools')),
    censusDirectories: (extensionPath) => [path.join(extensionPath, 'dist', 'census')],
    docCorpora: (extensionPath) => [
        path.join(extensionPath, 'dist', 'corpus', 'osdu', 'docs', 'reference'),
        path.join(extensionPath, 'dist', 'corpus', 'sqlflow', 'docs', 'reference'),
    ],
    skeletons: SKELETONS,
    cliVerbs: [
        { verb: 'validate', label: 'Validate', detail: 'Read this document through the loader a run uses, offline.', applies: () => true },
        { verb: 'check', label: 'Check', detail: 'Would this delivery flow render? Its mapping, template and cache, sending nothing.', applies: isDelivery },
        { verb: 'preview', label: 'Preview', detail: 'One record exactly as it would be sent, sending nothing.', applies: isDelivery },
        { verb: 'values', label: 'Check Values', detail: 'Which rows will not give a property the value its template expects.', applies: isDelivery },
    ],
    modules: [deliveryModule],
};

/** What the extension offers another extension, and its own integration suite. */
export interface OsduDeliveryApi {
    skeletons: typeof SKELETONS;
    estate: Estate;
}

export async function activate(context: vscode.ExtensionContext): Promise<OsduDeliveryApi> {
    const { estate } = await activateHost(context, OSDU_DELIVERY);
    return { skeletons: SKELETONS, estate };
}

export async function deactivate(): Promise<void> {
    await deactivateHost();
}

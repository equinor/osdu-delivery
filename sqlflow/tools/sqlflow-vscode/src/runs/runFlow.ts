import * as vscode from 'vscode';
import { Estate, FlowKindOperation, Pipeline } from '../mcp/estate';

interface RunArguments {
    fullLoad?: boolean;
    operation?: string;
    values?: Record<string, unknown>;
    payload?: Record<string, unknown>;
}

/** A parameter a run of a flow takes, as a module or the document declares it. */
export interface RunParameter {
    name: string;
    required?: boolean;
    defaultValue?: string;
    description?: string;
    /** The values it may take, offered as a choice. */
    options?: string[];
}

/** What a run of a kind may be given beyond its operation and parameters: undefined for nothing, null to cancel the run. */
export type RunPayloadReader = (pipeline: Pipeline, operation: string | undefined) => Promise<Record<string, unknown> | undefined | null>;

/** Asks what a run should do. A kind a host module registers says what its runs can do; SQLFlow's own kinds take a full
 * load or not. A run that writes to the flow's target is confirmed. */
async function askArguments(estate: Estate, pipeline: Pipeline, parameters: RunParameter[], payloadOf: RunPayloadReader): Promise<RunArguments | undefined> {
    const kinds = await estate.flowKinds();
    const kind = kinds.runOperations?.find((candidate) => candidate.flowType === pipeline.kind);
    if (!kind) {
        const load = await vscode.window.showQuickPick(['Incremental', 'Full load'], { placeHolder: `Run '${pipeline.name}'` });
        return load === undefined ? undefined : { fullLoad: load === 'Full load' };
    }

    const operation = kind.operations.length === 0
        ? undefined
        : await pickOperation(pipeline, kind.operations, kind.defaultOperation ?? undefined);
    if (kind.operations.length > 0 && !operation) {
        return undefined;
    }

    const values: Record<string, unknown> = {};
    for (const parameter of parameters) {
        const value = parameter.options
            ? await vscode.window.showQuickPick(parameter.options, { title: `Run '${pipeline.name}': ${parameter.name}`, ignoreFocusOut: true })
            : await vscode.window.showInputBox({
                title: `Run '${pipeline.name}': ${parameter.name}`,
                prompt: parameter.description,
                value: parameter.defaultValue,
                ignoreFocusOut: true,
                validateInput: (text) => (parameter.required && text.trim() === '' ? `${parameter.name} is required.` : undefined),
            });
        if (value === undefined) {
            return undefined;
        }
        if (value.trim() !== '') {
            values[parameter.name] = value.trim();
        }
    }

    const payload = await payloadOf(pipeline, operation?.name);
    if (payload === null) {
        return undefined;
    }

    if (operation?.writesTarget) {
        const confirmed = await vscode.window.showWarningMessage(
            `Run '${operation.label}' on ${pipeline.name}? This writes to the flow's target.`,
            { modal: true },
            'Run'
        );
        if (confirmed !== 'Run') {
            return undefined;
        }
    }

    return { operation: operation?.name, values: Object.keys(values).length > 0 ? values : undefined, payload };
}

async function pickOperation(pipeline: Pipeline, operations: FlowKindOperation[], defaultOperation: string | undefined): Promise<FlowKindOperation | undefined> {
    const sorted = [...operations].sort((a, b) => Number(b.name === defaultOperation) - Number(a.name === defaultOperation));
    const chosen = await vscode.window.showQuickPick(
        sorted.map((operation) => ({
            label: operation.label,
            description: operation.writesTarget ? 'writes to the target' : undefined,
            detail: operation.description,
            operation,
        })),
        { placeHolder: `Run '${pipeline.name}': what should it do?` }
    );
    return chosen?.operation;
}

/**
 * Starts a run of a flow: asks what the run should do and queues it through the same tool an assistant calls. Answers
 * the run's id, or undefined when the person backed out; the run is followed on its own page, which shows its trace
 * as it is written.
 */
export async function runFlow(
    estate: Estate,
    pipeline: Pipeline,
    parameters: RunParameter[],
    payloadOf: RunPayloadReader
): Promise<string | undefined> {
    const args = await askArguments(estate, pipeline, parameters, payloadOf);
    if (!args) {
        return undefined;
    }
    return (await estate.triggerRun({ repoId: pipeline.repoId, flowName: pipeline.name, ...args })).runId;
}

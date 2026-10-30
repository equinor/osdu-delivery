import * as vscode from 'vscode';
import { Estate, Pipeline } from '../../../../sqlflow/tools/sqlflow-vscode/src/mcp/estate';
import { RunChoices, RunOption, runOptions, runPayload, SendAgain } from './runOptions';

const OPTION_LABELS: Record<RunOption, { label: string; detail: string }> = {
    force: { label: 'Force', detail: 'Pass the gate that would stop this run.' },
    submissionId: { label: 'A submission', detail: 'Work on one submission rather than plan a new one.' },
    recordKeys: { label: 'Only these records', detail: 'Scope the run to records named by key.' },
    sendAgain: { label: 'Send again', detail: 'Send more than what changed.' },
    interfaces: { label: 'Only these interfaces', detail: 'Leave the other interfaces of the source out of this run.' },
    types: { label: 'Only these types', detail: 'Refresh some of the types the flow declares, keeping what the cache holds of the others.' },
};

const SEND_AGAIN: { label: string; value: SendAgain }[] = [
    { label: 'What renders differently', value: 'uptodate' },
    { label: 'Metadata and payload', value: 'all' },
    { label: 'Metadata only', value: 'metadata' },
    { label: 'Payload only', value: 'payload' },
];

function list(text: string): string[] {
    return text.split(/[\s,]+/).map((value) => value.trim()).filter((value) => value !== '');
}

/** The names under a cache flow's `types`. */
function typesOf(yaml: string): string[] {
    return [...yaml.matchAll(/^\s+(?:-\s+)?name\s*:\s*["']?([\w.-]+)/gm)].map((match) => match[1]);
}

/**
 * Asks what a run should be given beyond its operation, offering only what that operation takes. Answers undefined for
 * nothing chosen, and null when the person backed out, which cancels the run.
 */
export async function askRunPayload(estate: Estate, pipeline: Pipeline, operation: string | undefined): Promise<Record<string, unknown> | undefined | null> {
    const options = runOptions(pipeline.kind, operation);
    if (options.length === 0) {
        return undefined;
    }

    const picked = await vscode.window.showQuickPick(
        options.map((option) => ({ ...OPTION_LABELS[option], option })),
        { canPickMany: true, placeHolder: `Run '${pipeline.name}': anything beyond the usual? Enter to run as it is.`, ignoreFocusOut: true }
    );
    if (picked === undefined) {
        return null;
    }

    const choices: RunChoices = {};
    for (const { option } of picked) {
        switch (option) {
            case 'force':
                choices.force = true;
                break;
            case 'submissionId': {
                const value = await vscode.window.showInputBox({ prompt: 'The submission to work on', ignoreFocusOut: true });
                if (value === undefined) {
                    return null;
                }
                choices.submissionId = value.trim();
                break;
            }
            case 'recordKeys': {
                const value = await vscode.window.showInputBox({ prompt: 'The records\' keys, separated by commas or spaces', ignoreFocusOut: true });
                if (value === undefined) {
                    return null;
                }
                choices.recordKeys = list(value);
                break;
            }
            case 'sendAgain': {
                const value = await vscode.window.showQuickPick(SEND_AGAIN, { placeHolder: 'Send what again?', ignoreFocusOut: true });
                if (value === undefined) {
                    return null;
                }
                choices.sendAgain = value.value;
                break;
            }
            case 'interfaces': {
                const flow = await estate.mcp.call<{ interfaces?: { interface?: string; name?: string }[] | { error: string } }>('delivery_flow', { pipelineId: pipeline.id });
                const named = (Array.isArray(flow.interfaces) ? flow.interfaces : [])
                    .map((entry) => entry.interface ?? entry.name)
                    .filter((name): name is string => !!name);
                const value = named.length > 0
                    ? await vscode.window.showQuickPick(named, { canPickMany: true, placeHolder: 'Which interfaces?', ignoreFocusOut: true })
                    : list((await vscode.window.showInputBox({ prompt: 'The interfaces, separated by commas', ignoreFocusOut: true })) ?? '');
                if (value === undefined) {
                    return null;
                }
                choices.interfaces = value;
                break;
            }
            case 'types': {
                const declared = typesOf((await estate.pipeline(pipeline.id)).yaml);
                const value = await vscode.window.showQuickPick(declared, { canPickMany: true, placeHolder: 'Which types?', ignoreFocusOut: true });
                if (value === undefined) {
                    return null;
                }
                choices.types = value;
                break;
            }
        }
    }
    return runPayload(options, choices);
}

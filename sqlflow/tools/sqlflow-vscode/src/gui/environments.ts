import * as vscode from 'vscode';
import {
    checkDraft,
    environmentOf,
    MAX_ENVIRONMENTS,
    readSavedEnvironments,
    sameControlPlane,
    type SavedEnvironment,
} from '../../../../gui/src/embed/environments';
import type { EnvironmentAnswer, HostEnvironment } from '../../../../gui/src/embed/protocol';
import { credentialFile, heldSignIn, readCredentialFile } from './credentials';
import { isUnknownSettingRefusal, probeControlPlane, readEnvironmentRequest, SettingNotLoaded } from './environmentCalls';

/** The action that reloads the window, offered when the window has not loaded a setting of the extension. */
const RELOAD_WINDOW = 'Reload Window';

/** The setting the environments are saved in. */
export const ENVIRONMENTS_SETTING = 'sqlflow.environments';

/** The saved environments with the sign-in kept for each, and the one the control plane in use is. */
export interface DescribedEnvironments {
    environments: HostEnvironment[];
    environment: HostEnvironment | null;
}

/** An environment as the setting holds it: a GUI address only when there is one. */
function asSetting(environment: SavedEnvironment): Record<string, string> {
    const saved: Record<string, string> = { name: environment.name, url: environment.url, stage: environment.stage };
    if (environment.guiUrl !== null) {
        saved.guiUrl = environment.guiUrl;
    }
    return saved;
}

/**
 * Writes a setting, once this window is known to hold it. VS Code gives every setting it has registered the default the
 * extension declares, and refuses to write one it has no record of (the extension installed or updated under a window
 * that has not reloaded since) in words that do not say what to do, so a setting with no default here, or a refusal of
 * that kind, is said as SettingNotLoaded, which says to reload the window.
 */
async function update(config: vscode.WorkspaceConfiguration, key: string, setting: string, value: unknown, where: vscode.ConfigurationTarget): Promise<void> {
    if (config.inspect(key)?.defaultValue === undefined) {
        throw new SettingNotLoaded(setting);
    }
    try {
        await config.update(key, value, where);
    } catch (error) {
        if (isUnknownSettingRefusal(error)) {
            throw new SettingNotLoaded(setting);
        }
        throw error;
    }
}

/**
 * Writes one of the extension's settings where it is set: in the workspace when the workspace sets it, so the value the
 * editor reads is the one written, and in the person's own settings otherwise. Nothing is written when it holds already.
 */
async function writeSetting(key: 'controlPlaneUrl' | 'guiUrl', value: string): Promise<void> {
    const config = vscode.workspace.getConfiguration('sqlflow');
    if (config.get<string>(key, '').trim() === value) {
        return;
    }
    const where = config.inspect<string>(key)?.workspaceValue !== undefined
        ? vscode.ConfigurationTarget.Workspace
        : vscode.ConfigurationTarget.Global;
    await update(config, key, `sqlflow.${key}`, value, where);
}

/**
 * The environments a person works against, each a control plane they saved with a name and its stage
 * (`sqlflow.environments`, the person's own settings and never a workspace's, so a repository cannot relabel production as
 * development). Working against one is the control plane setting every surface of the extension already follows, so the
 * MCP server, the command line in the terminals, the assistants the server is shared with and the pages move together,
 * and each takes the sign-in the shared credential file keeps for that control plane: switching back to an environment
 * signed in to before needs no browser while its session lasts. The GUI's address moves with it, so "open in the browser"
 * never opens another environment's GUI on this one's ids.
 */
export class Environments implements vscode.Disposable {
    private readonly onChange = new vscode.EventEmitter<void>();
    /** Fires when the saved environments change, by this extension or by a person editing their settings. */
    readonly changed = this.onChange.event;
    private readonly subscription: vscode.Disposable;
    private readonly output: vscode.LogOutputChannel;
    private readonly userAgent: string;
    /** The setting's mistakes as last logged, so each is logged once rather than on every read. */
    private logged = '';

    constructor(output: vscode.LogOutputChannel, userAgent: string) {
        this.output = output;
        this.userAgent = userAgent;
        this.subscription = vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration(ENVIRONMENTS_SETTING)) {
                this.onChange.fire();
            }
        });
    }

    dispose(): void {
        this.subscription.dispose();
        this.onChange.dispose();
    }

    /** The saved environments and the setting's mistakes, read now: an entry that cannot be read is left out and logged. */
    private read(): { environments: SavedEnvironment[]; problems: string[] } {
        const raw = vscode.workspace.getConfiguration().get<unknown>(ENVIRONMENTS_SETTING);
        const read = readSavedEnvironments(raw, ENVIRONMENTS_SETTING);
        const said = read.problems.join('\n');
        if (said !== this.logged) {
            this.logged = said;
            read.problems.forEach((problem) => this.output.warn(`[environments] ${problem}`));
        }
        return read;
    }

    /** The saved environments, in the person's order. */
    saved(): SavedEnvironment[] {
        return this.read().environments;
    }

    /** The saved environment the control plane at `url` is, or null when it is none of them. */
    of(url: string | null): SavedEnvironment | null {
        return environmentOf(this.saved(), url);
    }

    /** The saved environments with the sign-in kept for each, and the one the control plane at `apiBaseUrl` is. */
    describe(apiBaseUrl: string | null): DescribedEnvironments {
        const saved = this.saved();
        const entries = saved.length === 0 ? {} : readCredentialFile(credentialFile());
        const environments = saved.map((environment): HostEnvironment => {
            const held = heldSignIn(entries, environment.url);
            return { ...environment, signedIn: held !== null, account: held?.account ?? null };
        });
        return { environments, environment: environmentOf(environments, apiBaseUrl) };
    }

    /** Does what the side bar asked, and says how it went: never throws, since the answer is what the page shows. */
    async handle(raw: unknown): Promise<EnvironmentAnswer> {
        try {
            const request = readEnvironmentRequest(raw);
            switch (request.kind) {
                case 'use':
                    await this.use(request.url);
                    return { ok: true, reach: null };
                case 'save':
                    await this.save(request.replaces, request.environment, request.use);
                    return { ok: true, reach: null };
                case 'remove':
                    await this.remove(request.url);
                    return { ok: true, reach: null };
                case 'probe':
                    return { ok: true, reach: await probeControlPlane(request.url, this.userAgent) };
            }
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            this.output.warn(`[environments] ${message}`);
            if (error instanceof SettingNotLoaded) {
                // The form says it too; the editor's own notification is where the reload is one click.
                void vscode.window.showWarningMessage(message, RELOAD_WINDOW).then((pick) => {
                    if (pick === RELOAD_WINDOW) {
                        void vscode.commands.executeCommand('workbench.action.reloadWindow');
                    }
                });
            }
            return { ok: false, problem: message };
        }
    }

    /** Works against a saved environment: only one the person saved, so no page names an address of its choosing. */
    async use(url: string): Promise<void> {
        const environment = this.of(url);
        if (environment === null) {
            throw new Error('No saved environment has that address, so nothing was switched. Save it first.');
        }
        await this.workAgainst(environment);
    }

    /** Moves the control plane, and the GUI's address with it, to the environment's. */
    private async workAgainst(environment: SavedEnvironment): Promise<void> {
        // The GUI first: the control plane's change is the one every surface restarts on, so it comes last and finds both.
        await writeSetting('guiUrl', environment.guiUrl ?? '');
        await writeSetting('controlPlaneUrl', environment.url);
        const now = vscode.workspace.getConfiguration('sqlflow').get<string>('controlPlaneUrl', '').trim();
        if (!sameControlPlane(now, environment.url)) {
            // A folder's own settings outweigh the workspace's and the person's; say so rather than show a switch that
            // did not happen.
            throw new Error(
                `A folder of this workspace sets sqlflow.controlPlaneUrl, which outweighs the switch to ${environment.name}. `
                + 'Remove it from that folder\'s settings to switch here.'
            );
        }
    }

    /** The list the setting holds, refused while it holds an entry that cannot be read, which a write would lose. */
    private writable(): SavedEnvironment[] {
        const { environments, problems } = this.read();
        if (problems.length > 0) {
            throw new Error(`${problems[0]} Correct ${ENVIRONMENTS_SETTING} in your settings first, so nothing in it is lost.`);
        }
        return environments;
    }

    private async write(environments: SavedEnvironment[]): Promise<void> {
        await update(
            vscode.workspace.getConfiguration(),
            ENVIRONMENTS_SETTING,
            ENVIRONMENTS_SETTING,
            environments.length === 0 ? undefined : environments.map(asSetting),
            vscode.ConfigurationTarget.Global
        );
    }

    /**
     * Saves an environment, new or in place of the one at `replaces`, checked by the rules the form checked it with; with
     * `use`, works against it too. Changing the environment in use moves the control plane with it.
     */
    async save(replaces: string | null, environment: SavedEnvironment, use: boolean): Promise<void> {
        const list = this.writable();
        const index = replaces === null ? -1 : list.findIndex((saved) => sameControlPlane(saved.url, replaces));
        if (replaces !== null && index < 0) {
            throw new Error('The environment being changed is no longer saved: another window may have removed it.');
        }
        if (index < 0 && list.length >= MAX_ENVIRONMENTS) {
            throw new Error(`At most ${MAX_ENVIRONMENTS} environments are kept. Remove one to add another.`);
        }
        const others = list.filter((_, at) => at !== index);
        const checked = checkDraft({ ...environment, guiUrl: environment.guiUrl ?? '' }, others);
        if (checked.problems !== null) {
            throw new Error(Object.values(checked.problems).join(' '));
        }
        const next = index < 0 ? [...list, checked.environment] : list.map((saved, at) => (at === index ? checked.environment : saved));
        await this.write(next);
        if (use) {
            await this.workAgainst(checked.environment);
        }
    }

    /** Removes an environment from the list. Its control plane and the sign-in kept for it are left as they are. */
    async remove(url: string): Promise<void> {
        const list = this.writable();
        const next = list.filter((saved) => !sameControlPlane(saved.url, url));
        if (next.length === list.length) {
            throw new Error('No saved environment has that address: another window may have removed it.');
        }
        await this.write(next);
    }
}

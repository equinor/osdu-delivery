/**
 * Why nobody is signed in, as a page says it to a person. The MCP server's own sentence is written for an assistant: it
 * carries the transport's error whole and names the tool to call next. A person is told what did not answer and where,
 * and the server's sentence stays in the log, where whoever looks for the cause finds all of it.
 */
export function sessionProblemText(problem: string | undefined, apiBaseUrl: string): string | null {
    if (problem === undefined || problem.trim() === '') {
        return null;
    }
    if (/could not reach|could not verify|error sending request|connect|timed out|dns error/i.test(problem)) {
        return `The control plane at ${apiBaseUrl} did not answer. Check the address, and that it is running.`;
    }
    // What is left is the control plane's own answer (a session past its limit, a credential it refused): its words,
    // without the instruction to an assistant that follows them.
    return problem.replace(/\s*(Call|Check|Use) (the )?`?[a-z_]+`?( tool)?[^.]*\.\s*$/i, '').trim();
}

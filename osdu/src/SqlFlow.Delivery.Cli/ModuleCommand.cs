using SqlFlow.Cli.Hosting;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// What a verb of the module reads of the command it runs for: the command's services, its parsed command line, where its
/// output goes, how it reports a usage error, and what cancels it. A handler takes it from the <see cref="CliVerbContext"/>
/// SQLFlow's verb runner gives it (<see cref="Of"/>), so the verb's own logic runs the same whether the command line or a
/// suite runs it.
/// </summary>
/// <param name="Services">The command's service provider.</param>
/// <param name="Arguments">The command line, parsed with the verb's value-taking options.</param>
/// <param name="Out">Where the verb's own output goes.</param>
/// <param name="Error">Where the verb notes what it did beside its output, such as a file it wrote.</param>
/// <param name="ReportUsageError">Prints a reason and the verb's usage, and answers the exit code; see <see cref="CliVerbContext.UsageError"/>.</param>
/// <param name="CancellationToken">Stops the verb.</param>
/// <param name="Input">
/// Where a verb that asks reads the answers: the console's input for the command line, a script's lines when that input is a
/// pipe or a file, or a suite's own text. Null for a command that answers nothing, which a verb that asks reads as no answer.
/// </param>
/// <param name="Interactive">Whether a person at a terminal answers <see cref="Input"/>, rather than a pipe, a file or nothing.</param>
internal sealed record ModuleCommand(
    IServiceProvider Services, CliArguments Arguments, TextWriter Out, TextWriter Error, Func<string?, int> ReportUsageError, CancellationToken CancellationToken,
    TextReader? Input = null, bool Interactive = false)
{
    /// <summary>True when <c>--json</c> was given: stdout then carries exactly one JSON document.</summary>
    public bool Json => Arguments.HasFlag("--json");

    /// <summary>Prints <paramref name="reason"/> and the verb's usage lines, and returns exit code 1.</summary>
    public int UsageError(string? reason = null) => ReportUsageError(reason);

    /// <summary>
    /// Writes <paramref name="prompt"/> and reads the line answering it, trimmed; null when nothing answers (no input, or its
    /// end reached), which a verb takes as the answer that changes nothing.
    /// </summary>
    public string? Ask(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Out.Write(prompt);
        Out.Flush();
        CancellationToken.ThrowIfCancellationRequested();
        var answer = Input?.ReadLine();
        CancellationToken.ThrowIfCancellationRequested();
        if (!Interactive)
        {
            // A script's answer is not echoed by a terminal, so the transcript shows it after the question.
            Out.WriteLine(answer ?? string.Empty);
        }

        return answer?.Trim();
    }

    /// <summary>The command a verb runner's context describes, answered on the console's input.</summary>
    public static ModuleCommand Of(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ModuleCommand(
            context.Services, context.Arguments, context.Out, context.Error, context.UsageError, context.CancellationToken,
            Console.In, !Console.IsInputRedirected);
    }
}

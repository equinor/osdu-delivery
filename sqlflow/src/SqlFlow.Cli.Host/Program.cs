using SqlFlow.Cli.Hosting;

namespace SqlFlow.Cli.Host;

/// <summary>
/// SQLFlow's own <c>sqlflow</c> command line: SQLFlow's verbs with no module. A product's program is this with its modules
/// (<see cref="CliHost"/>), and is named <c>sqlflow</c> too.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args) => CliHost.RunAsync(args);
}

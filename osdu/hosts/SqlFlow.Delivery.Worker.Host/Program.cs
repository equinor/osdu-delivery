using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.Worker.Host;

/// <summary>
/// An OSDU Delivery compute node: SQLFlow's node runtime, with the OSDU module installed so the node can execute the
/// delivery, retrieval, cache, assertion, dimension, inventory, removal and download flows (and the mapping, dictionary
/// and repository documents they read). It is the CLI's <c>worker</c> verb with nothing else on the command line, which keeps one drain loop, one set
/// of node options and one place where a run is executed, however the node was started.
/// </summary>
/// <remarks>
/// The container passes only the node's own options (<c>--pool</c>, <c>--poll-seconds</c>, <c>--drain-seconds</c>, and
/// <c>--url</c> / <c>--token</c> when the environment does not carry them); every credential, including the module
/// database connection, is resolved on the node from its own environment.
/// </remarks>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // The verb is this host's identity, not something a caller chooses: a node process runs the node loop and
        // nothing else, so a positional argument (a verb, or anything else) is refused, while an option's value, such as
        // the pool --pool names, is the node's.
        var (commandLine, refusal) = WorkerHostArguments.Of(args);
        if (commandLine is null)
        {
            Console.Error.WriteLine($"ERROR  {refusal}");
            return Task.FromResult(1);
        }

        return CliHost.RunAsync(commandLine, OsduDeliveryBranding.Product, new DeliveryCliModule());
    }
}

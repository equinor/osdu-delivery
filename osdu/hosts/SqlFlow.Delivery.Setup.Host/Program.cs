using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Setup;

namespace SqlFlow.Delivery.Setup.Host;

/// <summary>
/// <c>osdu-delivery-setup</c>: sets a checkout of OSDU Delivery up for a local run and checks it
/// (osdu/docs/reference/cli/dev.md). dev.bat and dev.sh build it from the checkout, which takes seconds since it carries
/// only the setup, once they have offered to install .NET on a machine without it. For what only the command line does (creating the catalog, registering the partition) it builds the
/// checkout's command line, in the background while the questions are asked.
/// </summary>
public sealed class Program
{
    private Program()
    {
    }

    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        var interrupts = 0;
        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            // The first Ctrl+C stops the setup, which writes nothing it has not finished; a second ends the process.
            if (Interlocked.Increment(ref interrupts) == 1)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            return await DevCommand.RunAsync(
                args, "osdu-delivery-setup", Console.Out, Console.Error, Directory.GetCurrentDirectory(), DevCommandLineKind.Checkout, cancel.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("CANCELLED  interrupted (Ctrl+C).").ConfigureAwait(false);
            return 130;
        }
        catch (SqlFlowException ex)
        {
            await Console.Error.WriteLineAsync($"ERROR  {SecretHygiene.RedactedMessage(ex)}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"ERROR  {SecretHygiene.RedactedMessage(ex)}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }
}

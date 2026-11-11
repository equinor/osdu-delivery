using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow template register</c> (osdu/docs/reference/cli/template-register.md): brings a partition's Schema service in step
/// with the custom kinds a repository's template folders hold. It reads the connection from an env file, finds every template
/// of a kind whose authority is not <c>osdu</c>, reads where each stands in the partition, shows what it would write, and
/// writes what the developer chooses, reading each back. Nothing is written without a person's answer or <c>--confirm</c>.
/// </summary>
internal static class DeliveryTemplateRegister
{
    /// <summary>The variable that names the partition, beside the connection's own (<see cref="PartitionConnection.Names"/>).</summary>
    public const string PartitionVariable = "OSDU_DATA_PARTITION";

    /// <summary>The operation code the requests' correlation id carries.</summary>
    private const string CorrelationOperation = "schemaregister";

    /// <summary>The variables the connection and the partition are read from, which the env file decides over the shell.</summary>
    public static IReadOnlyList<string> ConnectionVariables { get; } = [.. PartitionConnection.Names, PartitionVariable];

    private static readonly string[] AttentionStates =
    [
        SchemaRegistrationStates.Frozen, SchemaRegistrationStates.Shared, SchemaRegistrationStates.Conflict,
        SchemaRegistrationStates.Unsendable, SchemaRegistrationStates.Unavailable,
    ];

    /// <summary>The command line's run: the process environment, from the current folder.</summary>
    internal static Task<int> RegisterAsync(ModuleCommand context)
        => RegisterAsync(context, Environment.GetEnvironmentVariable, Directory.GetCurrentDirectory());

    /// <summary>
    /// A run reading the process environment through <paramref name="environment"/>, from <paramref name="currentDirectory"/>:
    /// what a suite gives so a run never reads or changes the environment other suites share.
    /// </summary>
    internal static async Task<int> RegisterAsync(ModuleCommand context, Func<string, string?> environment, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        var ct = context.CancellationToken;
        var args = context.Arguments;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var dryRun = args.HasFlag("--dry-run");
        var confirm = args.GetOption("--confirm")?.Trim();
        var publish = args.HasFlag("--publish");
        var all = args.HasFlag("--all");
        var named = args.GetOptions("--kind")
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dryRun && confirm is not null)
        {
            return context.UsageError("--dry-run writes nothing and --confirm writes without asking: give one of them.");
        }

        if (confirm is { Length: 0 })
        {
            return context.UsageError("--confirm takes the partition the schemas are written in, typed back.");
        }

        if (context.Json && !dryRun && confirm is null)
        {
            return context.UsageError("--json writes one JSON document and asks nothing: add --dry-run to see what would be written, or --confirm <partition> to write it.");
        }

        if (all && named.Count > 0)
        {
            return context.UsageError("--all takes every custom kind, and --kind names some: give one of them.");
        }

        if (args.Positional(3) is not null)
        {
            return context.UsageError("'sqlflow template register' reads one repository folder.");
        }

        var directory = args.Positional(2);
        var root = directory is null
            ? TemplateFolders.RepositoryTop(currentDirectory) ?? currentDirectory
            : Path.GetFullPath(Path.Combine(currentDirectory, directory));
        if (!Directory.Exists(root))
        {
            return context.UsageError($"'{directory}' is not a folder: name the repository whose template folders hold the custom schemas.");
        }

        foreach (var kind in named)
        {
            if (CustomTemplates.Parse(kind) is null)
            {
                return context.UsageError($"--kind '{kind}' is not a kind: authority:source:entityType:major.minor.patch.");
            }

            if (!CustomTemplates.IsCustom(kind))
            {
                return context.UsageError(
                    $"--kind {kind} is an OSDU kind, which the platform's shared schemas hold; a custom kind has an authority other than '{CustomTemplates.OsduAuthority}'.");
            }
        }

        RegisterEnvironment values;
        try
        {
            values = RegisterEnvironment.Read(args.GetOption("--env-file"), root, currentDirectory, environment);
        }
        catch (DeliveryException ex)
        {
            return Failed(context, ex.Message);
        }

        var partition = (args.GetOption("--partition") ?? values.Get(PartitionVariable))?.Trim();
        if (string.IsNullOrEmpty(partition))
        {
            return Failed(context, $"No partition to register in: name it with --partition, or set {PartitionVariable} in {values.Described}.");
        }

        if (!CacheScope.IsPartitionId(partition))
        {
            return Failed(context, $"'{partition}' is not a partition id.");
        }

        if (confirm is not null && !string.Equals(confirm, partition, StringComparison.OrdinalIgnoreCase))
        {
            return context.UsageError(
                $"--confirm names '{confirm}', and the schemas would be written in partition {partition}: type the partition back exactly to write without asking.");
        }

        var configured = new Dictionary<string, string>(StringComparer.Ordinal);
        if (PartitionConnection.Missing(configured, values.Get) is { Count: > 0 } missing)
        {
            return Failed(context,
                $"The connection to OSDU needs {string.Join(", ", missing)}, which {values.Described} does not set. "
                + "Add them to the env file ('sqlflow dev setup' asks for them).");
        }

        if (PartitionConnection.EndpointShown(configured, values.Get) is not { } platform)
        {
            return Failed(context, $"{PartitionConnection.UrlName} is not an absolute http or https URL: give the platform's base URL, without a path.");
        }

        var scan = TemplateFolders.Scan(root, ct);
        var custom = CustomTemplates.Of(scan);
        if (named.FirstOrDefault(k => !custom.Any(t => string.Equals(t.Kind, k, StringComparison.OrdinalIgnoreCase))) is { } absent)
        {
            return context.UsageError(custom.Count == 0
                ? $"--kind names {absent}, and no template folder under {root} holds a custom kind."
                : $"--kind names {absent}, which no template folder under {root} holds; it holds {string.Join(", ", custom.Select(t => t.Kind))}.");
        }

        var run = new RegisterRun(context, root, partition, platform, values, scan);
        run.Header();
        if (custom.Count == 0)
        {
            return run.NothingCustom();
        }

        using var correlation = OsduCorrelation.Begin(CorrelationOperation);
        run.CorrelationId = correlation.Id;

        // The connection's references resolve from the env file first: the run reads its input, and changes no environment.
        engine = engine with { Secrets = values.Over(engine.Secrets) };
        using var own = engine.Clients is null ? new TargetClients(engine.Time) : null;
        SchemaRegistrar registrar;
        ConnectionLease lease;
        try
        {
            lease = await (engine.Clients ?? own!).OpenAsync(engine, PartitionConnection.Of(partition), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Unreached(ex, ct))
        {
            return Failed(context, $"OSDU could not be reached for partition {partition} at {platform}: {HeaderRedaction.RedactMessage(ex.Message)}");
        }

        using (lease)
        {
            try
            {
                registrar = new SchemaRegistrar(await lease.ClientAsync(ct).ConfigureAwait(false), partition, engine.Time);
            }
            catch (Exception ex) when (Unreached(ex, ct))
            {
                return Failed(context, $"OSDU could not be reached for partition {partition} at {platform}: {HeaderRedaction.RedactMessage(ex.Message)}");
            }

            // Every version a family holds in the partition, read once per family. The first read is the first request, so
            // its failure is the connection's and stops the run with one message rather than one per kind.
            var families = new Dictionary<string, IReadOnlyList<SchemaListing>?>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in custom.Select(t => t.Family).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    families[family] = await registrar.FamilyAsync(family, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (Unreached(ex, ct))
                {
                    if (families.Count == 0)
                    {
                        return Failed(context, ReadRefused(ex, partition, platform));
                    }

                    families[family] = null;
                }
            }

            var rows = new List<RegisterRow>();
            foreach (var template in custom)
            {
                var item = await registrar.ReadAsync(template, families[template.Family], ct).ConfigureAwait(false);
                var selected = named.Count > 0 ? named.Contains(template.Kind, StringComparer.OrdinalIgnoreCase) : all || template.Latest;
                var proposed = !selected ? null
                    : publish && item.Actions.Contains(SchemaRegistrationActions.Publish) ? SchemaRegistrationActions.Publish
                    : item.Suggested;
                rows.Add(new RegisterRow(rows.Count + 1, item, selected, proposed));
            }

            run.Plan(rows);
            if (dryRun)
            {
                return run.Finish(rows, written: false);
            }

            if (confirm is not null)
            {
                foreach (var row in rows)
                {
                    row.Chosen = row.Proposed;
                }

                if (rows.All(r => r.Chosen is null))
                {
                    return run.Finish(rows, written: false);
                }
            }
            else if (!run.Choose(rows))
            {
                return run.Finish(rows, written: false);
            }

            await run.ApplyAsync(registrar, rows, ct).ConfigureAwait(false);
            return run.Finish(rows, written: true);
        }
    }

    /// <summary>Whether <paramref name="ex"/> says OSDU could not be reached or refused, rather than that the command was stopped.</summary>
    internal static bool Unreached(Exception ex, CancellationToken ct)
        => ex is SqlFlowException or HttpRequestException or TimeoutException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>Why the first read of the Schema service failed, with where to look.</summary>
    private static string ReadRefused(Exception ex, string partition, string platform)
    {
        var said = HeaderRedaction.RedactMessage(ex.Message);
        var hint = ex is OsduStatusException { StatusCode: 401 or 403 }
            ? $" Check {PartitionConnection.TokenUrlName}, {PartitionConnection.ClientIdName}, {PartitionConnection.ClientSecretName} and {PartitionConnection.ScopeName}, "
                + $"and that the client is in service.schema-service.viewers in partition {partition}."
            : $" Check {PartitionConnection.UrlName} (the platform's base URL) and the network path to it.";
        return $"OSDU's Schema service could not be read in partition {partition} at {platform}: {said}.{hint}";
    }

    private static int Failed(ModuleCommand context, string message)
    {
        context.Error.WriteLine($"ERROR  {message}");
        return 1;
    }

    /// <summary>Whether a kind is left needing a person: a state no write of this command can change.</summary>
    internal static bool NeedsAttention(SchemaRegistrationItem item) => AttentionStates.Contains(item.State, StringComparer.Ordinal);
}

/// <summary>One kind in a run: where it stands, whether it was selected, what is proposed and chosen, and what writing it came to.</summary>
internal sealed class RegisterRow(int number, SchemaRegistrationItem item, bool selected, string? proposed)
{
    public int Number { get; } = number;

    public SchemaRegistrationItem Item { get; } = item;

    /// <summary>Whether the run takes the kind: the latest of its family, every kind with <c>--all</c>, or the ones <c>--kind</c> names.</summary>
    public bool Selected { get; } = selected;

    /// <summary>The write proposed for it, or null when none is.</summary>
    public string? Proposed { get; } = proposed;

    /// <summary>The write the person chose, or null to leave it.</summary>
    public string? Chosen { get; set; }

    /// <summary>What writing it came to, or null when it was not written.</summary>
    public SchemaRegistrationOutcome? Outcome { get; set; }
}

/// <summary>The env file a run reads the connection from, its values, and what it noticed reading them.</summary>
/// <param name="File">The file, as a full path, or null when none was found and the process environment alone is read.</param>
/// <param name="Values">Every variable the file sets, the first of a name set twice; never written anywhere.</param>
/// <param name="Notes">What a developer should know: a variable the process environment holds otherwise, a variable set twice.</param>
/// <param name="Process">The process environment, read for a name the file does not set.</param>
internal sealed record RegisterEnvironment(string? File, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Notes, Func<string, string?> Process)
{
    /// <summary>How a message names where the values were read: the file and the process environment, or the process environment alone.</summary>
    public string Described => File is null
        ? $"the process environment (no {LocalEnvFile.RelativePath} was found above the current folder or the repository)"
        : $"{File} or the process environment";

    /// <summary>The connection's variables the file does not set, which the process environment gives.</summary>
    public IReadOnlyList<string> FromProcess => DeliveryTemplateRegister.ConnectionVariables
        .Where(name => !Values.ContainsKey(name) && !string.IsNullOrWhiteSpace(Process(name)))
        .ToList();

    /// <summary>The value of <paramref name="name"/> as written: the file's, else the process environment's; null when neither sets it.</summary>
    public string? Get(string name)
        => Values.TryGetValue(name, out var value) ? value
            : Process(name) is { } own && !string.IsNullOrWhiteSpace(own) ? own
            : null;

    /// <summary>A resolver answering <c>${env:NAME}</c> from the file before <paramref name="inner"/>, which resolves everything else.</summary>
    public ISecretResolver Over(ISecretResolver inner) => new EnvFileReferences(Values, inner);

    /// <summary>
    /// Reads the env file <paramref name="named"/> names, or the nearest <c>.sqlflow/env</c> above
    /// <paramref name="currentDirectory"/>, else above <paramref name="root"/>, without applying it: the file is the run's
    /// input, so its values are used where the process environment holds others too, which is noted for the connection's
    /// variables. Only names and line numbers are ever written; no value is.
    /// </summary>
    /// <exception cref="DeliveryException">The file named is not there, cannot be read, or holds a line that is not KEY=VALUE.</exception>
    public static RegisterEnvironment Read(string? named, string root, string currentDirectory, Func<string, string?> process)
    {
        ArgumentNullException.ThrowIfNull(process);
        string? file;
        if (named is not null)
        {
            file = Path.GetFullPath(Path.Combine(currentDirectory, named));
            if (!System.IO.File.Exists(file))
            {
                throw new DeliveryException($"--env-file names '{named}', which is not a file.");
            }
        }
        else
        {
            file = LocalEnvFile.FindNearest(currentDirectory) ?? LocalEnvFile.FindNearest(root);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();
        if (file is null)
        {
            return new RegisterEnvironment(null, values, notes, process);
        }

        LocalEnvFileContent content;
        try
        {
            content = LocalEnvFile.Read(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DeliveryException($"{file} could not be read: {ex.Message}", ex);
        }

        if (content.Problems.Count > 0)
        {
            // A problem never carries the line: a malformed line in a secrets file may be the secret itself.
            var first = content.Problems[0];
            throw new DeliveryException($"{file}({first.Line}): {first.Message}");
        }

        var connection = DeliveryTemplateRegister.ConnectionVariables.ToHashSet(StringComparer.Ordinal);
        foreach (var entry in content.Entries)
        {
            if (!values.TryAdd(entry.Name, entry.Value))
            {
                if (connection.Contains(entry.Name))
                {
                    notes.Add($"{entry.Name} is set again on line {entry.Line} of {file}; the first is used, as every command uses it.");
                }

                continue;
            }

            if (connection.Contains(entry.Name) && process(entry.Name) is { } own && !string.IsNullOrWhiteSpace(own)
                && !string.Equals(own, entry.Value, StringComparison.Ordinal))
            {
                notes.Add($"{entry.Name} is set to another value in the process environment (by the shell, or by a {LocalEnvFile.RelativePath} nearer the current folder); the one in {file} is used.");
            }
        }

        return new RegisterEnvironment(file, values, notes, process);
    }
}

/// <summary>
/// The references of a run whose env file is its input: <c>${env:NAME}</c> for a name the file sets is answered with the
/// file's value, and everything else, a file value that is itself a reference (<c>${keyvault:vault/secret}</c>) included, by
/// the resolver the command was given. Values do not chain: a file value <c>${env:OTHER}</c> is read from the process
/// environment, as a supplied value is (<see cref="SuppliedReferences"/>), so no two values naming each other can loop.
/// </summary>
internal sealed partial class EnvFileReferences(IReadOnlyDictionary<string, string> values, ISecretResolver inner) : ISecretResolver
{
    public string Resolve(string value)
        => Held(value) is { } held ? (IsReference(held) ? inner.Resolve(held) : held) : inner.Resolve(value);

    public async Task<string> ResolveAsync(string value, CancellationToken ct = default)
        => Held(value) is { } held
            ? IsReference(held) ? await inner.ResolveAsync(held, ct).ConfigureAwait(false) : held
            : await inner.ResolveAsync(value, ct).ConfigureAwait(false);

    /// <summary>The file's value for <paramref name="value"/> when it is exactly <c>${env:NAME}</c> and the file sets the name; null otherwise.</summary>
    private string? Held(string value)
        => value is not null && WholeEnvReference().Match(value) is { Success: true } match && values.TryGetValue(match.Groups["name"].Value, out var held)
            ? held
            : null;

    private static bool IsReference(string value) => value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}');

    [GeneratedRegex(@"^\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeEnvReference();
}

/// <summary>What a run writes to the developer, and the questions it asks: kept apart from the reads and writes it reports.</summary>
internal sealed class RegisterRun(
    ModuleCommand context, string root, string partition, string platform, RegisterEnvironment environment, TemplateFolderScan scan)
{
    /// <summary>How many times a question is asked again after an answer it does not take.</summary>
    private const int MaxAsks = 5;

    /// <summary>The most property changes shown under one kind; the rest are counted.</summary>
    private const int MaxChangesShown = 8;

    private TextWriter Out => context.Out;

    /// <summary>The correlation id every request of the run carries, to find them in the platform's logs.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Where the run reads from and writes to, before anything is asked of OSDU.</summary>
    public void Header()
    {
        foreach (var warning in scan.Warnings)
        {
            context.Error.WriteLine($"WARN   {warning}");
        }

        foreach (var note in environment.Notes)
        {
            context.Error.WriteLine($"NOTE   {note}");
        }

        if (context.Json)
        {
            return;
        }

        Out.WriteLine($"Custom schemas of {root}, against OSDU partition {partition}");
        Out.WriteLine($"  environment  {environment.File ?? "the process environment (no " + LocalEnvFile.RelativePath + " found)"}");
        if (environment.File is not null && environment.FromProcess.Count > 0)
        {
            Out.WriteLine($"               {string.Join(", ", environment.FromProcess)} from the process environment, which the file does not set");
        }

        Out.WriteLine($"  platform     {platform}");
        var templates = scan.Files.Where(f => f.IsTemplate).ToList();
        var custom = templates.Count(f => CustomTemplates.IsCustom(f.Kind!));
        Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  templates    {templates.Count} in {scan.Folders.Count} template folder(s), {custom} of a custom kind"));
    }

    /// <summary>A repository with no custom kind: said, and nothing asked of OSDU.</summary>
    public int NothingCustom()
    {
        var message = $"No template under {root} is of a custom kind (an authority other than '{CustomTemplates.OsduAuthority}'), so there is nothing to register.";
        if (context.Json)
        {
            Out.WriteLine(CanonicalJson.Pretty(Document([], written: false, message)));
        }
        else
        {
            Out.WriteLine();
            Out.WriteLine(message);
        }

        return 0;
    }

    /// <summary>Every custom kind: where it stands, what can be done, and what is proposed.</summary>
    public void Plan(IReadOnlyList<RegisterRow> rows)
    {
        if (context.Json)
        {
            return;
        }

        Out.WriteLine();
        foreach (var row in rows)
        {
            WriteRow(row);
            Out.WriteLine();
        }

        var proposed = rows.Where(r => r.Proposed is not null).ToList();
        Out.WriteLine(proposed.Count == 0
            ? "Nothing is proposed: every kind taken is in step, or needs you rather than a write."
            : $"Proposed in partition {partition}: {Proposals(proposed)}.");
        var attention = rows.Where(r => DeliveryTemplateRegister.NeedsAttention(r.Item)).ToList();
        if (attention.Count > 0)
        {
            Out.WriteLine($"Needs you: {string.Join(", ", attention.Select(r => $"{r.Number} ({StateText(r.Item)})"))}; what to do is under each.");
        }

        Out.WriteLine("register adds a schema in development, update replaces the one in development, publish makes it final.");
    }

    /// <summary>
    /// Asks what to write: the proposal whole, nothing, or kind by kind; then, for a publish, the word typed back. False when
    /// nothing is to be written, an answer the input never gave included.
    /// </summary>
    public bool Choose(IReadOnlyList<RegisterRow> rows)
    {
        var proposed = rows.Where(r => r.Proposed is not null).ToList();
        var choosable = rows.Where(r => r.Item.Actions.Count > 0).ToList();
        if (choosable.Count == 0)
        {
            Out.WriteLine("Nothing can be written.");
            return false;
        }

        var question = proposed.Count > 0
            ? $"Write the proposed in partition {partition}? [y]es, [n]o, [c]hoose kind by kind: "
            : "Nothing is proposed. [c]hoose kind by kind, or [n]o: ";
        string? decision = null;
        for (var i = 0; i < MaxAsks && decision is null; i++)
        {
            var answer = context.Ask(question)?.ToLowerInvariant();
            decision = answer switch
            {
                null or "n" or "no" or "q" or "quit" => "no",
                "y" or "yes" when proposed.Count > 0 => "yes",
                "" when proposed.Count > 0 => "yes",
                "c" or "choose" => "choose",
                _ => null,
            };
        }

        switch (decision)
        {
            case "yes":
                foreach (var row in proposed)
                {
                    row.Chosen = row.Proposed;
                }

                break;
            case "choose":
                if (!ChooseEach(choosable))
                {
                    foreach (var row in rows)
                    {
                        row.Chosen = null;
                    }

                    Out.WriteLine("Nothing was written: the input ended before every kind was answered.");
                    return false;
                }

                break;
            default:
                Out.WriteLine("Nothing was written.");
                return false;
        }

        // A publish is never written on one keystroke: the word is typed back, and anything else keeps the schema in development.
        if (rows.Any(r => r.Chosen == SchemaRegistrationActions.Publish) && !ConfirmPublish(rows))
        {
            foreach (var row in rows.Where(r => r.Chosen == SchemaRegistrationActions.Publish))
            {
                row.Chosen = row.Item.Suggested;
            }

            Out.WriteLine("Kept in development: nothing is published.");
        }

        if (rows.All(r => r.Chosen is null))
        {
            Out.WriteLine("Nothing was written.");
            return false;
        }

        return true;
    }

    /// <summary>Writes every chosen kind, in the plan's order, saying each outcome as it comes.</summary>
    public async Task ApplyAsync(SchemaRegistrar registrar, IReadOnlyList<RegisterRow> rows, CancellationToken ct)
    {
        var chosen = rows.Where(r => r.Chosen is not null).ToList();
        if (!context.Json)
        {
            Out.WriteLine();
            Out.WriteLine($"Writing {chosen.Count} schema(s) in partition {partition} (correlation id {CorrelationId}):");
        }

        foreach (var row in chosen)
        {
            row.Outcome = await registrar.ApplyAsync(row.Item, row.Chosen!, ct).ConfigureAwait(false);
            if (!context.Json)
            {
                var mark = row.Outcome.Done ? row.Outcome.Verified ? "OK      " : "SENT    " : "FAILED  ";
                Out.WriteLine($"  {mark}{row.Number}  {row.Item.Kind}");
                Out.WriteLine($"          {row.Outcome.Message}");
            }
        }
    }

    /// <summary>
    /// The run's last word and its exit code: 1 when a write failed; 2 when a kind the run took is left out of step, a dry
    /// run's or a confirmed run's (a run a person answered leaves out what they chose to); 0 otherwise.
    /// </summary>
    public int Finish(IReadOnlyList<RegisterRow> rows, bool written)
    {
        var failed = rows.Count(r => r.Outcome is { Done: false });
        var asked = !context.Arguments.HasFlag("--dry-run") && context.Arguments.GetOption("--confirm") is null;
        var outOfStep = rows.Count(r => r.Selected && OutOfStep(r));
        var message = written
            ? $"{rows.Count(r => r.Outcome is { Done: true })} written, {failed} failed."
            : context.Arguments.HasFlag("--dry-run") ? "Dry run: nothing was written." : "Nothing was written.";
        if (context.Json)
        {
            Out.WriteLine(CanonicalJson.Pretty(Document(rows, written, message)));
        }
        else if (written)
        {
            Out.WriteLine();
            Out.WriteLine(message);
        }
        else if (!asked)
        {
            Out.WriteLine(message);
        }

        return failed > 0 ? 1 : !asked && outOfStep > 0 ? 2 : 0;
    }

    /// <summary>Whether a row is left out of step: not written as proposed, and not in step with its file.</summary>
    private static bool OutOfStep(RegisterRow row)
        => row.Outcome is not { Done: true } && (row.Proposed is not null || DeliveryTemplateRegister.NeedsAttention(row.Item));

    /// <summary>Asks what to do with each kind that can be written; false when the input ended before every one was answered.</summary>
    private bool ChooseEach(IReadOnlyList<RegisterRow> choosable)
    {
        Out.WriteLine("For each kind: the letter of what to do, or Enter for the one in brackets.");
        foreach (var row in choosable)
        {
            var options = row.Item.Actions.Append("skip").ToList();
            var fallback = row.Proposed ?? "skip";
            var question = $"  {row.Number}  {row.Item.Kind} ({StateText(row.Item)}): {string.Join(", ", options.Select(o => $"[{o[0]}]{o[1..]}"))} [{fallback}]: ";
            string? picked = null;
            for (var i = 0; i < MaxAsks && picked is null; i++)
            {
                var answer = context.Ask(question)?.ToLowerInvariant();
                if (answer is null)
                {
                    return false;
                }

                picked = answer.Length == 0 ? fallback : options.FirstOrDefault(o => o.StartsWith(answer, StringComparison.Ordinal));
            }

            row.Chosen = picked is null or "skip" ? null : picked;
        }

        return true;
    }

    private bool ConfirmPublish(IReadOnlyList<RegisterRow> rows)
    {
        var publishing = rows.Where(r => r.Chosen == SchemaRegistrationActions.Publish).ToList();
        Out.WriteLine();
        Out.WriteLine($"Publishing makes a schema final: OSDU never changes a published schema and has no way to remove one, so a later change needs a new version of the kind. To publish: {string.Join(", ", publishing.Select(r => r.Item.Kind))}.");
        var answer = context.Ask($"Type publish to publish {publishing.Count} schema(s) in partition {partition}, or anything else to keep them in development: ");
        return string.Equals(answer, "publish", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteRow(RegisterRow row)
    {
        var item = row.Item;
        Out.WriteLine($"  {row.Number}  {item.Kind}{(item.Template.Latest ? "  latest" : string.Empty)}");
        var file = item.Template.File ?? item.Template.Files[0];
        Out.WriteLine(item.TemplateVersion is null
            ? $"     file      {file.Shown}"
            : $"     file      {file.Shown}, template version {item.TemplateVersion}");
        foreach (var copy in item.Template.Files.Where(f => !ReferenceEquals(f, file)))
        {
            Out.WriteLine($"     also      {copy.Shown}, template version {copy.Version}");
        }

        Out.WriteLine($"     OSDU      {item.Why}");
        var changes = item.Changes;
        for (var i = 0; i < Math.Min(changes.Count, MaxChangesShown); i++)
        {
            Out.WriteLine($"     {(i == 0 ? "changes" : "       ")}   {ChangeText(changes[i])}");
        }

        if (changes.Count > MaxChangesShown)
        {
            Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"               and {changes.Count - MaxChangesShown} more"));
        }

        foreach (var note in item.Notes)
        {
            Out.WriteLine($"     note      {note}");
        }

        if (item.Actions.Count > 0)
        {
            Out.WriteLine($"     can       {string.Join(", ", item.Actions)}");
        }

        Out.WriteLine(row.Proposed is not null
            ? $"     proposed  {row.Proposed}"
            : !row.Selected && item.Suggested is not null
                ? "     proposed  nothing: not the latest version of its family (choose it, or give --all or --kind)"
                : "     proposed  nothing");
    }

    private static string ChangeText(TemplateVariableChange change)
    {
        var impact = change.Impact.ToString().ToLowerInvariant();
        return change.Change switch
        {
            TemplateVariableChangeKind.Added => $"{change.Path}: only in the file ({impact})",
            TemplateVariableChangeKind.Removed => $"{change.Path}: only in OSDU ({impact})",
            _ => $"{change.Path}: {string.Join("; ", change.Fields.Select(f => $"{f.Field} {f.Before ?? "none"} -> {f.After ?? "none"}"))} ({impact})",
        };
    }

    private static string StateText(SchemaRegistrationItem item) => item.State switch
    {
        SchemaRegistrationStates.Unregistered => "not registered",
        SchemaRegistrationStates.InStep => item.Status is null ? "in step" : $"in step, {item.Status.ToLowerInvariant()}",
        SchemaRegistrationStates.Differs => "differs, in development",
        SchemaRegistrationStates.Frozen => $"differs, {item.Status?.ToLowerInvariant()}",
        SchemaRegistrationStates.Shared => "differs from the platform's shared schema",
        SchemaRegistrationStates.Conflict => "two schemas in the repository",
        SchemaRegistrationStates.Unsendable => "file cannot be sent",
        _ => "OSDU could not be read",
    };

    private static string Proposals(IEnumerable<RegisterRow> rows)
        => string.Join(", ", rows.GroupBy(r => r.Proposed!).Select(g => $"{g.Key} {string.Join(" ", g.Select(r => r.Number.ToString(CultureInfo.InvariantCulture)))}"));

    private JsonObject Document(IReadOnlyList<RegisterRow> rows, bool written, string message) => new()
    {
        ["repository"] = root,
        ["environment"] = environment.File,
        ["partition"] = partition,
        ["platform"] = platform,
        ["correlationId"] = CorrelationId,
        ["written"] = written,
        ["message"] = message,
        ["warnings"] = new JsonArray([.. scan.Warnings.Concat(environment.Notes).Select(w => (JsonNode)w)]),
        ["kinds"] = new JsonArray([.. rows.Select(r => (JsonNode)Row(r))]),
    };

    private static JsonObject Row(RegisterRow row)
    {
        var item = row.Item;
        return new JsonObject
        {
            ["number"] = row.Number,
            ["kind"] = item.Kind,
            ["family"] = item.Template.Family,
            ["version"] = item.Template.Version.ToString(),
            ["latest"] = item.Template.Latest,
            ["files"] = new JsonArray([.. item.Template.Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Shown, ["templateVersion"] = f.Version })]),
            ["templateVersion"] = item.TemplateVersion,
            ["state"] = item.State,
            ["status"] = item.Status,
            ["scope"] = item.Scope,
            ["serviceTemplateVersion"] = item.ServiceTemplateVersion,
            ["why"] = item.Why,
            ["changes"] = new JsonObject
            {
                ["count"] = item.Changes.Count,
                ["breaking"] = item.Count(TemplateChangeImpact.Breaking),
                ["additive"] = item.Count(TemplateChangeImpact.Additive),
                ["wording"] = item.Count(TemplateChangeImpact.Wording),
                ["listed"] = new JsonArray([.. item.Changes.Select(c => (JsonNode)new JsonObject
                {
                    ["path"] = c.Path,
                    ["change"] = c.Change.ToString().ToLowerInvariant(),
                    ["impact"] = c.Impact.ToString().ToLowerInvariant(),
                    ["fields"] = new JsonArray([.. c.Fields.Select(f => (JsonNode)new JsonObject { ["field"] = f.Field, ["osdu"] = f.Before, ["file"] = f.After })]),
                })]),
            },
            ["otherDifferences"] = new JsonArray([.. item.OtherDifferences.Select(p => (JsonNode)p)]),
            ["newer"] = new JsonArray([.. item.Newer.Select(p => (JsonNode)p)]),
            ["notes"] = new JsonArray([.. item.Notes.Select(p => (JsonNode)p)]),
            ["actions"] = new JsonArray([.. item.Actions.Select(p => (JsonNode)p)]),
            ["selected"] = row.Selected,
            ["proposed"] = row.Proposed,
            ["chosen"] = row.Chosen,
            ["outcome"] = row.Outcome is not { } outcome ? null : new JsonObject
            {
                ["action"] = outcome.Action,
                ["done"] = outcome.Done,
                ["verified"] = outcome.Verified,
                ["message"] = outcome.Message,
                ["steps"] = new JsonArray([.. outcome.Steps.Select(s => (JsonNode)new JsonObject { ["status"] = s.Status, ["httpStatus"] = s.HttpStatus })]),
                ["stateAfter"] = outcome.After?.State,
                ["statusAfter"] = outcome.After?.Status,
            },
        };
    }
}

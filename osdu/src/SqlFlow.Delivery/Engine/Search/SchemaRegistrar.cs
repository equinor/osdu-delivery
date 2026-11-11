using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>Where a kind of one's own stands in a partition's Schema service, set against the template file the repository holds.</summary>
public static class SchemaRegistrationStates
{
    /// <summary>The service holds no schema of the kind. Storage takes its records; the indexer cannot index them.</summary>
    public const string Unregistered = "unregistered";

    /// <summary>The service holds the file's schema.</summary>
    public const string InStep = "inStep";

    /// <summary>The service holds another schema of the kind, in development, which a write replaces.</summary>
    public const string Differs = "differs";

    /// <summary>The service holds another schema of the kind, published or obsolete, which never changes.</summary>
    public const string Frozen = "frozen";

    /// <summary>The platform's shared schemas hold another schema of the kind, which a partition cannot replace or hold beside it.</summary>
    public const string Shared = "shared";

    /// <summary>The repository holds two schemas of the kind, or names it in two cases, so it does not say which to register.</summary>
    public const string Conflict = "conflict";

    /// <summary>The file holds a schema the service would refuse as it stands, or no longer holds the template it was read as.</summary>
    public const string Unsendable = "unsendable";

    /// <summary>The service could not be asked, or refused to answer, so nothing is known of the kind.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>What can be done to bring a kind of one's own in step with its template file.</summary>
public static class SchemaRegistrationActions
{
    /// <summary>Register the file's schema, in development: a schema the service does not hold yet.</summary>
    public const string Register = "register";

    /// <summary>Replace the schema the service holds in development with the file's.</summary>
    public const string Update = "update";

    /// <summary>Make the file's schema the published one, which never changes again and is never removed.</summary>
    public const string Publish = "publish";
}

/// <summary>
/// One kind of one's own as the partition's Schema service holds it, set against the template file the repository holds
/// (osdu/docs/reference/cli/template-register.md): where it stands, what can be done, and what would bring it in step.
/// </summary>
public sealed record SchemaRegistrationItem
{
    /// <summary>The kind, with every file of the repository holding it.</summary>
    public required CustomTemplate Template { get; init; }

    /// <summary>Where it stands (<see cref="SchemaRegistrationStates"/>).</summary>
    public required string State { get; init; }

    /// <summary>Why it stands there, and what to do about it, as a person reads it.</summary>
    public required string Why { get; init; }

    /// <summary>The template version of the file's schema; null when the file could not be read.</summary>
    public string? TemplateVersion { get; init; }

    /// <summary>The status the service holds the schema in (<c>DEVELOPMENT</c>, <c>PUBLISHED</c>, <c>OBSOLETE</c>), or null when it holds none or did not say.</summary>
    public string? Status { get; init; }

    /// <summary>The scope the service holds the schema in (<c>INTERNAL</c>, the partition's own, or <c>SHARED</c>, the platform's), or null.</summary>
    public string? Scope { get; init; }

    /// <summary>The template version of the schema the service holds, read as a template file would be; null when it holds none.</summary>
    public string? ServiceTemplateVersion { get; init; }

    /// <summary>Every property that differs from the service's schema to the file's, the file's being the newer.</summary>
    public IReadOnlyList<TemplateVariableChange> Changes { get; init; } = [];

    /// <summary>Where the two schemas differ outside their properties (an <c>x-osdu</c> key, a definition), as JSON paths; listed when no property differs.</summary>
    public IReadOnlyList<string> OtherDifferences { get; init; } = [];

    /// <summary>The versions of the kind's family the service holds above the highest the repository holds; listed on the latest version only.</summary>
    public IReadOnlyList<string> Newer { get; init; } = [];

    /// <summary>What can be done (<see cref="SchemaRegistrationActions"/>), in the order a person is offered them.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>The write that brings the kind in step without publishing it, or null when none is needed or none can.</summary>
    public string? Suggested { get; init; }

    /// <summary>What else a person should know before writing: an index the change cannot reach, a status the service did not say.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The kind.</summary>
    public string Kind => Template.Kind;

    /// <summary>How many properties differ, by impact.</summary>
    public int Count(TemplateChangeImpact impact) => Changes.Count(c => c.Impact == impact);
}

/// <summary>One request a write sent: the status it asked for, and the HTTP status the service answered.</summary>
public sealed record SchemaRegistrationStep(string Status, int HttpStatus);

/// <summary>What one write of a kind came to.</summary>
public sealed record SchemaRegistrationOutcome
{
    /// <summary>The kind written.</summary>
    public required string Kind { get; init; }

    /// <summary>The action taken (<see cref="SchemaRegistrationActions"/>).</summary>
    public required string Action { get; init; }

    /// <summary>Whether the service now holds the file's schema in the status the action asked for.</summary>
    public required bool Done { get; init; }

    /// <summary>Whether the service was read back after writing, so <see cref="Done"/> is what it answers rather than what it accepted.</summary>
    public required bool Verified { get; init; }

    /// <summary>What happened, as a person reads it: the service's own words when it refused, with what to do.</summary>
    public required string Message { get; init; }

    /// <summary>Each request sent, in order.</summary>
    public IReadOnlyList<SchemaRegistrationStep> Steps { get; init; } = [];

    /// <summary>Where the kind stands once written, read back from the service; null when it could not be read.</summary>
    public SchemaRegistrationItem? After { get; init; }
}

/// <summary>
/// Registers a repository's kinds of one's own with a partition's Schema service (osdu/specs/core/schema_service/openapi.yaml,
/// <c>PUT /schema</c>), from the template files the repository holds: reads where each kind stands, and writes the file's
/// schema as a person chose, then reads it back.
/// </summary>
/// <remarks>
/// <para>
/// Every write is <c>PUT /schema</c> (upsertSchema), which creates a schema in development, replaces one in development, or
/// moves one from development to published. A new schema cannot be created published through it, so publishing a kind the
/// service does not hold registers it in development first; a refusal of the second step leaves it in development, which
/// can still be changed. A published or obsolete schema is never written: the service refuses any change to it
/// (schema-service SchemaService.java, update), and no route removes a schema.
/// </para>
/// <para>
/// What the service stores is the schema re-serialised (members holding null dropped, key order not kept), so the two are
/// compared by their template versions, the hash of the canonical form, which neither changes; property by property when
/// they differ. A file is checked before it is sent as the service checks it: a local reference must end in the name of a
/// definition at the top of the schema (schema-service SchemaResolver.java).
/// </para>
/// </remarks>
public sealed class SchemaRegistrar
{
    /// <summary>The most JSON paths <see cref="SchemaRegistrationItem.OtherDifferences"/> lists.</summary>
    public const int MaxOtherDifferences = 5;

    /// <summary>The most references a refusal of a file names.</summary>
    private const int MaxReferenceProblems = 5;

    private const string Development = SchemaServiceReader.Development;
    private const string Published = SchemaServiceReader.Published;
    private const string SharedScope = "SHARED";

    private static readonly HashSet<int> Refusals = [400, 401, 403, 404, 405, 409, 412, 415, 422];

    private readonly OsduHttpClient _client;
    private readonly SchemaServiceReader _reader;
    private readonly string _path;
    private readonly string _partition;

    /// <param name="client">The partition's connection.</param>
    /// <param name="partition">The partition, as messages name it.</param>
    /// <param name="time">The clock a read schema is stamped with.</param>
    /// <param name="path">The Schema service's schema path; its default otherwise.</param>
    public SchemaRegistrar(OsduHttpClient client, string partition, TimeProvider time, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(time);
        _client = client;
        _partition = partition;
        _path = string.IsNullOrWhiteSpace(path) ? SchemaServiceReader.DefaultPath : path.TrimEnd('/');
        _reader = new SchemaServiceReader(client, time, _path);
    }

    /// <summary>
    /// Every version of <paramref name="family"/> the service holds, in every status and scope. Throws what the service
    /// answered when it could not be asked, so a caller tells a partition it cannot reach from one that holds nothing.
    /// </summary>
    public Task<IReadOnlyList<SchemaListing>> FamilyAsync(string family, CancellationToken ct = default) => _reader.FamilyAsync(family, ct);

    /// <summary>
    /// Where <paramref name="template"/> stands in the partition. <paramref name="family"/> is what <see cref="FamilyAsync"/>
    /// answered for its family, or null when the listing could not be read, in which case the kind's own status is asked.
    /// Never throws for what the service answers: a service that cannot be read is <see cref="SchemaRegistrationStates.Unavailable"/>.
    /// </summary>
    public async Task<SchemaRegistrationItem> ReadAsync(CustomTemplate template, IReadOnlyList<SchemaListing>? family, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Conflict is { } conflict)
        {
            return new SchemaRegistrationItem
            {
                Template = template,
                State = SchemaRegistrationStates.Conflict,
                Why = $"Not written: {conflict}",
            };
        }

        SchemaSnapshot file;
        try
        {
            file = TemplateFolders.Load(template.File!);
        }
        catch (DeliveryException ex)
        {
            return Unsendable(template, null, ex.Message);
        }

        if (ReferenceProblems(file.Root) is { Count: > 0 } problems)
        {
            return Unsendable(template, file.Version,
                $"{template.File!.Shown} cannot be sent as it stands: the Schema service reads a local reference by the name after its last '/', "
                + $"which must be a definition at the top of the schema, and {string.Join("; ", problems)}. Bundle it again with 'sqlflow template import'.");
        }

        SchemaServiceRead read;
        SchemaListing? listing;
        var notes = new List<string>();
        try
        {
            read = await _reader.ReadAsync(template.Kind, ct).ConfigureAwait(false);
            listing = family is null
                ? await StatusAsync(template.Kind, notes, ct).ConfigureAwait(false)
                : family.FirstOrDefault(s => string.Equals(s.Id, template.Kind, StringComparison.Ordinal));
        }
        catch (Exception ex) when (SchemaCrossCheck.Unreached(ex, ct))
        {
            return new SchemaRegistrationItem
            {
                Template = template,
                State = SchemaRegistrationStates.Unavailable,
                TemplateVersion = file.Version,
                Why = $"The Schema service of partition {_partition} could not be read for this kind: {HeaderRedaction.RedactMessage(ex.Message)}",
            };
        }

        var newer = template.Latest && family is not null
            ? family.Where(s => KindVersion.Parse(s.Version) is { } v && v > template.Version).Select(s => s.Id).ToList()
            : [];
        if (newer.Count > 0)
        {
            notes.Add($"OSDU already holds {string.Join(", ", newer)}, newer than any version of the family the repository holds.");
        }

        if (read.Unresolved.Count > 0)
        {
            notes.Add($"The schema OSDU holds refers to what it does not hold: {string.Join("; ", read.Unresolved.Select(HeaderRedaction.RedactMessage))}.");
        }

        return Decide(template, file, read.Schema, listing, newer, notes);
    }

    /// <summary>
    /// Writes <paramref name="item"/>'s file as <paramref name="action"/> asks, then reads the kind back. Never throws for
    /// what the service answers: a refusal is an outcome that says what the service said and what to do.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="action"/> is not one <paramref name="item"/> offers.</exception>
    public async Task<SchemaRegistrationOutcome> ApplyAsync(SchemaRegistrationItem item, string action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (!item.Actions.Contains(action, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{action}' is not what can be done to {item.Kind}, which is {item.State}: {(item.Actions.Count == 0 ? "nothing can" : string.Join(", ", item.Actions))}.",
                nameof(action));
        }

        SchemaSnapshot file;
        try
        {
            // Read again, so a file changed since the plan was shown is never sent for the version the person agreed to.
            file = TemplateFolders.Load(item.Template.File!);
        }
        catch (DeliveryException ex)
        {
            return new SchemaRegistrationOutcome { Kind = item.Kind, Action = action, Done = false, Verified = false, Message = $"Not written: {ex.Message}" };
        }

        var target = action == SchemaRegistrationActions.Publish ? Published : Development;
        var statuses = action == SchemaRegistrationActions.Publish && item.State == SchemaRegistrationStates.Unregistered
            ? new[] { Development, Published }
            : [target];

        var steps = new List<SchemaRegistrationStep>();
        string? refusal = null;
        foreach (var status in statuses)
        {
            var (code, problem) = await PutAsync(item.Template, file, status, ct).ConfigureAwait(false);
            steps.Add(new SchemaRegistrationStep(status, code));
            if (problem is not null)
            {
                refusal = steps.Count > 1
                    ? $"{item.Kind} was registered in development, and publishing it was refused: {problem}"
                    : problem;
                break;
            }
        }

        // Read back whatever was answered: a write the service took while its answer was lost reads as done, and one refused
        // after an earlier step reads as where it was left.
        var after = await ReadAsync(item.Template, null, ct).ConfigureAwait(false);
        var verified = after.State != SchemaRegistrationStates.Unavailable;
        var inStep = verified && after.State == SchemaRegistrationStates.InStep;

        // A listing that did not say the status leaves a write the service took, and holds as sent, as done.
        var done = inStep && (string.Equals(after.Status, target, StringComparison.OrdinalIgnoreCase) || (after.Status is null && refusal is null));
        string message;
        if (done)
        {
            message = Completed(action, item.Kind, refusal);
        }
        else if (!verified)
        {
            message = refusal ?? $"OSDU accepted {Describe(action)} of {item.Kind} (HTTP {steps[^1].HttpStatus}), and reading it back failed: {after.Why}";
        }
        else
        {
            message = refusal ?? NotAsAsked(after, target);
        }

        return new SchemaRegistrationOutcome
        {
            Kind = item.Kind,
            Action = action,

            // Accepted and not read back: what the service answered is all that is known, and the outcome says it was not verified.
            Done = done || (!verified && refusal is null),
            Verified = verified,
            Message = message,
            Steps = steps,
            After = verified ? after : null,
        };
    }

    /// <summary>
    /// Where a kind stands, from its file and what the service holds of it: the decision every read and every read back
    /// comes to, kept apart from the service so each case is checked alone.
    /// </summary>
    internal static SchemaRegistrationItem Decide(
        CustomTemplate template, SchemaSnapshot file, SchemaSnapshot? service, SchemaListing? listing, IReadOnlyList<string> newer, IReadOnlyList<string> notes)
    {
        var item = new SchemaRegistrationItem
        {
            Template = template,
            State = SchemaRegistrationStates.Unregistered,
            Why = string.Empty,
            TemplateVersion = file.Version,
            Status = listing?.Status,
            Scope = listing?.Scope,
            ServiceTemplateVersion = service?.Version,
            Newer = newer,
            Notes = notes,
        };

        if (service is null)
        {
            return item with
            {
                Why = "OSDU holds no schema of this kind. Storage takes its records all the same, but the indexer cannot index them, "
                    + "so search, the explorer and assertions do not find them by their data until the kind is registered.",
                Actions = [SchemaRegistrationActions.Register, SchemaRegistrationActions.Publish],
                Suggested = SchemaRegistrationActions.Register,
            };
        }

        var shared = string.Equals(listing?.Scope, SharedScope, StringComparison.OrdinalIgnoreCase);
        var status = listing?.Status;
        var development = string.Equals(status, Development, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(file.Version, service.Version, StringComparison.Ordinal))
        {
            return item with
            {
                State = SchemaRegistrationStates.InStep,
                Why = shared ? "The platform's shared schemas hold this schema."
                    : status is null ? "OSDU holds this schema; its listing did not say in which status."
                    : development ? "OSDU holds this schema in development: it can still be replaced, or published to make it final."
                    : $"OSDU holds this schema, {status.ToLowerInvariant()}.",
                Actions = development && !shared ? [SchemaRegistrationActions.Publish] : [],
            };
        }

        var changes = TemplateComparer.Compare(OsduTemplate.From(service), OsduTemplate.From(file)).Changes
            .OrderByDescending(c => c.Impact)
            .ToList();
        var other = changes.Count == 0 ? JsonDifferences(service.Root, file.Root, MaxOtherDifferences) : [];
        var what = changes.Count > 0
            ? $"{changes.Count} propert{(changes.Count == 1 ? "y differs" : "ies differ")} from it to the file ({Impacts(changes)})"
            : $"they differ outside the template's properties, at {string.Join(", ", other)}";
        item = item with { Changes = changes, OtherDifferences = other };

        if (shared)
        {
            return item with
            {
                State = SchemaRegistrationStates.Shared,
                Why = $"The platform's shared schemas hold another schema of this kind: {what}. A partition cannot hold a schema of a kind the "
                    + "platform shares (the Schema service refuses it as present in another tenant): give the kind an authority or a version of its own.",
            };
        }

        if (status is not null && !development)
        {
            var bump = changes.Any(c => c.Impact == TemplateChangeImpact.Breaking)
                ? "a higher major version, since the Schema service refuses breaking changes within one"
                : "a higher version";
            return item with
            {
                State = SchemaRegistrationStates.Frozen,
                Why = $"OSDU holds another schema of this kind, {status.ToLowerInvariant()}, which never changes: {what}. "
                    + $"Give the file's kind {bump} in x-osdu-schema-source (and the file a name to match), and register that.",
            };
        }

        var typeChanges = changes
            .Where(c => c.Change == TemplateVariableChangeKind.Changed && c.Fields.Any(f => f.Field is "type" or "nested"))
            .Select(c => c.Path)
            .ToList();
        var extra = notes.ToList();
        if (typeChanges.Count > 0)
        {
            var listed = $"{string.Join(", ", typeChanges.Take(MaxOtherDifferences))}{(typeChanges.Count > MaxOtherDifferences ? $" and {typeChanges.Count - MaxOtherDifferences} more" : string.Empty)}";
            extra.Add((typeChanges.Count == 1 ? $"The type of {listed} changes. " : $"The types of {listed} change. ")
                + "OSDU's indexer cannot change the type of a field it has indexed, so the kind's index keeps the old mapping "
                + "until an administrator reindexes the kind with force_clean.");
        }

        if (status is null)
        {
            extra.Add("OSDU's listing did not say the schema's status. A write to a published or obsolete schema is refused by the Schema service itself.");
        }

        return item with
        {
            State = SchemaRegistrationStates.Differs,
            Why = $"OSDU holds another schema of this kind{(development ? " in development" : string.Empty)}: {what}.",
            Actions = [SchemaRegistrationActions.Update, SchemaRegistrationActions.Publish],
            Suggested = SchemaRegistrationActions.Update,
            Notes = extra,
        };
    }

    /// <summary>
    /// Why the service would refuse <paramref name="schema"/>'s references, each as a clause naming the reference: a local
    /// reference whose name after its last '/' is no definition at the top of the schema, or a reference to another schema,
    /// which a template file holds none of. Empty when the service takes them all.
    /// </summary>
    internal static IReadOnlyList<string> ReferenceProblems(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var definitions = schema["definitions"] as JsonObject;
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<JsonNode?>();
        pending.Push(schema);
        while (pending.Count > 0 && problems.Count < MaxReferenceProblems)
        {
            switch (pending.Pop())
            {
                case JsonObject node:
                    foreach (var (name, value) in node)
                    {
                        if (name == "$ref" && value is JsonValue reference && reference.TryGetValue<string>(out var text) && seen.Add(text))
                        {
                            if (text.StartsWith('#'))
                            {
                                var target = text[(text.LastIndexOf('/') + 1)..];
                                if (definitions is null || !definitions.ContainsKey(target))
                                {
                                    problems.Add(definitions is null
                                        ? $"'{text}' refers into the schema, which has no 'definitions'"
                                        : $"'{text}' names '{target}', which is not one of its definitions");
                                }
                            }
                            else if (!text.StartsWith("https", StringComparison.Ordinal))
                            {
                                problems.Add($"'{text}' refers to a schema outside the file, which OSDU would have to hold already");
                            }
                        }
                        else
                        {
                            pending.Push(value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var element in array)
                    {
                        pending.Push(element);
                    }

                    break;
            }
        }

        return problems;
    }

    /// <summary>
    /// The JSON paths where <paramref name="before"/> and <paramref name="after"/> differ once both are canonical (members
    /// holding null dropped, keys in ordinal order), at most <paramref name="max"/>, outermost first.
    /// </summary>
    internal static IReadOnlyList<string> JsonDifferences(JsonNode? before, JsonNode? after, int max)
    {
        var found = new List<string>();
        Walk(CanonicalJson.Normalize(before), CanonicalJson.Normalize(after), "$");
        return found;

        void Walk(JsonNode? a, JsonNode? b, string path)
        {
            if (found.Count >= max)
            {
                return;
            }

            switch (a, b)
            {
                case (JsonObject x, JsonObject y):
                    foreach (var key in x.Select(p => p.Key).Union(y.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    {
                        Walk(x[key], y[key], $"{path}{Step(key)}");
                    }

                    break;
                case (JsonArray x, JsonArray y) when x.Count == y.Count:
                    for (var i = 0; i < x.Count; i++)
                    {
                        Walk(x[i], y[i], string.Create(CultureInfo.InvariantCulture, $"{path}[{i}]"));
                    }

                    break;
                default:
                    if (!string.Equals(a?.ToJsonString(), b?.ToJsonString(), StringComparison.Ordinal))
                    {
                        found.Add(path);
                    }

                    break;
            }
        }

        static string Step(string key)
            => key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? $".{key}" : $"['{key.Replace("'", "\\'", StringComparison.Ordinal)}']";
    }

    /// <summary>The kind's own status and scope, asked when its family could not be listed; null with a note when that fails too.</summary>
    private async Task<SchemaListing?> StatusAsync(string kind, List<string> notes, CancellationToken ct)
    {
        try
        {
            return await _reader.InfoAsync(kind, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (SchemaCrossCheck.Unreached(ex, ct))
        {
            notes.Add($"OSDU's listing could not be read for the schema's status: {HeaderRedaction.RedactMessage(ex.Message)}");
            return null;
        }
    }

    /// <summary>
    /// One <c>PUT /schema</c> of <paramref name="file"/> in <paramref name="status"/>: the HTTP status answered, and why it was
    /// refused (the service's words, with what to do), or null when it was taken.
    /// </summary>
    private async Task<(int Code, string? Problem)> PutAsync(CustomTemplate template, SchemaSnapshot file, string status, CancellationToken ct)
    {
        var parts = template.Kind.Split(':');
        var body = new JsonObject
        {
            ["schemaInfo"] = new JsonObject
            {
                ["schemaIdentity"] = new JsonObject
                {
                    ["authority"] = parts[0],
                    ["source"] = parts[1],
                    ["entityType"] = parts[2],
                    ["schemaVersionMajor"] = template.Version.Major,
                    ["schemaVersionMinor"] = template.Version.Minor,
                    ["schemaVersionPatch"] = template.Version.Patch,
                },
                ["status"] = status,
            },
            ["schema"] = file.Root.DeepClone(),
        };

        HttpFetchResult result;
        try
        {
            // The same body sent twice leaves the same schema, so a request whose answer was lost may be sent again.
            result = await _client.SendJsonAsync(HttpMethod.Put, _client.Url(_path), body, Refusals, ct, idempotent: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (SchemaCrossCheck.Unreached(ex, ct))
        {
            var code = ex is OsduStatusException statusError ? statusError.StatusCode : 0;
            return (code, $"the Schema service could not be written: {HeaderRedaction.RedactMessage(ex.Message)}");
        }

        var answered = (int)result.Status;
        return answered is >= 200 and < 300 ? (answered, null) : (answered, Refused(answered, ServiceMessage(result.Body), template.Kind));
    }

    /// <summary>A refusal of a write as a person reads it: the service's words, and what they mean for this kind.</summary>
    private string Refused(int code, string said, string kind)
    {
        var hint = code switch
        {
            401 => $" Reading the Schema service worked with the same client, so it is most likely not in service.schema-service.editors in partition {_partition}: "
                + "ask the partition's administrator to add it to that group.",
            403 => $" The client may not write schemas in partition {_partition}: ask the partition's administrator to add it to service.schema-service.editors.",
            _ when said.Contains("another tenant", StringComparison.OrdinalIgnoreCase)
                => $" The platform's shared schemas hold {kind}, so a partition cannot hold it: give the kind an authority or a version of its own.",
            // The service spells it "developement" (schema-service SchemaConstants.java); the right spelling is matched too.
            _ when said.Contains("developement stage", StringComparison.OrdinalIgnoreCase) || said.Contains("development stage", StringComparison.OrdinalIgnoreCase)
                => " OSDU holds the schema published or obsolete now, which never changes: give the file's kind a higher version and register that.",
            _ when said.Contains("Breaking changes", StringComparison.OrdinalIgnoreCase)
                => " The Schema service compares a schema with the nearest versions of its family in the same major version.",
            _ => string.Empty,
        };
        return $"OSDU refused it (HTTP {code}): {said}.{hint}";
    }

    /// <summary>
    /// What a refusal's body says: the Schema service's <c>error.message</c>, an <c>AppError</c>'s <c>message</c>, or the text
    /// itself, shortened and with anything that looks like a credential redacted.
    /// </summary>
    internal static string ServiceMessage(byte[] body)
    {
        const int MaxText = 600;
        string? message = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject answer)
            {
                message = (answer["error"] as JsonObject)?["message"]?.GetValue<string>()
                    ?? answer["message"]?.GetValue<string>()
                    ?? answer["reason"]?.GetValue<string>();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Not the JSON the service answers with: its text is what it said.
        }

        message ??= Encoding.UTF8.GetString(body);
        message = message.Trim().TrimEnd('.');
        if (message.Length == 0)
        {
            return "no reason given";
        }

        return HeaderRedaction.RedactMessage(message.Length > MaxText ? message[..MaxText] + "..." : message);
    }

    private static SchemaRegistrationItem Unsendable(CustomTemplate template, string? version, string why) => new()
    {
        Template = template,
        State = SchemaRegistrationStates.Unsendable,
        TemplateVersion = version,
        Why = why,
    };

    private static string Impacts(IReadOnlyList<TemplateVariableChange> changes)
        => string.Join(", ", new[] { TemplateChangeImpact.Breaking, TemplateChangeImpact.Additive, TemplateChangeImpact.Wording }
            .Select(impact => (Impact: impact, Count: changes.Count(c => c.Impact == impact)))
            .Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {p.Impact.ToString().ToLowerInvariant()}"));

    private static string Describe(string action) => action switch
    {
        SchemaRegistrationActions.Register => "the registration",
        SchemaRegistrationActions.Update => "the update",
        _ => "the publication",
    };

    private string Completed(string action, string kind, string? refusal)
    {
        var done = action switch
        {
            SchemaRegistrationActions.Register => $"registered {kind} in development in partition {_partition}",
            SchemaRegistrationActions.Update => $"replaced the schema of {kind} in development in partition {_partition}",
            _ => $"published {kind} in partition {_partition}",
        };

        // A refusal the read back contradicts was an answer lost on the way: the service holds what was asked.
        return refusal is null ? $"{char.ToUpperInvariant(done[0])}{done[1..]}." : $"{char.ToUpperInvariant(done[0])}{done[1..]}: the service holds it as asked, though it answered: {refusal}";
    }

    private static string NotAsAsked(SchemaRegistrationItem after, string target)
        => after.State == SchemaRegistrationStates.InStep
                ? $"OSDU accepted the write, and holds the file's schema {after.Status?.ToLowerInvariant() ?? "in a status its listing did not say"} rather than {target.ToLowerInvariant()}."
                : $"OSDU accepted the write, and reading it back says: {after.Why}";
}

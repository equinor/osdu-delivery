using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Documents;

// The DDMSs a delivery flow declares under target.ddms (osdu/docs/reference/flow/ddms.md, target.ddms;
// osdu/docs/reference/flow/ddms-services.md): each one's name, root and shape, the settings of its shape (Well
// Delivery, a production historian, Reservoir Management, a Seismic Store), and the Register service id it is
// looked up under.
internal static partial class FlowMapper
{
    /// <summary>
    /// The DDMSs a flow declares under <c>target.ddms</c> (osdu/docs/reference/flow/ddms.md, "target.ddms"), in
    /// document order. Each has a name, a root under the endpoint, a shape and the collections it serves (the shape's
    /// own when it lists none). No entity type is served by two of them, since a record goes to one DDMS. A DDMS
    /// without a root makes the endpoint that DDMS itself, so it is the only DDMS such a flow reaches; a source with
    /// interfaces reaches every service under the platform endpoint, so each of its DDMSs names its root.
    /// </summary>
    private static IReadOnlyList<DdmsService> MapDdms(
        OrderedDictionary<string, DdmsYaml?>? declared, DeliveryProtocol protocol, bool interfaceForm, string? ddmsRoot, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (!DeliveryProtocols.ReachesDdms(protocol))
        {
            throw new FlowValidationException($"{source}: target.ddms declares the DDMSs the ddms route delivers to, and this flow's protocol is {DeliveryProtocols.Name(protocol)}. Remove target.ddms.");
        }

        if (declared.Count == 0)
        {
            throw new FlowValidationException($"{source}: target.ddms declares no DDMS. Name each DDMS the flow delivers to under it, or remove it.");
        }

        var services = new List<DdmsService>(declared.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var servedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in declared)
        {
            var name = key?.Trim() ?? string.Empty;
            if (!DdmsCatalog.IsName(name))
            {
                throw new FlowValidationException($"{source}: target.ddms names a DDMS '{name}'; a DDMS name is a letter followed by letters, digits, '_' and '-', at most 64 characters.");
            }

            if (!names.Add(name))
            {
                throw new FlowValidationException($"{source}: target.ddms declares '{name}' more than once (DDMS names are compared ignoring case).");
            }

            var at = $"target.ddms.{name}";
            var ddms = value ?? new DdmsYaml();
            var shape = ParseEnum(ddms.Shape, DdmsShape.WellboreDdmsV3, at + ".shape", source);
            var root = MapDdmsRoot(ddms.Root, at + ".root", source);
            var registration = MapRegistration(ddms, shape, at, source);
            if (root is null && shape == DdmsShape.ProductionTimeSeriesV1)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.root is required. The historian's records are written through Storage and its points through its ingestion service, so the flow's endpoint is "
                    + $"the platform both are under; say where the ingestion service is under it (usually {DdmsCatalog.UsualRoot(shape)}).");
            }

            if (root is null && shape == DdmsShape.SeismicStoreV3)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.root is required. A dataset's record is read and removed through Storage, so the flow's endpoint is the platform Seismic Store is under; "
                    + $"say where Seismic Store is under it, version path included (usually {DdmsCatalog.UsualRoot(shape)}, or /seistore-svc/api/v3 on Azure).");
            }

            if (root is null && shape == DdmsShape.ReservoirManagement)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.root is required. A header record is written, read and removed through Storage, so the flow's endpoint is the platform the "
                    + "Reservoir Management DDMS is under; say where the service is under it (its project names no prefix, so it is the deployment's).");
            }

            if (root is null && registration is null && interfaceForm)
            {
                var usual = DdmsCatalog.UsualRoot(shape) is { } known
                    ? $"a DDMS of the {DdmsCatalog.ShapeName(shape)} shape is usually deployed under {known}"
                    : $"a DDMS of the {DdmsCatalog.ShapeName(shape)} shape is deployed under the prefix its deployment gives it";
                throw new FlowValidationException(
                    $"{source}: {at}.root is required. The source's endpoint is the platform its interfaces reach every service under, so say where the DDMS is under it "
                    + $"({usual})"
                    + (shape == DdmsShape.WellboreDdmsV3 ? ", or name its registration in the Register service with register." : "."));
            }

            // A registered DDMS whose collections the flow leaves out serves what its registration says, once it is read.
            var collections = registration is not null && ddms.Collections is null ? [] : MapDdmsCollections(ddms.Collections, shape, at, source);
            var settings = MapWellDelivery(ddms, shape, at, source);
            var timeSeries = MapTimeSeries(ddms, shape, at, source);
            var seismic = MapSeismicStore(ddms, shape, at, source);
            var reservoirManagement = MapReservoirManagement(ddms, shape, at, source);
            foreach (var collection in collections)
            {
                if (!servedBy.TryAdd(collection.EntityType, name))
                {
                    throw new FlowValidationException(
                        $"{source}: target.ddms serves {collection.EntityType} from both '{servedBy[collection.EntityType]}' and '{name}'. A record goes to one DDMS, "
                        + "so list the collections of one of them under its collections, leaving that entity type out.");
                }
            }

            services.Add(new DdmsService(name, root, shape, collections)
            {
                Registration = registration,
                DeclaresCollections = ddms.Collections is not null,
                WellDelivery = settings,
                TimeSeries = timeSeries,
                SeismicStore = seismic,
                ReservoirManagement = reservoirManagement,
            });
        }

        if (services.FirstOrDefault(s => s.Root is null && s.Registration is null) is { } unrooted && (services.Count > 1 || ddmsRoot is not null))
        {
            throw new FlowValidationException(
                $"{source}: target.ddms.{unrooted.Name} names no root, which makes the flow's endpoint that DDMS itself, so the flow reaches no other DDMS"
                + (ddmsRoot is null ? string.Empty : " and target.protocolOptions.ddmsRoot places none under it")
                + ". Give every DDMS its root under the endpoint, or declare that one alone.");
        }

        return services;
    }

    /// <summary>
    /// The deployment settings of a declared Well Delivery DDMS, or null for any other shape, which takes none of them
    /// (osdu/specs/well-delivery-ddms/INTEGRATION.md section 8: whether the deployment copies entities into Storage, the
    /// provider it runs on, and how many writes it takes at a time).
    /// </summary>
    private static WellDeliverySettings? MapWellDelivery(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (shape != DdmsShape.WellDeliveryV1)
        {
            var misplaced = new[] { ("mirror", ddms.Mirror is not null), ("provider", ddms.Provider is not null && shape != DdmsShape.SeismicStoreV3), ("concurrency", ddms.Concurrency is not null) }
                .Where(k => k.Item2)
                .Select(k => $"{at}.{k.Item1}")
                .ToList();
            if (misplaced.Count > 0)
            {
                throw new FlowValidationException(
                    $"{source}: {string.Join(", ", misplaced)} describe a Well Delivery DDMS deployment, and {at} has the {DdmsCatalog.ShapeName(shape)} shape. Remove them, or declare shape: wellDeliveryV1.");
            }

            return null;
        }

        var provider = ParseEnum<DdmsProvider>(ddms.Provider, default, at + ".provider", source);
        if (ddms.Provider is not null && provider == DdmsProvider.Anthos)
        {
            throw new FlowValidationException($"{source}: {at}.provider '{ddms.Provider}' is not a provider the Well Delivery DDMS runs on: azure, aws, gc or ibm.");
        }

        var concurrency = ddms.Concurrency ?? WellDeliverySettings.DefaultConcurrency;
        if (concurrency is < 1 or > WellDeliverySettings.MaxConcurrency)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {at}.concurrency must be between 1 and {WellDeliverySettings.MaxConcurrency}; the Mongo and Cosmos stores are safe with 1 (section 6 of its brief)."));
        }

        return new WellDeliverySettings
        {
            Mirror = ddms.Mirror ?? true,
            Provider = string.IsNullOrWhiteSpace(ddms.Provider) ? null : provider,
            Concurrency = concurrency,
        };
    }

    /// <summary>
    /// The settings of a declared Production DDMS historian, or null for any other shape, which takes none of them
    /// (osdu/specs/production-timeseries/INTEGRATION.md sections 1, 4 and 6: where the query service is, how long accepted
    /// points are read back for, and the largest request the points are sent in).
    /// </summary>
    private static TimeSeriesSettings? MapTimeSeries(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (shape != DdmsShape.ProductionTimeSeriesV1)
        {
            var misplaced = new[]
                {
                    ("queryRoot", ddms.QueryRoot is not null),
                    ("settleSeconds", ddms.SettleSeconds is not null && shape != DdmsShape.ReservoirManagement),
                    ("pollSeconds", ddms.PollSeconds is not null && shape != DdmsShape.ReservoirManagement),
                    ("maxRequestBytes", ddms.MaxRequestBytes is not null),
                }
                .Where(k => k.Item2)
                .Select(k => $"{at}.{k.Item1}")
                .ToList();
            if (misplaced.Count > 0)
            {
                throw new FlowValidationException(
                    $"{source}: {string.Join(", ", misplaced)} describe the Production DDMS historian, and {at} has the {DdmsCatalog.ShapeName(shape)} shape. "
                    + "Remove them, or declare shape: productionTimeSeriesV1.");
            }

            return null;
        }

        var settle = ddms.SettleSeconds ?? TimeSeriesSettings.DefaultSettleSeconds;
        if (settle is < 0 or > TimeSeriesSettings.MaxSettleSeconds)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at}.settleSeconds must be between 0 and {TimeSeriesSettings.MaxSettleSeconds}: how long a delivery reads accepted points back for, 0 not reading them back."));
        }

        var poll = ddms.PollSeconds ?? TimeSeriesSettings.DefaultPollSeconds;
        if (poll is < 1 or > TimeSeriesSettings.MaxPollSeconds)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture, $"{source}: {at}.pollSeconds must be between 1 and {TimeSeriesSettings.MaxPollSeconds}."));
        }

        var bytes = ddms.MaxRequestBytes ?? TimeSeriesSettings.DefaultMaxRequestBytes;
        if (bytes is < TimeSeriesSettings.MinRequestBytes or > TimeSeriesSettings.MaxRequestBytes)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at}.maxRequestBytes must be between {TimeSeriesSettings.MinRequestBytes} and {TimeSeriesSettings.MaxRequestBytes}; "
                + $"the historian's documentation gives {TimeSeriesSettings.DefaultMaxRequestBytes} bytes as its limit, unverified, which is the default."));
        }

        return new TimeSeriesSettings
        {
            QueryRoot = MapDdmsRoot(ddms.QueryRoot, at + ".queryRoot", source) ?? DdmsCatalog.UsualTimeSeriesQueryRoot,
            SettleSeconds = settle,
            PollSeconds = poll,
            MaxRequestBytesPerRequest = bytes,
        };
    }

    /// <summary>
    /// The settings of a declared Reservoir Management DDMS, or null for any other shape
    /// (osdu/specs/reservoir-management-ddms/INTEGRATION.md section 2.7: how long a delivery with rows waits for the service
    /// to take the header record into its database, and how often it asks). The other shapes refuse the two keys, except
    /// the historian, whose own they are too.
    /// </summary>
    private static ReservoirManagementSettings? MapReservoirManagement(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (shape != DdmsShape.ReservoirManagement)
        {
            return null;
        }

        var settle = ddms.SettleSeconds ?? ReservoirManagementSettings.DefaultSettleSeconds;
        if (settle is < 0 or > ReservoirManagementSettings.MaxSettleSeconds)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at}.settleSeconds must be between 0 and {ReservoirManagementSettings.MaxSettleSeconds}: how long a delivery with rows waits for the service to take its header record, 0 asking once."));
        }

        var poll = ddms.PollSeconds ?? ReservoirManagementSettings.DefaultPollSeconds;
        if (poll is < 1 or > ReservoirManagementSettings.MaxPollSeconds)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture, $"{source}: {at}.pollSeconds must be between 1 and {ReservoirManagementSettings.MaxPollSeconds}."));
        }

        return new ReservoirManagementSettings { SettleSeconds = settle, PollSeconds = poll };
    }

    /// <summary>
    /// The settings of a declared Seismic Store, or null for any other shape, which takes none of them
    /// (osdu/specs/seismic-ddms/INTEGRATION.md section 9.3: the tenant, subproject and folder the datasets are registered in,
    /// the provider and object store their files go to, the size of each object or part, and the read-only flag).
    /// </summary>
    private static SeismicStoreSettings? MapSeismicStore(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (shape != DdmsShape.SeismicStoreV3)
        {
            var misplaced = new[]
                {
                    ("tenant", ddms.Tenant is not null), ("subproject", ddms.Subproject is not null), ("folder", ddms.Folder is not null),
                    ("objectStore", ddms.ObjectStore is not null), ("region", ddms.Region is not null), ("chunkMiB", ddms.ChunkMiB is not null),
                    ("readOnly", ddms.ReadOnly is not null),
                }
                .Where(k => k.Item2)
                .Select(k => $"{at}.{k.Item1}")
                .ToList();
            if (misplaced.Count > 0)
            {
                throw new FlowValidationException(
                    $"{source}: {string.Join(", ", misplaced)} describe a Seismic Store, and {at} has the {DdmsCatalog.ShapeName(shape)} shape. Remove them, or declare shape: seismicStoreV3.");
            }

            return null;
        }

        var subproject = ddms.Subproject?.Trim();
        if (string.IsNullOrEmpty(subproject))
        {
            throw new FlowValidationException($"{source}: {at}.subproject is required: the Seismic Store subproject the datasets are registered in, which an operator provisions.");
        }

        if (!DdmsCatalog.IsSubproject(subproject))
        {
            throw new FlowValidationException(
                $"{source}: {at}.subproject '{subproject}' is not a Seismic Store subproject name: a lower-case letter, then lower-case letters, digits and '-', not ending in '-'.");
        }

        var tenant = string.IsNullOrWhiteSpace(ddms.Tenant) ? null : ddms.Tenant.Trim();
        if (tenant is not null && !DdmsCatalog.IsSeismicTenant(tenant))
        {
            throw new FlowValidationException($"{source}: {at}.tenant '{tenant}' is not a Seismic Store tenant name (letters, digits, '_', '.' and '-'); on OSDU it is the data partition id.");
        }

        var folder = string.IsNullOrWhiteSpace(ddms.Folder) ? null : ddms.Folder.Trim().Trim('/');
        if (folder is { Length: 0 })
        {
            folder = null;
        }

        if (folder is not null && !DdmsCatalog.IsSeismicFolder(folder))
        {
            throw new FlowValidationException(
                $"{source}: {at}.folder '{ddms.Folder!.Trim()}' is not a Seismic Store folder: segments of letters, digits, '_', '.' and '-', separated by single slashes.");
        }

        DdmsProvider? provider = null;
        if (ddms.Provider is not null)
        {
            provider = ParseEnum<DdmsProvider>(ddms.Provider, default, at + ".provider", source);
            if (provider == DdmsProvider.Aws)
            {
                throw new FlowValidationException($"{source}: {at}.provider '{ddms.Provider}' is not a provider Seismic Store v3 runs on: azure, gc, anthos or ibm.");
            }
        }

        string? objectStore = null;
        if (!string.IsNullOrWhiteSpace(ddms.ObjectStore))
        {
            var written = ddms.ObjectStore.Trim();
            if (!Uri.TryCreate(written, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
                || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.objectStore '{written}' must be the object store's absolute http(s) address, without credentials, query or fragment (https://s3.example.com).");
            }

            objectStore = written.TrimEnd('/');
        }

        if (provider is DdmsProvider.Anthos or DdmsProvider.Ibm && objectStore is null)
        {
            throw new FlowValidationException(
                $"{source}: {at}.objectStore is required on {ddms.Provider}: Seismic Store issues a key triple there for an S3 store it does not name (osdu/specs/seismic-ddms/INTEGRATION.md section 4.3).");
        }

        var region = string.IsNullOrWhiteSpace(ddms.Region) ? SeismicStoreSettings.DefaultRegion : ddms.Region.Trim();
        if (region.Length > 64 || !region.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            throw new FlowValidationException($"{source}: {at}.region '{region}' is not a region name (lower-case letters, digits and '-', such as us-east-1).");
        }

        var chunk = ddms.ChunkMiB ?? SeismicStoreSettings.DefaultChunkMiB;
        if (chunk is < 0 or > SeismicStoreSettings.MaxChunkMiB)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at}.chunkMiB must be between 0 and {SeismicStoreSettings.MaxChunkMiB}: the MiB of each object a file is cut into on Azure (0 keeps it whole), and of each part it goes up in."));
        }

        return new SeismicStoreSettings
        {
            Tenant = tenant,
            Subproject = subproject,
            Folder = folder,
            Provider = provider,
            ObjectStore = objectStore,
            Region = region,
            ChunkMiB = chunk,
            ReadOnly = ddms.ReadOnly ?? false,
        };
    }

    /// <summary>
    /// The Register service id a declared DDMS is looked up under, or null. A DDMS that declares both its root and its
    /// collections leaves its registration nothing to supply, so naming one as well is refused. Only the Wellbore DDMS's
    /// registered documents say which collection serves which entity type in a way the route can read.
    /// </summary>
    private static string? MapRegistration(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (ddms.Register is null)
        {
            return null;
        }

        if (shape != DdmsShape.WellboreDdmsV3)
        {
            throw new FlowValidationException(
                $"{source}: {at}.register reads a DDMS's collections from its Register service registration, which the route reads for DDMSs of the wellboreDdmsV3 shape. "
                + $"Declare the root of a DDMS of the {DdmsCatalog.ShapeName(shape)} shape, and its collections where they differ from the ones its shape serves.");
        }

        var id = ddms.Register.Trim();
        if (!DdmsCatalog.IsRegistration(id))
        {
            throw new FlowValidationException(
                $"{source}: {at}.register '{id}' is not an id the Register service keeps a DDMS under: 2 to 50 letters, digits and '-'.");
        }

        if (!string.IsNullOrWhiteSpace(ddms.Root) && ddms.Collections is not null)
        {
            throw new FlowValidationException(
                $"{source}: {at} declares its root and its collections, so its registration has nothing to supply. Remove register, or leave out what the registration should say.");
        }

        return id;
    }
}

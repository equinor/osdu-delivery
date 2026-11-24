using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Documents;

// A delivery flow's target block beside its DDMSs (osdu/docs/reference/flow/delivery.md, target): its authentication,
// the options its route reads, what the gate before a record is sent does and what a record's references are checked
// against, and the blocks of the External Data Services, the Reservoir DDMS (ETP) and the Production DDMS core
// service (DSPDM) (osdu/docs/reference/flow/ddms-services.md).
internal static partial class FlowMapper
{
    internal static TargetAuth MapAuth(TargetAuthYaml? a, string source, string key = "target.auth")
    {
        if (a is null)
        {
            return new TargetAuth { Type = TargetAuthType.None };
        }

        return new TargetAuth
        {
            Type = ParseEnum(a.Type, TargetAuthType.None, key + ".type", source),
            SecretRef = a.SecretRef,
            SecondarySecretRef = a.SecondarySecretRef,
            HeaderName = a.HeaderName,
            ValuePrefix = a.ValuePrefix,
            Token = a.Token is null ? null : new TargetTokenEndpoint
            {
                Url = a.Token.Url,
                DiscoveryUrl = a.Token.DiscoveryUrl,
                Body = a.Token.Body ?? new Dictionary<string, string>(StringComparer.Ordinal),
                BasicAuthClient = a.Token.BasicAuthClient,
                TokenPath = a.Token.TokenPath ?? "access_token",
                ApplyPrefix = a.Token.ApplyPrefix ?? "Bearer ",
            },
        };
    }

    private static ProtocolOptions MapOptions(ProtocolOptionsYaml? o, string source)
    {
        if (o is null)
        {
            return new ProtocolOptions();
        }

        return new ProtocolOptions
        {
            RecordPath = o.RecordPath,
            RecordMethod = o.RecordMethod,
            DataPath = o.DataPath,
            SessionPath = o.SessionPath,
            SessionDataPath = o.SessionDataPath,
            SessionCommitPath = o.SessionCommitPath,
            VerifyPath = o.VerifyPath,
            DeletePath = o.DeletePath,
            PurgePath = o.PurgePath,
            PurgeVersionsPath = o.PurgeVersionsPath,
            BulkDeletePath = o.BulkDeletePath,
            ProbePath = o.ProbePath,
            Payload = o.Payload,
            SessionThresholdChunks = o.SessionThresholdChunks ?? 1,
            MaxChunkValues = o.MaxChunkValues ?? WellboreDdmsBulkLimits.MaxChunkValues,
            MaxChunkColumns = o.MaxChunkColumns ?? WellboreDdmsBulkLimits.MaxChunkColumns,
            PayloadContentType = Optional(o.PayloadContentType) ?? "application/x-parquet",
            FilesContentType = Optional(o.FilesContentType),
            VersionPath = o.VersionPath ?? "recordIdVersions[0]",
            SkipDuplicates = o.SkipDuplicates ?? false,
            VerifyBatchPath = o.VerifyBatchPath,
            DdmsRoot = string.IsNullOrWhiteSpace(o.DdmsRoot) ? null : o.DdmsRoot!.Trim().TrimEnd('/'),
            ValidateLegalTags = o.ValidateLegalTags ?? true,
            LegalValidatePath = string.IsNullOrWhiteSpace(o.LegalValidatePath) ? null : o.LegalValidatePath!.Trim(),
            PreserveDataKeys = o.PreserveDataKeys ?? [],
            BatchSize = o.BatchSize ?? 100,
            UploadUrlPath = o.UploadUrlPath,
            FileMetadataPath = o.FileMetadataPath,
            DatasetKind = string.IsNullOrWhiteSpace(o.DatasetKind) ? "osdu:wks:dataset--File.Generic:1.0.0" : o.DatasetKind!.Trim(),
            UploadHeaders = new Dictionary<string, string>(o.UploadHeaders ?? [], StringComparer.OrdinalIgnoreCase),
            DatasetsProperty = string.IsNullOrWhiteSpace(o.DatasetsProperty) ? "Datasets" : o.DatasetsProperty!.Trim(),
            WorkflowName = string.IsNullOrWhiteSpace(o.WorkflowName) ? "Osdu_ingest" : o.WorkflowName!.Trim(),
            WorkflowRunPath = o.WorkflowRunPath,
            WorkflowStatusPath = o.WorkflowStatusPath,
            WorkflowPollSeconds = o.WorkflowPollSeconds ?? 10,
            WorkflowTimeoutMinutes = o.WorkflowTimeoutMinutes ?? 60,
            DatasetIndexWaitSeconds = o.DatasetIndexWaitSeconds ?? 120,
            ManifestKind = string.IsNullOrWhiteSpace(o.ManifestKind) ? "osdu:wks:Manifest:1.0.0" : o.ManifestKind!.Trim(),
            UploadUrlExpiry = string.IsNullOrWhiteSpace(o.UploadUrlExpiry) ? null : o.UploadUrlExpiry!.Trim(),
            UploadBlockMiB = o.UploadBlockMiB ?? ProtocolOptions.DefaultUploadBlockMiB,
            UploadConcurrency = o.UploadConcurrency ?? ProtocolOptions.DefaultUploadConcurrency,
            UploadServerCopy = o.UploadServerCopy ?? true,
            FileDeletePath = o.FileDeletePath,
            ManifestSection = string.IsNullOrWhiteSpace(o.ManifestSection) ? null : o.ManifestSection!.Trim(),
            WorkflowAppKey = string.IsNullOrWhiteSpace(o.WorkflowAppKey) ? "osdu-delivery" : o.WorkflowAppKey!.Trim(),
            WorkflowPayload = new Dictionary<string, string>(o.WorkflowPayload ?? [], StringComparer.Ordinal),
            RecordQueryPath = o.RecordQueryPath,
            SearchQueryPath = o.SearchQueryPath,
            RegisterPath = string.IsNullOrWhiteSpace(o.RegisterPath) ? null : o.RegisterPath!.Trim(),
            DatasetInstructionsPath = Optional(o.DatasetInstructionsPath),
            DatasetRegisterPath = Optional(o.DatasetRegisterPath),
            DatasetRetrievalPath = Optional(o.DatasetRetrievalPath),
            DatasetSoftDeletePath = Optional(o.DatasetSoftDeletePath),
            ManifestByReference = ParseEnum(o.ManifestByReference, ManifestReference.Never, "target.protocolOptions.manifestByReference", source),
            ManifestInlineLimitKb = o.ManifestInlineLimitKb ?? ProtocolOptions.DefaultManifestInlineLimitKb,
            ByReferenceWorkflowName = Optional(o.ByReferenceWorkflowName) ?? ProtocolOptions.DefaultByReferenceWorkflowName,
            WorkflowPath = Optional(o.WorkflowPath),
            ContentSchemaVersion = Optional(o.ContentSchemaVersion) ?? ProtocolOptions.DefaultContentSchemaVersion,
        };
    }

    /// <summary>
    /// What the gate before a record is sent does with the record's verdict (<c>target.validation</c>, or an interface's
    /// <c>validation</c> laid over it): <c>mode</c> report (the default) or enforce, <c>unverified</c> send (the default) or
    /// hold.
    /// </summary>
    private static ValidationPolicy MapValidation(ValidationYaml? declared, KeyPaths paths, string source)
    {
        if (declared is null)
        {
            return ValidationPolicy.Default;
        }

        return new ValidationPolicy
        {
            Mode = ParseEnum(declared.Mode?.Trim(), ValidationMode.Report, paths.Shared("target.validation.mode"), source),
            Unverified = ParseEnum(declared.Unverified?.Trim(), UnverifiedAction.Send, paths.Shared("target.validation.unverified"), source),
        };
    }

    /// <summary>
    /// What a record's references are checked against before it is sent (<c>target.verifyReferences</c>): <c>none</c>,
    /// the default, or <c>storage</c>. The dspdm route writes rows that refer to no storage record, so a flow it delivers
    /// is refused the storage check.
    /// </summary>
    private static ReferenceVerification MapVerifyReferences(string? declared, DeliveryProtocol protocol, string source)
    {
        var verification = ParseEnum(declared?.Trim(), ReferenceVerification.None, "target.verifyReferences", source);
        if (verification == ReferenceVerification.Storage && protocol == DeliveryProtocol.Dspdm)
        {
            throw new FlowValidationException(
                $"{source}: target.verifyReferences is storage, and the flow is delivered by the dspdm route, whose rows refer to no storage record. Remove target.verifyReferences.");
        }

        return verification;
    }

    /// <summary>
    /// The External Data Services block (<c>target.eds</c>): how the records that configure EDS are checked before they are
    /// sent (osdu/specs/eds-dms/INTEGRATION.md section 2.1). A retrieval setting or a build named while the checks are off
    /// is refused, since nothing would read it.
    /// </summary>
    private static EdsTarget MapEds(EdsYaml? declared, string source)
    {
        if (declared is null)
        {
            return new EdsTarget();
        }

        var checks = declared.Checks ?? true;
        if (!checks && (declared.Retrieval is not null || !string.IsNullOrWhiteSpace(declared.Build)))
        {
            throw new FlowValidationException(
                $"{source}: target.eds.checks is false, so nothing reads target.eds.retrieval or target.eds.build. Remove them, or turn the checks back on.");
        }

        return new EdsTarget
        {
            Checks = checks,
            Retrieval = declared.Retrieval ?? true,
            Build = string.IsNullOrWhiteSpace(declared.Build) ? null : ParseEnum<EdsBuild>(declared.Build.Trim(), "target.eds.build", source),
        };
    }

    /// <summary>
    /// The Production DDMS core service block (<c>target.dspdm</c>): where DSPDM is under the endpoint, the time zone every
    /// request names, and the business objects the flow's kinds are rows of (osdu/specs/production-dspdm/INTEGRATION.md
    /// section 7). Whether each business object exists, and which unique constraint its key is, is DSPDM's metadata, which
    /// the run's preflight reads.
    /// </summary>
    /// <summary>
    /// The Reservoir DDMS block (<c>target.etp</c>): where its ETP WebSocket answers under the endpoint, the dataspace
    /// its records go into, and the sizes one delivery works under (osdu/specs/reservoir-ddms/INTEGRATION.md sections
    /// 1.1, 4.3 and 8.6).
    /// </summary>
    private static EtpTarget MapEtp(EtpYaml? declared, DeliveryProtocol protocol, string source)
    {
        if (declared is null)
        {
            return new EtpTarget();
        }

        if (protocol != DeliveryProtocol.Etp)
        {
            throw new FlowValidationException(
                $"{source}: target.etp declares the Reservoir DDMS the etp route writes Energistics objects to, and this flow's route is {DeliveryProtocols.Name(protocol)}. Remove target.etp.");
        }

        var target = new EtpTarget
        {
            Path = string.IsNullOrWhiteSpace(declared.Path) ? EtpTarget.DefaultPath : declared.Path.Trim(),
            Dataspace = string.IsNullOrWhiteSpace(declared.Dataspace) ? null : declared.Dataspace.Trim(),
            ObjectsPerMessage = declared.ObjectsPerMessage ?? EtpTarget.DefaultObjectsPerMessage,
            MaxMessageBytes = declared.MaxMessageBytes ?? EtpTarget.DefaultMaxMessageBytes,
            MaxArrayBytes = declared.MaxArrayBytes ?? EtpTarget.DefaultMaxArrayBytes,
            Lock = declared.Lock ?? false,
        };

        if (target.Dataspace is { } dataspace)
        {
            try
            {
                Engine.Protocols.Etp.EtpObjectXml.CheckPath(dataspace);
            }
            catch (RecordHeldException ex)
            {
                throw new FlowValidationException($"{source}: target.etp.dataspace '{dataspace}' is not a dataspace path: {ex.Message}");
            }
        }

        if (target.ObjectsPerMessage is < 1 or > 10_000)
        {
            throw new FlowValidationException($"{source}: target.etp.objectsPerMessage is {target.ObjectsPerMessage}; it is between 1 and 10000 objects.");
        }

        if (target.MaxMessageBytes is < 64_000 or > 2_000_000_000)
        {
            throw new FlowValidationException(
                $"{source}: target.etp.maxMessageBytes is {target.MaxMessageBytes}; it is between 64000 and 2000000000 bytes, and the server narrows it to its own maximum.");
        }

        if (target.MaxArrayBytes < 1024)
        {
            throw new FlowValidationException($"{source}: target.etp.maxArrayBytes is {target.MaxArrayBytes}; it is at least 1024 bytes.");
        }

        return target;
    }

    private static DspdmTarget MapDspdm(DspdmYaml? declared, DeliveryProtocol protocol, string source)
    {
        if (declared is null)
        {
            return new DspdmTarget();
        }

        if (protocol != DeliveryProtocol.Dspdm)
        {
            throw new FlowValidationException(
                $"{source}: target.dspdm declares the Production DDMS core service the dspdm route writes rows to, and this flow's route is {DeliveryProtocols.Name(protocol)}. Remove target.dspdm.");
        }

        var timezone = string.IsNullOrWhiteSpace(declared.Timezone) ? DspdmTarget.DefaultTimeZone : declared.Timezone.Trim();
        if (!DspdmKinds.IsTimezone(timezone))
        {
            throw new FlowValidationException(
                $"{source}: target.dspdm.timezone '{timezone}' must be GMT+hh:mm or GMT-hh:mm, from GMT-12:00 to GMT+14:00, the only form DSPDM's save takes.");
        }

        var objects = new Dictionary<string, DspdmBusinessObject>(StringComparer.Ordinal);
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in declared.BusinessObjects ?? [])
        {
            var entity = key?.Trim() ?? string.Empty;
            var at = $"target.dspdm.businessObjects.{entity}";
            if (!KindSegment().IsMatch(entity))
            {
                throw new FlowValidationException(
                    $"{source}: target.dspdm.businessObjects names '{entity}', which is not an entity type: the entity segment of a DSPDM kind is letters, digits, '_', '-' and '.'.");
            }

            var business = value ?? new DspdmBusinessObjectYaml();
            // DSPDM names business objects in upper case (DTOHelper upper-cases the names a request gives), and its metadata is read by that name.
            var name = string.IsNullOrWhiteSpace(business.Name) ? DspdmKinds.DefaultName(entity) : business.Name.Trim().ToUpperInvariant();
            if (!DspdmKinds.IsName(name))
            {
                throw new FlowValidationException(
                    $"{source}: {at}.name '{name}' is not a name DSPDM gives a business object: at most 50 characters, not starting with a digit.");
            }

            if (named.TryGetValue(name, out var other))
            {
                throw new FlowValidationException($"{source}: {at} and target.dspdm.businessObjects.{other} are both rows of the business object '{name}'.");
            }

            named[name] = entity;
            if (business.Key is { Count: 0 })
            {
                throw new FlowValidationException($"{source}: {at}.key lists no attribute. Leave it out to find rows by the business object's one unique constraint.");
            }

            var attributes = new List<string>();
            foreach (var attribute in business.Key ?? [])
            {
                var upper = attribute?.Trim().ToUpperInvariant() ?? string.Empty;
                if (!DspdmKinds.IsName(upper))
                {
                    throw new FlowValidationException($"{source}: {at}.key names '{attribute}', which is not an attribute name.");
                }

                if (attributes.Contains(upper, StringComparer.Ordinal))
                {
                    throw new FlowValidationException($"{source}: {at}.key names {upper} more than once (DSPDM reads attribute names in upper case).");
                }

                attributes.Add(upper);
            }

            objects[entity] = new DspdmBusinessObject { Name = string.IsNullOrWhiteSpace(business.Name) ? null : name, Key = attributes };
        }

        return new DspdmTarget
        {
            Root = MapDdmsRoot(declared.Root, "target.dspdm.root", source, "/api/dspdm/v1"),
            Timezone = timezone,
            BusinessObjects = objects,
            ExistingRows = ParseEnum(declared.ExistingRows?.Trim(), DspdmExistingRows.Hold, "target.dspdm.existingRows", source),
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex KindSegment();
}

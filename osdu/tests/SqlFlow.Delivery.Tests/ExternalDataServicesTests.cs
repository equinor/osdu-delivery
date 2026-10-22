using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// External Data Services as the storage, manifest and workflow routes serve it (osdu/specs/eds-dms/INTEGRATION.md section
/// 2): connected source registry entries, data jobs and proxy datasets checked for what eds-dms and the EDS workflows need
/// before anything is sent, and held with the rules they break; the run state EDS writes on a data job carried forward on
/// every rewrite; a version EDS writes told apart from drift by the hash of the content the flow owns; and every request
/// kept to the pinned contracts.
/// </summary>
public sealed class ExternalDataServicesTests
{
    private const string Partition = "dev";
    private const string RegistryId = "dev:master-data--ConnectedSourceRegistryEntry:source-1";
    private const string RegistryKind = "osdu:wks:master-data--ConnectedSourceRegistryEntry:1.0.0";
    private const string JobId = "dev:master-data--ConnectedSourceDataJob:job-1";
    private const string JobKind = "osdu:wks:master-data--ConnectedSourceDataJob:2.0.0";
    private const string ProxyId = "dev:dataset--External:proxy-1";
    private const string ProxyKind = "osdu:wks:dataset--External:1.0.0";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private static readonly string[] RunState = ["LastSuccessfulRunDateUTC", "FailedRecords", "CreateTimeMax"];

    private static JsonObject Scheme(string name, string flow = "ClientCredentials") => new()
    {
        ["Name"] = name,
        ["TypeID"] = "dev:reference-data--SecuritySchemeType:OAuth2:",
        ["FlowTypeID"] = $"dev:reference-data--OAuth2FlowType:{flow}:",
        ["TokenUrl"] = "https://login.example.com/oauth2/token",
        ["ClientIDKeyName"] = "source-client-id",
        ["ClientSecretKeyName"] = "source-client-secret",
        ["ScopesKeyName"] = "source-scopes",
    };

    private static JsonObject Registry(Action<JsonObject>? change = null, string kind = RegistryKind)
    {
        var data = new JsonObject
        {
            ["Name"] = "External source",
            ["FullOSDUImplementationIndicator"] = false,
            ["DatasetURL"] = "https://source.example.com/api/dataset/v1",
            ["SecuritySchemes"] = new JsonArray(Scheme("source-token")),
        };
        change?.Invoke(data);
        return FakeOsduPlatform.Record(RegistryId, kind, data);
    }

    private static JsonObject SchemeOf(JsonObject data, int index = 0) => data["SecuritySchemes"]![index]!.AsObject();

    private static JsonObject Job(Action<JsonObject>? change = null, string name = "Wells from the source")
    {
        var data = new JsonObject
        {
            ["Name"] = name,
            ["ConnectedSourceRegistryEntryID"] = RegistryId + ":",
            ["ActiveIndicator"] = true,
            ["FetchKind"] = "osdu:wks:master-data--Well:1.*.*",
            ["Filter"] = "data.VersionCreationReason: \"INCREMENTALFETCH\"",
            ["LimitRecords"] = 100,
            ["ConnectedSourceDataPartitionID"] = "source",
            ["OnIngestionDataPartitionID"] = Partition,
            ["ScheduleUTC"] = "0 1 * * *",
            ["LastSuccessfulRunDateUTC"] = "2026-01-01T00:00:00Z",
            ["OnIngestionLegalTags"] = new JsonObject { ["legaltags"] = new JsonArray("dev-public"), ["otherRelevantDataCountries"] = new JsonArray("US") },
            ["OnIngestionAcl"] = new JsonObject
            {
                ["owners"] = new JsonArray("data.default.owners@dev.example.com"),
                ["viewers"] = new JsonArray("data.default.viewers@dev.example.com"),
            },
            ["Workflows"] = new JsonArray(Fetch()),
        };
        change?.Invoke(data);
        return FakeOsduPlatform.Record(JobId, JobKind, data);
    }

    private static JsonObject Fetch() => new()
    {
        ["Tag"] = "FETCH",
        ["Handler"] = "eds_ingest",
        ["SecuritySchemeName"] = "source-token",
        ["Url"] = "https://source.example.com/api/search/v2/query",
    };

    private static JsonObject FetchOf(JsonObject data) => data["Workflows"]![0]!.AsObject();

    private static JsonObject Proxy(Action<JsonObject>? change = null, string kind = ProxyKind, string id = ProxyId)
    {
        var data = new JsonObject
        {
            ["Name"] = "file-1",
            ["DatasetProperties"] = new JsonObject
            {
                ["ConnectedSourceRegistryEntryId"] = RegistryId,
                ["ConnectedSourceDataJobId"] = JobId,
                ["SourceDataPartitionId"] = "source",
                ["SourceRecordId"] = "source:dataset--File.Generic:file-1",
            },
        };
        change?.Invoke(data);
        return FakeOsduPlatform.Record(id, kind, data);
    }

    private static JsonObject PropertiesOf(JsonObject data) => data["DatasetProperties"]!.AsObject();

    private static string Held(JsonObject document, EdsTarget? settings = null)
    {
        var hold = EdsRecordRules.Hold(settings ?? new EdsTarget(), document);
        Assert.NotNull(hold);
        Assert.EndsWith("(osdu/specs/eds-dms/INTEGRATION.md)", hold, StringComparison.Ordinal);
        return hold;
    }

    private static readonly Dictionary<string, (Action<JsonObject> Change, string Expected)> RegistryCases = new(StringComparer.Ordinal)
    {
        ["no dataset URL"] = (d => d.Remove("DatasetURL"), "data.DatasetURL is missing, and eds-dms answers 500"),
        ["empty dataset URL"] = (d => d["DatasetURL"] = " ", "data.DatasetURL is empty"),
        ["relative dataset URL"] = (d => d["DatasetURL"] = "api/dataset/v1", "data.DatasetURL 'api/dataset/v1' is not an absolute http(s) URL"),
        ["dataset URL of another scheme"] = (d => d["DatasetURL"] = "ftp://source.example.com/", "is not an absolute http(s) URL"),
        ["dataset URL not text"] = (d => d["DatasetURL"] = 5, "data.DatasetURL is not a string"),
        ["no schemes"] = (d => d.Remove("SecuritySchemes"), "data.SecuritySchemes is missing"),
        ["schemes not a list"] = (d => d["SecuritySchemes"] = "source-token", "data.SecuritySchemes is not a list"),
        ["empty schemes"] = (d => d["SecuritySchemes"] = new JsonArray(), "data.SecuritySchemes is empty"),
        ["scheme not an object"] = (d => d["SecuritySchemes"] = new JsonArray("source-token"), "data.SecuritySchemes[0] is not an object"),
        ["scheme without a name"] = (d => SchemeOf(d).Remove("Name"), "data.SecuritySchemes[0] has no Name"),
        ["scheme without a type"] = (d => SchemeOf(d).Remove("TypeID"), "data.SecuritySchemes[0] has no TypeID"),
        ["scheme without a flow"] = (d => SchemeOf(d).Remove("FlowTypeID"), "data.SecuritySchemes[0] has no FlowTypeID"),
        ["flow of another reference type"] = (d => SchemeOf(d)["FlowTypeID"] = "dev:reference-data--SecuritySchemeType:OAuth2:", "FlowTypeID 'dev:reference-data--SecuritySchemeType:OAuth2:' is not a reference-data--OAuth2FlowType reference"),
        ["flow eds-dms does not build"] = (d => SchemeOf(d)["FlowTypeID"] = "dev:reference-data--OAuth2FlowType:DeviceCode:", "names the flow 'DeviceCode', and eds-dms builds only ClientCredentials"),
        ["flow in another case"] = (d => SchemeOf(d)["FlowTypeID"] = "dev:reference-data--OAuth2FlowType:clientCredentials:", "names the flow 'clientCredentials'"),
        ["implicit flow"] = (d => d["SecuritySchemes"] = new JsonArray(Implicit()), "data.SecuritySchemes[0] uses the Implicit flow, which eds-dms refuses"),
        ["flow key missing"] = (d => SchemeOf(d).Remove("ScopesKeyName"), "data.SecuritySchemes[0] (ClientCredentials) has no ScopesKeyName"),
        ["flow key empty"] = (d => SchemeOf(d)["ClientSecretKeyName"] = string.Empty, "data.SecuritySchemes[0] (ClientCredentials) has no ClientSecretKeyName"),
        ["relative token URL"] = (d => SchemeOf(d)["TokenUrl"] = "/oauth2/token", "data.SecuritySchemes[0].TokenUrl '/oauth2/token' is not an absolute http(s) URL"),
        ["a later scheme broken"] = (d => d["SecuritySchemes"]!.AsArray().Add(Without(Scheme("backup"), "TokenUrl")), "data.SecuritySchemes[1] (ClientCredentials) has no TokenUrl"),
        ["two schemes of one name"] = (d => d["SecuritySchemes"]!.AsArray().Add(Scheme("source-token")), "data.SecuritySchemes[1] is named 'source-token' like data.SecuritySchemes[0], and eds-dms takes the first scheme of a name"),
        ["password flow without its keys"] = (d => d["SecuritySchemes"] = new JsonArray(Scheme("password", "PasswordCredentials")), "(PasswordCredentials) has no UsernameKeyName; data.SecuritySchemes[0] (PasswordCredentials) has no PasswordKeyName"),
        ["refresh flow without its token"] = (d => d["SecuritySchemes"] = new JsonArray(Scheme("refresh", "RefreshToken")), "(RefreshToken) has no RefreshTokenKeyName"),
        ["authorization code without its callback"] = (d => d["SecuritySchemes"] = new JsonArray(With(Scheme("code", "AuthorizationCode"), "RefreshTokenKeyName", "source-code")), "(AuthorizationCode) has no CallbackUrl"),
    };

    private static JsonObject Implicit() => new()
    {
        ["Name"] = "implicit",
        ["TypeID"] = "dev:reference-data--SecuritySchemeType:OAuth2:",
        ["FlowTypeID"] = "dev:reference-data--OAuth2FlowType:Implicit:",
        ["AuthorizationUrl"] = "https://login.example.com/oauth2/authorize",
        ["CallbackUrl"] = "https://app.example.com/callback",
        ["ClientIDKeyName"] = "source-client-id",
        ["ScopesKeyName"] = "source-scopes",
    };

    private static JsonObject Without(JsonObject node, string key)
    {
        node.Remove(key);
        return node;
    }

    private static JsonObject With(JsonObject node, string key, JsonNode? value)
    {
        node[key] = value;
        return node;
    }

    public static TheoryData<string> RegistryCaseNames() => Names(RegistryCases.Keys);

    private static TheoryData<string> Names(IEnumerable<string> names)
    {
        var data = new TheoryData<string>();
        foreach (var name in names)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RegistryCaseNames))]
    public void A_registry_entry_eds_dms_could_not_build_is_held_with_the_rule_it_breaks(string name)
    {
        var (change, expected) = RegistryCases[name];
        var hold = Held(Registry(change));
        Assert.StartsWith("External Data Services could not use this connected source registry entry: ", hold, StringComparison.Ordinal);
        Assert.Contains(expected, hold, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registry_entry_eds_dms_can_build_goes_whatever_flow_its_schemes_use()
    {
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Registry()));
        var schemes = new JsonArray(
            Scheme("client"),
            With(With(Scheme("password", "PasswordCredentials"), "UsernameKeyName", "source-user"), "PasswordKeyName", "source-password"),
            With(Scheme("refresh", "RefreshToken"), "RefreshTokenKeyName", "source-refresh"),
            With(With(Scheme("code", "AuthorizationCode"), "RefreshTokenKeyName", "source-code"), "CallbackUrl", "https://app.example.com/callback"));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Registry(d => d["SecuritySchemes"] = schemes)));

        // eds-dms cuts a reference at its third colon, so a version or a missing trailing colon names the same flow; the
        // value of TypeID is not read.
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Registry(d => SchemeOf(d)["FlowTypeID"] = "dev:reference-data--OAuth2FlowType:ClientCredentials:3")));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Registry(d => SchemeOf(d)["FlowTypeID"] = "dev:reference-data--OAuth2FlowType:ClientCredentials")));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Registry(d => SchemeOf(d)["TypeID"] = string.Empty)));

        // The type eds-dms's own tests use, without its group, is checked the same way.
        Assert.Contains("data.DatasetURL is missing", Held(Registry(d => d.Remove("DatasetURL"), "dev:wks:ConnectedSourceRegistryEntry:1.0.0")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_registry_entry_whose_jobs_fetch_no_files_needs_no_dataset_url_when_the_flow_says_so()
    {
        var noRetrieval = new EdsTarget { Retrieval = false };
        Assert.Null(EdsRecordRules.Hold(noRetrieval, Registry(d => d.Remove("DatasetURL"))));
        Assert.Null(EdsRecordRules.Hold(noRetrieval, Registry(d => d["DatasetURL"] = string.Empty)));

        // A URL it does name is still one eds-dms must be able to call.
        Assert.Contains("is not an absolute http(s) URL", Held(Registry(d => d["DatasetURL"] = "source"), noRetrieval), StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_account_scheme_goes_only_to_the_gc_build_of_eds_dms()
    {
        var account = new JsonObject
        {
            ["Name"] = "account",
            ["TypeID"] = "dev:reference-data--SecuritySchemeType:OAuth2:",
            ["FlowTypeID"] = "dev:reference-data--OAuth2FlowType:GcpServiceAccount:",
            ["GcpServiceAccountKey"] = "source-account-key",
            ["TokenUrl"] = "https://oauth2.googleapis.com/token",
        };
        JsonObject Entry() => Registry(d => d["SecuritySchemes"] = new JsonArray(account.DeepClone()));

        Assert.Contains("uses the GcpServiceAccount flow, which only the gc build of eds-dms takes (target.eds.build names the partition's build)", Held(Entry()), StringComparison.Ordinal);
        Assert.Contains("and target.eds.build is azure", Held(Entry(), new EdsTarget { Build = EdsBuild.Azure }), StringComparison.Ordinal);
        Assert.Contains("and target.eds.build is corePlus", Held(Entry(), new EdsTarget { Build = EdsBuild.CorePlus }), StringComparison.Ordinal);
        Assert.Null(EdsRecordRules.Hold(new EdsTarget { Build = EdsBuild.Gc }, Entry()));

        account.Remove("GcpServiceAccountKey");
        Assert.Contains("(GcpServiceAccount) has no GcpServiceAccountKey", Held(Entry(), new EdsTarget { Build = EdsBuild.Gc }), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_rule_a_record_breaks_is_named_at_once_and_nothing_is_checked_when_the_flow_turns_the_checks_off()
    {
        var broken = Registry(d =>
        {
            d.Remove("DatasetURL");
            SchemeOf(d).Remove("ClientIDKeyName");
        });
        var hold = Held(broken);
        Assert.Contains("data.DatasetURL is missing", hold, StringComparison.Ordinal);
        Assert.Contains("; data.SecuritySchemes[0] (ClientCredentials) has no ClientIDKeyName", hold, StringComparison.Ordinal);

        Assert.Null(EdsRecordRules.Hold(new EdsTarget { Checks = false }, broken));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), FakeOsduPlatform.Record("dev:master-data--Well:w-1", "osdu:wks:master-data--Well:1.0.0")));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), new JsonObject { ["id"] = RegistryId }));
    }

    private static readonly Dictionary<string, (Action<JsonObject> Change, string Expected)> JobCases = new(StringComparer.Ordinal)
    {
        ["no registry entry"] = (d => d.Remove("ConnectedSourceRegistryEntryID"), "data.ConnectedSourceRegistryEntryID is missing"),
        ["registry entry without its colon"] = (d => d["ConnectedSourceRegistryEntryID"] = RegistryId, $"data.ConnectedSourceRegistryEntryID '{RegistryId}' is not a reference to a connected source registry entry"),
        ["registry entry of another type"] = (d => d["ConnectedSourceRegistryEntryID"] = "dev:master-data--Well:w-1:", "'dev:master-data--Well:w-1:' is not a reference to a connected source registry entry"),
        ["registry entry with a colon in its id"] = (d => d["ConnectedSourceRegistryEntryID"] = RegistryId + ":a:", "is not a reference to a connected source registry entry"),
        ["no active indicator"] = (d => d.Remove("ActiveIndicator"), "data.ActiveIndicator is missing, and the scheduler runs only the jobs where it is true"),
        ["active indicator as text"] = (d => d["ActiveIndicator"] = "true", "data.ActiveIndicator is not true or false"),
        ["no fetch kind"] = (d => d.Remove("FetchKind"), "data.FetchKind is missing"),
        ["fetch kind of three parts"] = (d => d["FetchKind"] = "osdu:wks:master-data--Well", "data.FetchKind 'osdu:wks:master-data--Well' is not a kind the source's search takes"),
        ["no filter"] = (d => d.Remove("Filter"), "data.Filter is missing"),
        ["filter not text"] = (d => d["Filter"] = 3, "data.Filter is not a string"),
        ["no source partition"] = (d => d.Remove("ConnectedSourceDataPartitionID"), "data.ConnectedSourceDataPartitionID is missing"),
        ["partition with a space"] = (d => d["OnIngestionDataPartitionID"] = "open des", "data.OnIngestionDataPartitionID 'open des' is not a partition id"),
        ["no legal tags"] = (d => d.Remove("OnIngestionLegalTags"), "data.OnIngestionLegalTags is missing, and EDS gives the records it fetches its legal tags"),
        ["legal tags not an object"] = (d => d["OnIngestionLegalTags"] = new JsonArray("dev-public"), "data.OnIngestionLegalTags is not an object"),
        ["empty legal tags"] = (d => d["OnIngestionLegalTags"]!["legaltags"] = new JsonArray(), "data.OnIngestionLegalTags.legaltags is not a list of at least one entry"),
        ["no countries"] = (d => d["OnIngestionLegalTags"]!.AsObject().Remove("otherRelevantDataCountries"), "data.OnIngestionLegalTags.otherRelevantDataCountries is missing"),
        ["no access block"] = (d => d.Remove("OnIngestionAcl"), "data.OnIngestionAcl is missing"),
        ["owner that is not a data group"] = (d => d["OnIngestionAcl"]!["owners"] = new JsonArray("owners@dev.example.com"), "data.OnIngestionAcl.owners holds 'owners@dev.example.com', which is not a group Storage takes in an ACL"),
        ["empty viewers"] = (d => d["OnIngestionAcl"]!["viewers"] = new JsonArray(), "data.OnIngestionAcl.viewers is not a list of at least one entry"),
        ["viewer that is not text"] = (d => d["OnIngestionAcl"]!["viewers"] = new JsonArray(1), "data.OnIngestionAcl.viewers holds an empty entry, or one that is not a string"),
        ["no schedule"] = (d => d.Remove("ScheduleUTC"), "data.ScheduleUTC is missing"),
        ["schedule of four fields"] = (d => d["ScheduleUTC"] = "0 1 * *", "data.ScheduleUTC '0 1 * *' is not a cron expression of five or six fields"),
        ["limit of zero"] = (d => d["LimitRecords"] = 0, "data.LimitRecords is not a whole number above zero"),
        ["limit with a fraction"] = (d => d["LimitRecords"] = 2.5, "data.LimitRecords is not a whole number above zero"),
        ["limit as text"] = (d => d["LimitRecords"] = "100", "data.LimitRecords is not a whole number above zero"),
        ["no workflows"] = (d => d.Remove("Workflows"), "data.Workflows is missing, and eds_ingest reads the source's search URL from its FETCH entry"),
        ["workflows not a list"] = (d => d["Workflows"] = Fetch(), "data.Workflows is not a list"),
        ["no fetch entry"] = (d => FetchOf(d)["Tag"] = "RETRIEVE", "data.Workflows has no FETCH entry"),
        ["two fetch entries"] = (d => d["Workflows"]!.AsArray().Add(Fetch()), "data.Workflows has 2 FETCH entries, and eds_ingest reads one"),
        ["fetch of another handler"] = (d => FetchOf(d)["Handler"] = "Eds_ingest", "names the handler 'Eds_ingest', and the scheduler starts eds_ingest for it"),
        ["fetch of no handler"] = (d => FetchOf(d).Remove("Handler"), "names the handler 'none'"),
        ["fetch without its url"] = (d => FetchOf(d).Remove("Url"), "the FETCH entry of data.Workflows has no Url"),
        ["fetch with a relative url"] = (d => FetchOf(d)["Url"] = "/api/search/v2/query", "the FETCH entry's Url '/api/search/v2/query' is not an absolute http(s) URL"),
        ["fetch without its scheme"] = (d => FetchOf(d).Remove("SecuritySchemeName"), "the FETCH entry of data.Workflows has no SecuritySchemeName"),
        ["workflow entry not an object"] = (d => d["Workflows"]!.AsArray().Add("RETRIEVE"), "data.Workflows[1] is not an object"),
    };

    public static TheoryData<string> JobCaseNames() => Names(JobCases.Keys);

    [Theory]
    [MemberData(nameof(JobCaseNames))]
    public void A_data_job_the_eds_workflows_could_not_run_is_held_with_the_rule_it_breaks(string name)
    {
        var (change, expected) = JobCases[name];
        var hold = Held(Job(change));
        Assert.StartsWith("External Data Services could not use this connected source data job: ", hold, StringComparison.Ordinal);
        Assert.Contains(expected, hold, StringComparison.Ordinal);
    }

    [Fact]
    public void A_data_job_the_eds_workflows_can_run_goes_in_every_form_the_sources_write()
    {
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Job()));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Job(d =>
        {
            d["ConnectedSourceRegistryEntryID"] = RegistryId + ":3";
            d["ScheduleUTC"] = "0 13 * * ? 1";
            d["Filter"] = string.Empty;
            d["ActiveIndicator"] = false;
            d["LimitRecords"] = 100.0;
            d["FetchKind"] = "osdu:wks:*:*";
            d["Workflows"]!.AsArray().Add(new JsonObject { ["Tag"] = "RETRIEVE", ["Handler"] = "eds_dms", ["SecuritySchemeName"] = "source-token", ["Url"] = "https://source.example.com/api/dataset/v1" });
        })));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Job(d => d.Remove("LimitRecords"))));

        // The job a data job names by the bare type eds-dms's tests use is a registry entry all the same.
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Job(d => d["ConnectedSourceRegistryEntryID"] = "dev:ConnectedSourceRegistryEntry:source-1:")));
    }

    private static readonly Dictionary<string, (Action<JsonObject> Change, string Expected)> ProxyCases = new(StringComparer.Ordinal)
    {
        ["no dataset properties"] = (d => d.Remove("DatasetProperties"), "data.DatasetProperties is missing, and eds-dms fails every retrieval request that includes the record"),
        ["the layout eds-dms does not read"] = (
            d => d["DatasetProperties"] = new JsonObject
            {
                ["Source"] = "source",
                ["DataJob"] = JobId,
                ["ExternalDatasetProperties"] = new JsonObject { ["FileSourceInfo"] = new JsonObject { ["FileSource"] = "https://source.example.com/file-1" } },
            },
            "data.DatasetProperties.ConnectedSourceRegistryEntryId is missing, and eds-dms fails every retrieval request that includes the record without it"),
        ["no registry entry id"] = (d => PropertiesOf(d).Remove("ConnectedSourceRegistryEntryId"), "data.DatasetProperties.ConnectedSourceRegistryEntryId is missing"),
        ["no job id"] = (d => PropertiesOf(d)["ConnectedSourceDataJobId"] = " ", "data.DatasetProperties.ConnectedSourceDataJobId is missing"),
        ["no source partition"] = (d => PropertiesOf(d).Remove("SourceDataPartitionId"), "data.DatasetProperties.SourceDataPartitionId is missing"),
        ["no source record"] = (d => PropertiesOf(d).Remove("SourceRecordId"), "data.DatasetProperties.SourceRecordId is missing"),
        ["two spellings that differ"] = (d => PropertiesOf(d)["ConnectedSourceDataJobID"] = "dev:master-data--ConnectedSourceDataJob:job-2", $"data.DatasetProperties names ConnectedSourceDataJobId '{JobId}' and ConnectedSourceDataJobID 'dev:master-data--ConnectedSourceDataJob:job-2', and eds-dms reads one of them"),
        ["id that is not text"] = (d => PropertiesOf(d)["SourceRecordId"] = 5, "data.DatasetProperties.SourceRecordId is not a string"),
        ["registry entry id of two parts"] = (d => PropertiesOf(d)["ConnectedSourceRegistryEntryId"] = "dev:source-1", "ConnectedSourceRegistryEntryId 'dev:source-1' is not a record id eds-dms can read"),
        ["registry entry id of another type"] = (d => PropertiesOf(d)["ConnectedSourceRegistryEntryId"] = "dev:master-data--Well:w-1", "ConnectedSourceRegistryEntryId 'dev:master-data--Well:w-1' does not name a connected source registry entry"),
        ["job id with a colon inside it"] = (d => PropertiesOf(d)["ConnectedSourceDataJobId"] = JobId + ":1a", $"ConnectedSourceDataJobId '{JobId}:1a' is not a record id eds-dms can read"),
        ["partition with a slash"] = (d => PropertiesOf(d)["SourceDataPartitionId"] = "sou/rce", "data.DatasetProperties.SourceDataPartitionId 'sou/rce' is not a partition id"),
        ["source record of another partition"] = (d => PropertiesOf(d)["SourceRecordId"] = "other:dataset--File.Generic:file-1", "SourceRecordId 'other:dataset--File.Generic:file-1' reaches the source as 'source:other:dataset--File.Generic:file-1', which is not a record id of partition 'source'"),
        ["source record with a version it cannot cut"] = (d => PropertiesOf(d)["SourceRecordId"] = "dataset--File.Generic:file-1:3", "reaches the source as 'source:dataset--File.Generic:file-1:3'"),
    };

    public static TheoryData<string> ProxyCaseNames() => Names(ProxyCases.Keys);

    [Theory]
    [MemberData(nameof(ProxyCaseNames))]
    public void A_proxy_dataset_eds_dms_could_not_serve_is_held_with_the_rule_it_breaks(string name)
    {
        var (change, expected) = ProxyCases[name];
        var hold = Held(Proxy(change));
        Assert.StartsWith("External Data Services could not use this proxy dataset: ", hold, StringComparison.Ordinal);
        Assert.Contains(expected, hold, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proxy_dataset_eds_dms_can_serve_goes_in_both_spellings_and_both_types()
    {
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Proxy()));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Proxy(
            d => d["DatasetProperties"] = new JsonObject
            {
                ["ConnectedSourceRegistryEntryID"] = RegistryId + ":",
                ["ConnectedSourceDataJobID"] = JobId + ":7",
                ["SourceDataPartitionID"] = "source",
                ["SourceRecordID"] = "source:dataset--File.Generic:file-1:3",
            },
            "osdu:wks:dataset--ConnectedSource.Generic:0.2.0",
            "dev:dataset--ConnectedSource.Generic:file-1")));

        // The same value under both spellings is one value; a source record id without its partition gets the partition in
        // front, as eds-dms puts it there.
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Proxy(d => PropertiesOf(d)["ConnectedSourceDataJobID"] = JobId)));
        Assert.Null(EdsRecordRules.Hold(new EdsTarget(), Proxy(d => PropertiesOf(d)["SourceRecordId"] = "dataset--File.Generic:file-1")));
    }

    [Theory]
    [InlineData("dev:master-data--Well:w-1:", "dev:master-data--Well:w-1")]
    [InlineData("dev:master-data--Well:w-1:12", "dev:master-data--Well:w-1")]
    [InlineData("dev:master-data--Well:w-1:a:b", "dev:master-data--Well:w-1")]
    [InlineData("dev:master-data--Well:w-1", "dev:master-data--Well:w-1")]
    [InlineData("master-data--Well:w-1:3", "master-data--Well:w-1:3")]
    [InlineData("", "")]
    public void A_reference_is_cut_at_its_third_colon_as_eds_dms_cuts_it(string reference, string expected)
        => Assert.Equal(expected, EdsRecordRules.VersionStripped(reference));

    [Theory]
    [InlineData("dev:reference-data--OAuth2FlowType:ClientCredentials:", "ClientCredentials")]
    [InlineData("dev:reference-data--OAuth2FlowType:RefreshToken:2", "RefreshToken")]
    [InlineData("dev:reference-data--OAuth2FlowType:", "")]
    [InlineData("ClientCredentials", null)]
    [InlineData("dev:reference-data--SecuritySchemeType:OAuth2:", null)]
    public void A_flow_type_reference_names_the_flow_after_its_type(string reference, string? expected)
        => Assert.Equal(expected, EdsRecordRules.FlowOf(reference));

    [Theory]
    [InlineData("source:dataset--File.Generic:f-1", "source:dataset--File.Generic:f-1")]
    [InlineData("source:dataset--File.Generic:f-1:4", "source:dataset--File.Generic:f-1")]
    [InlineData("dataset--File.Generic:f-1", "source:dataset--File.Generic:f-1")]
    [InlineData("sourcex:dataset--File.Generic:f-1", "source:sourcex:dataset--File.Generic:f-1")]
    public void The_source_record_id_eds_dms_sends_carries_the_source_partition(string reference, string expected)
        => Assert.Equal(expected, EdsRecordRules.SentSourceRecordId(reference, "source"));

    [Fact]
    public void Only_a_data_job_carries_keys_eds_writes_and_they_join_the_flow_s_own()
    {
        Assert.Equal(RunState, EdsRecordRules.WrittenByEds(EdsRecordRules.DataJob));
        Assert.Equal(RunState, EdsRecordRules.WrittenByEds("ConnectedSourceDataJob"));
        Assert.Empty(EdsRecordRules.WrittenByEds(EdsRecordRules.RegistryEntry));
        Assert.Empty(EdsRecordRules.WrittenByEds(null));

        var options = new ProtocolOptions { PreserveDataKeys = ["ExtensionProperties", "FailedRecords"] };
        Assert.Equal(new[] { "ExtensionProperties", "FailedRecords", "LastSuccessfulRunDateUTC", "CreateTimeMax" }, OwnedContent.PreservedKeys(options, Job()));
        Assert.Same(options.PreserveDataKeys, OwnedContent.PreservedKeys(options, Registry()));
        Assert.Empty(OwnedContent.PreservedKeys(new ProtocolOptions(), Proxy()));
    }

    [Fact]
    public void The_hash_of_a_flow_s_own_content_leaves_out_what_storage_adds_and_the_keys_another_system_writes()
    {
        var written = Job();
        var hash = OwnedContent.Hash(written, RunState);

        // What Storage adds, the forms it may give a number or an empty block in, and the order it lists keys in.
        var stored = (JsonObject)JsonNode.Parse(written.ToJsonString())!;
        stored["version"] = 1_700_000_000_000_123L;
        stored["createUser"] = "eds@dev.example.com";
        stored["createTime"] = "2026-09-17T10:00:00.000Z";
        stored["modifyUser"] = "eds@dev.example.com";
        stored["modifyTime"] = "2026-09-17T11:00:00.000Z";
        stored["legal"]!["status"] = "compliant";
        stored["tags"] = new JsonObject();
        stored["meta"] = new JsonArray();
        stored["ancestry"] = new JsonObject { ["parents"] = new JsonArray() };
        stored["data"]!["LimitRecords"] = 100.0;
        var reordered = new JsonObject();
        foreach (var (key, value) in stored.Reverse())
        {
            reordered[key] = value?.DeepClone();
        }

        Assert.Equal(hash, OwnedContent.Hash(reordered, RunState));

        // The run state EDS writes is left out; everything else of the record is the flow's.
        stored["data"]!["LastSuccessfulRunDateUTC"] = "2026-09-17T11:00:00Z";
        stored["data"]!["FailedRecords"] = new JsonArray("source:master-data--Well:w-9");
        stored["data"]!["CreateTimeMax"] = "2026-09-17T10:59:00Z";
        Assert.Equal(hash, OwnedContent.Hash(stored, RunState));
        Assert.NotEqual(hash, OwnedContent.Hash(stored, []));

        foreach (var change in new Action<JsonObject>[]
        {
            s => s["data"]!["ActiveIndicator"] = false,
            s => s["acl"]!["viewers"] = new JsonArray("data.other.viewers@dev.example.com"),
            s => s["legal"]!["legaltags"] = new JsonArray("dev-private"),
            s => s["tags"] = new JsonObject { ["owner"] = "eds" },
            s => s["ancestry"] = new JsonObject { ["parents"] = new JsonArray(RegistryId + ":1") },
            s => s["kind"] = "osdu:wks:master-data--ConnectedSourceDataJob:1.0.0",
        })
        {
            var changed = (JsonObject)stored.DeepClone();
            change(changed);
            Assert.NotEqual(hash, OwnedContent.Hash(changed, RunState));
        }
    }

    [Fact]
    public void A_delivery_records_the_hash_only_when_it_carries_keys_and_clears_one_it_no_longer_stands_behind()
    {
        var returned = new Dictionary<string, string>(StringComparer.Ordinal);
        OwnedContent.Record(returned, Job(), RunState, new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Equal(OwnedContent.Hash(Job(), RunState), returned[OwnedContent.HashValue]);
        Assert.Equal("""["LastSuccessfulRunDateUTC","FailedRecords","CreateTimeMax"]""", returned[OwnedContent.ExcludedValue]);
        var recorded = OwnedContent.Recorded(returned);
        Assert.NotNull(recorded);
        Assert.Equal(RunState, recorded.Value.Excluded);

        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        OwnedContent.Record(none, Registry(), [], new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Empty(none);

        var cleared = new Dictionary<string, string>(StringComparer.Ordinal);
        OwnedContent.Record(cleared, Registry(), [], returned);
        Assert.Equal(string.Empty, cleared[OwnedContent.HashValue]);
        Assert.Equal(string.Empty, cleared[OwnedContent.ExcludedValue]);
        Assert.Null(OwnedContent.Recorded(cleared));

        // A state that cannot be read records nothing a verify could lean on.
        Assert.Null(OwnedContent.Recorded(null));
        Assert.Null(OwnedContent.Recorded(new Dictionary<string, string> { [OwnedContent.HashValue] = "abc" }));
        Assert.Null(OwnedContent.Recorded(new Dictionary<string, string> { [OwnedContent.HashValue] = "abc", [OwnedContent.ExcludedValue] = "not json" }));
        Assert.Null(OwnedContent.Recorded(new Dictionary<string, string> { [OwnedContent.HashValue] = "abc", [OwnedContent.ExcludedValue] = """{"a":1}""" }));
        Assert.Null(OwnedContent.Recorded(new Dictionary<string, string> { [OwnedContent.HashValue] = "abc", [OwnedContent.ExcludedValue] = "[1]" }));
        Assert.Null(OwnedContent.Unchanged(Job(), null));

        Assert.Null(OwnedContent.StateOf(null));
        Assert.Null(OwnedContent.StateOf("""{"recordId":"x"}"""));
        Assert.Equal("h", OwnedContent.StateOf($$"""{"recordId":"x","{{OwnedContent.HashValue}}":"h"}""")![OwnedContent.HashValue]);
    }

    private static string FlowDocument(string eds = "", string interfaces = "")
    {
        var head = interfaces.Length == 0
            ? """
              flowType: delivery
              name: eds-sources
              source:
                connection: ${env:EDS_DB}
                work: ../.work/eds
                record: { object: Eds.ing.Source, key: [source_id] }
              render:
                mapping: ConnectedSourceRegistryEntry@1.0.0
              target:
                endpoint: ${env:OSDU_URL}
                protocol: storage
                headers:
                  data-partition-id: dev
              """
            : """
              flowType: delivery
              name: eds-sources
              source:
                connection: ${env:EDS_DB}
                work: ../.work/eds
              target:
                endpoint: ${env:OSDU_URL}
                headers:
                  data-partition-id: dev
              """;
        return (head + "\n" + eds + interfaces).ReplaceLineEndings("\n");
    }

    [Fact]
    public void A_flow_names_how_the_records_that_configure_eds_are_checked()
    {
        var loader = new DeliveryDocumentLoader();
        var defaults = loader.ParseFlow(FlowDocument()).Target.Eds;
        Assert.True(defaults.Checks);
        Assert.True(defaults.Retrieval);
        Assert.Null(defaults.Build);

        var declared = loader.ParseFlow(FlowDocument("  eds: { retrieval: false, build: gc }\n")).Target.Eds;
        Assert.Equal(new EdsTarget { Retrieval = false, Build = EdsBuild.Gc }, declared);
        Assert.Equal(EdsBuild.CorePlus, loader.ParseFlow(FlowDocument("  eds: { build: core-plus }\n")).Target.Eds.Build);
        Assert.Equal(new EdsTarget { Checks = false }, loader.ParseFlow(FlowDocument("  eds: { checks: false }\n")).Target.Eds);

        var build = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(FlowDocument("  eds: { build: aws }\n")));
        Assert.Contains("'target.eds.build' value 'aws' is not one of corePlus, azure, gc", build.Message, StringComparison.Ordinal);
        var unread = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(FlowDocument("  eds: { checks: false, retrieval: false }\n")));
        Assert.Contains("target.eds.checks is false, so nothing reads target.eds.retrieval or target.eds.build", unread.Message, StringComparison.Ordinal);
        Assert.Contains("invalid YAML", Assert.Throws<FlowValidationException>(() => loader.ParseFlow(FlowDocument("  eds: { provider: gc }\n"))).Message, StringComparison.Ordinal);

        // The deployment is the source's: every interface reads it.
        var source = loader.ParseSource(FlowDocument("  eds: { build: azure }\n", """
            interfaces:
              registries:
                record: { object: Eds.ing.Source, key: [source_id] }
                mapping: ConnectedSourceRegistryEntry@1.0.0
              jobs:
                record: { object: Eds.ing.Job, key: [job_id] }
                mapping: ConnectedSourceDataJob@2.0.0
                route: manifest
            """));
        Assert.Equal(2, source.Interfaces.Count);
        Assert.All(source.Interfaces, i => Assert.Equal(EdsBuild.Azure, i.Target.Eds.Build));
    }

    private static FlowDefinition Routed(DeliveryProtocol protocol, bool files = false, WorkflowAnchor? anchor = null)
    {
        var flow = Samples.Targeting(new FlowTarget
        {
            Endpoint = FakeOsduPlatform.Endpoint,
            Protocol = protocol,
            Workflow = anchor is { } a ? new WorkflowRoute { Anchor = a, Stages = [new WorkflowStage { Workflow = "eds_ingest", Context = new JsonObject() }] } : null,
        });
        return files
            ? flow with
            {
                Source = flow.Source with
                {
                    Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal) { [PayloadParts.Files] = new FlowPayload { Root = "files", LocationColumn = "folder", HashColumn = "hash" } },
                },
            }
            : flow;
    }

    [Fact]
    public void A_proxy_dataset_goes_by_a_route_that_registers_no_files_for_it()
    {
        RouteChecks.Check(Routed(DeliveryProtocol.Storage), ProxyKind);
        RouteChecks.Check(Routed(DeliveryProtocol.Manifest), ProxyKind);
        RouteChecks.Check(Routed(DeliveryProtocol.Workflow, anchor: WorkflowAnchor.Storage), "osdu:wks:dataset--ConnectedSource.Generic:0.2.0");
        RouteChecks.Check(Routed(DeliveryProtocol.Dataset), "osdu:wks:dataset--File.Generic:1.0.0");

        foreach (var flow in new[]
        {
            Routed(DeliveryProtocol.Dataset),
            Routed(DeliveryProtocol.File, files: true),
            Routed(DeliveryProtocol.Manifest, files: true),
            Routed(DeliveryProtocol.Workflow, anchor: WorkflowAnchor.Dataset, files: true),
            Routed(DeliveryProtocol.Workflow, anchor: WorkflowAnchor.Storage, files: true),
        })
        {
            var refused = Assert.Throws<DeliveryException>(() => RouteChecks.Check(flow, ProxyKind));
            Assert.Contains("an External Data Services proxy dataset: the dataset it names stays in the external source", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Deliver it by the storage route", refused.Message, StringComparison.Ordinal);
        }
    }

    private const string RegistryTemplate = """
        {
          "$id": "https://example.org/ConnectedSourceRegistryEntry.1.0.0.json",
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "acl": { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } }, "viewers": { "type": "array", "items": { "type": "string" } } } },
            "legal": { "type": "object", "properties": { "legaltags": { "type": "array", "items": { "type": "string" } }, "otherRelevantDataCountries": { "type": "array", "items": { "type": "string" } } } },
            "data": {
              "type": "object",
              "properties": {
                "Name": { "type": "string" },
                "DatasetURL": { "type": "string" },
                "SecuritySchemes": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "Name": { "type": "string" },
                      "TypeID": { "type": "string" },
                      "FlowTypeID": { "type": "string" },
                      "TokenUrl": { "type": "string" },
                      "ClientIDKeyName": { "type": "string" },
                      "ClientSecretKeyName": { "type": "string" },
                      "ScopesKeyName": { "type": "string" }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static async Task<List<PlanEntry>> PlanRegistriesAsync(EdsTarget eds)
    {
        var schema = SchemaSnapshot.Parse(RegistryKind, RegistryTemplate, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var mapping = new DeliveryDocumentLoader().ParseMapping($$"""
            documentType: mapping
            name: Registry
            version: 1.0.0
            template:
              kind: {{RegistryKind}}
              version: {{schema.Version}}
            dataset:
              system: eds
              key: [name]
            parameters:
              dataPartition: { required: true }
            record:
              acl:
                owners: [data.default.owners@dev.example.com]
                viewers: [data.default.viewers@dev.example.com]
              legal:
                legaltags: [dev-public]
                otherRelevantDataCountries: [US]
              data:
                Name: { $from: name }
                DatasetURL: { $from: url, $required: false }
                SecuritySchemes:
                  $forEach: schemes
                  $item:
                    Name: { $from: name }
                    TypeID: "dev:reference-data--SecuritySchemeType:OAuth2:"
                    FlowTypeID: { $from: flow }
                    TokenUrl: { $from: token_url }
                    ClientIDKeyName: { $from: client_id }
                    ClientSecretKeyName: { $from: client_secret, $required: false }
                    ScopesKeyName: { $from: scopes }
            """.ReplaceLineEndings("\n"), "registry.yaml");
        var context = new RenderContext
        {
            MappingReference = "Registry@1.0.0",
            CacheScope = Partition,
            CacheVersion = ReferenceSnapshot.Empty.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = Partition },
        };
        var resolved = new ResolvedMapping(mapping, schema, ReferenceSnapshot.Empty, context, new MappingRenderer(mapping, schema, ReferenceSnapshot.Empty, context));
        var flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Storage, Eds = eds }) with
        {
            Render = new FlowRender { Mapping = "Registry@1.0.0" },
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = Partition };
        var tables = new MemoryIngestionTables();
        var planner = new Planner(tables.Open(flow, parameters, NullLoggerFactory.Instance), Samples.Payloads(), ledger: null, Samples.Logger<Planner>());
        var header = new PlanHeader
        {
            Flow = flow,
            Source = new SourceHeader
            {
                Selection = SourceSelection.Full(),
                Window = new SourceWindow(null, new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc)),
                Columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase),
                KeyColumns = [new SourceKeyColumn("name", "nvarchar")],
            },
            Mapping = resolved,
            Parameters = parameters,
            Issues = [],
        };

        SourceRecord Row(string name, string? url, params (string Name, string Flow, string? Secret)[] schemes) => new()
        {
            Row = new SourceRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["name"] = name, ["url"] = url }),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
            {
                ["schemes"] = schemes.Select(s => new SourceRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = s.Name,
                    ["flow"] = $"dev:reference-data--OAuth2FlowType:{s.Flow}:",
                    ["token_url"] = "https://login.example.com/oauth2/token",
                    ["client_id"] = "source-client-id",
                    ["client_secret"] = s.Secret,
                    ["scopes"] = "source-scopes",
                })).ToList(),
            },
        };

        var records = new[]
        {
            Row("usable", "https://source.example.com/api/dataset/v1", ("token", "ClientCredentials", "source-client-secret")),
            Row("no-url", null, ("token", "ClientCredentials", "source-client-secret")),
            Row("no-secret", "https://source.example.com/api/dataset/v1", ("token", "ClientCredentials", null)),
        };

        var entries = new List<PlanEntry>();
        await foreach (var entry in planner.EntriesOfAsync(header, Stream(records), 1, new PlanSummary()))
        {
            entries.Add(entry);
        }

        return entries;

        static async IAsyncEnumerable<SourceRecord> Stream(IEnumerable<SourceRecord> rows)
        {
            foreach (var row in rows)
            {
                await Task.Yield();
                yield return row;
            }
        }
    }

    private static string NameOf(PlanEntry entry) => entry.Render!.Document["data"]!["Name"]!.GetValue<string>();

    [Fact]
    public async Task A_plan_holds_the_registry_entries_eds_could_not_use_before_anything_is_sent()
    {
        var entries = await PlanRegistriesAsync(new EdsTarget());
        Assert.Equal(3, entries.Count);

        var usable = Assert.Single(entries, e => e.Action == PlannedAction.Create);
        Assert.Equal("usable", NameOf(usable));
        Assert.StartsWith("dev:master-data--ConnectedSourceRegistryEntry:", usable.TargetId, StringComparison.Ordinal);
        var held = entries.Where(e => e.Action == PlannedAction.Hold).OrderBy(NameOf, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "no-secret", "no-url" }, held.Select(NameOf));
        Assert.All(held, e => Assert.StartsWith("External Data Services could not use this connected source registry entry: ", e.Reason, StringComparison.Ordinal));
        Assert.Contains("data.SecuritySchemes[0] (ClientCredentials) has no ClientSecretKeyName", held[0].Reason, StringComparison.Ordinal);
        Assert.Contains("data.DatasetURL is missing", held[1].Reason, StringComparison.Ordinal);
        Assert.All(held, e => Assert.False(e.IsDelivery));

        // The flow says what its deployment needs: without retrieval an entry needs no URL, and with the checks off nothing is held.
        var noRetrieval = await PlanRegistriesAsync(new EdsTarget { Retrieval = false });
        Assert.Equal(2, noRetrieval.Count(e => e.Action == PlannedAction.Create));
        var anything = await PlanRegistriesAsync(new EdsTarget { Checks = false });
        Assert.All(anything, e => Assert.Equal(PlannedAction.Create, e.Action));
    }

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform)
        {
            Platform = platform;
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, platform, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Partition });
        }

        public FakeOsduPlatform Platform { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public static ProtocolOptions Options => new() { BatchSize = 10, WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };

        public void Dispose() => Runtime.Dispose();
    }

    private static DeliveryWork Work(JsonObject document, long? existing = null, IReadOnlyDictionary<string, string>? state = null, IDictionary<string, IReadOnlyDictionary<string, string>>? steps = null) => new()
    {
        Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("eds", [document["id"]!.GetValue<string>()]),
        TargetId = document["id"]!.GetValue<string>(),
        Document = document,
        DeliverMetadata = true,
        DeliverPayload = false,
        ExistingVersion = existing,
        TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
        CompletedSteps = steps is null ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal) : (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>)new Dictionary<string, IReadOnlyDictionary<string, string>>(steps, StringComparer.Ordinal),
        StepCompleted = steps is null
            ? null
            : (report, _) =>
            {
                steps[report.Step] = report.Returned;
                return Task.CompletedTask;
            },
    };

    /// <summary>What EDS does to a data job after a fetch: it reads the latest version and writes it back with its run state (section 6).</summary>
    private static long RunEds(FakeOsduPlatform platform, string at, string failed)
    {
        var stored = (JsonObject)platform.Records[JobId].DeepClone();
        stored.Remove("version");
        stored["data"]!["LastSuccessfulRunDateUTC"] = at;
        stored["data"]!["FailedRecords"] = new JsonArray(failed);
        stored["data"]!["CreateTimeMax"] = at;
        var version = platform.Put(stored);

        // Storage stamps who wrote the version and when, and says whether the legal tags hold.
        var landed = platform.Records[JobId];
        landed["modifyUser"] = "eds-airflow@dev.example.com";
        landed["modifyTime"] = at;
        landed["legal"]!["status"] = "compliant";
        return version;
    }

    private static VerifyRequest Request(long? expected, IReadOnlyDictionary<string, string> state)
        => new(JobId, expected, OwnedContent.StateOf(JsonMerge.FromValues(state)));

    private static long VersionOf(IReadOnlyDictionary<string, string> returned) => long.Parse(returned["version"], CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_data_job_written_through_storage_keeps_the_run_state_eds_wrote_and_a_version_eds_writes_is_not_drift()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var protocol = new OsduRecordProtocol(rig.Client, Rig.Options);

        // A new job reads nothing and records the hash of what the flow owns of it.
        var first = await protocol.DeliverAsync(Work(Job()));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.DoesNotContain(platform.Calls, c => c.Method == HttpMethod.Get);
        Assert.Equal(OwnedContent.Hash(Job(), RunState), first.Returned[OwnedContent.HashValue]);
        var state = first.Returned;

        // EDS runs the job and writes its run state: a newer version whose only changes are EDS's is not drift.
        var edsVersion = RunEds(platform, "2026-09-17T10:00:00Z", "source:master-data--Well:w-9");
        IDeliveryProtocol verifying = protocol;
        var results = await verifying.VerifyBatchAsync([Request(first.TargetVersion, state), new VerifyRequest(JobId, first.TargetVersion)]);
        Assert.Equal(VerifyOutcome.Match, results[0].Outcome);
        Assert.Equal(edsVersion, results[0].ObservedVersion);
        Assert.Contains("changed only data keys another system writes (LastSuccessfulRunDateUTC, FailedRecords, CreateTimeMax), which is not drift", results[0].Detail, StringComparison.Ordinal);

        // Without the recorded hash, the version alone speaks, as it always has.
        Assert.Equal(VerifyOutcome.Drifted, results[1].Outcome);

        // A rewrite of the job carries EDS's run state from the version storage holds, not the one the source renders.
        var renamed = Job(name: "Wells, renamed");
        var second = await protocol.DeliverAsync(Work(renamed, existing: first.TargetVersion, state: state));
        Assert.True(second.Succeeded, second.Failure?.Message);
        var put = JsonNode.Parse(platform.Calls.Last(c => c.Method == HttpMethod.Put).Body!)![0]!["data"]!;
        Assert.Equal("Wells, renamed", put["Name"]!.GetValue<string>());
        Assert.Equal("2026-09-17T10:00:00Z", put["LastSuccessfulRunDateUTC"]!.GetValue<string>());
        Assert.Equal("source:master-data--Well:w-9", put["FailedRecords"]![0]!.GetValue<string>());
        Assert.Equal("2026-09-17T10:00:00Z", put["CreateTimeMax"]!.GetValue<string>());
        Assert.Equal(OwnedContent.Hash(renamed, RunState), second.Returned[OwnedContent.HashValue]);
        Assert.NotEqual(state[OwnedContent.HashValue], second.Returned[OwnedContent.HashValue]);

        // An operator stopping the job in OSDU changes what the flow delivered: that is drift.
        RunEds(platform, "2026-09-18T10:00:00Z", "source:master-data--Well:w-10");
        var stopped = (JsonObject)platform.Records[JobId].DeepClone();
        stopped["data"]!["ActiveIndicator"] = false;
        platform.Put(stopped);
        var drift = await verifying.VerifyBatchAsync([Request(second.TargetVersion, second.Returned)]);
        Assert.Equal(VerifyOutcome.Drifted, drift[0].Outcome);

        Assert.Equal(
            ["core/storage GET /records/{id}", "core/storage POST /query/records", "core/storage PUT /records"],
            OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage));
    }

    [Fact]
    public async Task A_storage_write_records_the_hash_of_what_it_wrote_and_a_verify_that_cannot_read_the_record_decides_nothing()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var protocol = new OsduRecordProtocol(rig.Client, Rig.Options);

        // Storage keeps what a plain write sends, so the write records the hash of it even with no keys to carry forward.
        var registry = await protocol.DeliverAsync(Work(Registry()));
        Assert.True(registry.Succeeded, registry.Failure?.Message);
        Assert.Equal(OwnedContent.Hash(Registry(), []), registry.Returned[OwnedContent.HashValue]);
        Assert.Equal("[]", registry.Returned[OwnedContent.ExcludedValue]);

        // A record an earlier delivery recorded a hash for, leaving out keys it no longer carries, gets the hash of this write.
        var earlier = new Dictionary<string, string>(StringComparer.Ordinal) { [OwnedContent.HashValue] = "old", [OwnedContent.ExcludedValue] = """["Owned"]""" };
        var rewritten = await protocol.DeliverAsync(Work(Registry(), existing: registry.TargetVersion, state: earlier));
        Assert.Equal(OwnedContent.Hash(Registry(), []), rewritten.Returned[OwnedContent.HashValue]);
        Assert.Equal("[]", rewritten.Returned[OwnedContent.ExcludedValue]);

        // The header read answers, the whole-record read does not: the moved version is left undecided, not called drift.
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            "/query/records",
            hit => hit == 0
                ? FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"records":[{"id":"{{JobId}}","version":9}]}""")
                : FakeHttpHandler.Json(HttpStatusCode.InternalServerError, """{"code":500,"reason":"Server error"}"""));
        using var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } }, Secrets, TimeProvider.System, handler, allowLoopback: true);
        var client = new OsduHttpClient(runtime, "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Partition });
        IDeliveryProtocol failing = new OsduRecordProtocol(client, Rig.Options);
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        OwnedContent.Record(state, Job(), RunState, state);
        var result = Assert.Single(await failing.VerifyBatchAsync([Request(8, state)]));
        Assert.Equal(VerifyOutcome.Error, result.Outcome);
        Assert.Equal(9, result.ObservedVersion);
        Assert.Contains("observed version 9, ledger holds 8; the record could not be read to tell whether the content the flow wrote changed, or only the version moved", result.Detail, StringComparison.Ordinal);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task A_data_job_written_by_manifest_carries_the_run_state_eds_wrote_into_the_manifest()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        using var rig = new Rig(platform);
        var protocol = new OsduManifestProtocol(rig.Client, Rig.Options, Samples.Logger<OsduManifestProtocol>());

        var first = await protocol.DeliverAsync(Work(Job()));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.Equal(OwnedContent.Hash(Job(), RunState), first.Returned[OwnedContent.HashValue]);
        Assert.DoesNotContain(platform.Calls, c => c.Body?.Contains("data.LastSuccessfulRunDateUTC", StringComparison.Ordinal) == true);

        RunEds(platform, "2026-09-17T10:00:00Z", "source:master-data--Well:w-9");
        var renamed = Job(name: "Wells, renamed");
        var second = await protocol.DeliverAsync(Work(renamed, existing: first.TargetVersion, state: first.Returned));
        Assert.True(second.Succeeded, second.Failure?.Message);

        // The run-state keys are read in one projected read, and the manifest's copy of the job carries EDS's values.
        var read = platform.Calls.Single(c => c.Body?.Contains("data.LastSuccessfulRunDateUTC", StringComparison.Ordinal) == true);
        Assert.Equal(
            new[] { "data.LastSuccessfulRunDateUTC", "data.FailedRecords", "data.CreateTimeMax" },
            JsonNode.Parse(read.Body!)!["attributes"]!.AsArray().Select(a => a!.GetValue<string>()));
        var manifested = platform.Runs[1].Context["manifest"]!["MasterData"]![0]!["data"]!;
        Assert.Equal("Wells, renamed", manifested["Name"]!.GetValue<string>());
        Assert.Equal("2026-09-17T10:00:00Z", manifested["LastSuccessfulRunDateUTC"]!.GetValue<string>());
        Assert.Equal("source:master-data--Well:w-9", manifested["FailedRecords"]![0]!.GetValue<string>());
        Assert.Equal("2026-09-17T10:00:00Z", platform.Records[JobId]["data"]!["LastSuccessfulRunDateUTC"]!.GetValue<string>());

        // EDS's next run is not drift for a job the manifest wrote either.
        RunEds(platform, "2026-09-18T10:00:00Z", "source:master-data--Well:w-10");
        IDeliveryProtocol verifying = protocol;
        var verified = Assert.Single(await verifying.VerifyBatchAsync([Request(second.TargetVersion, second.Returned)]));
        Assert.Equal(VerifyOutcome.Match, verified.Outcome);

        OsduContracts.AssertConform(platform.Calls, FakeOsduPlatform.ToSignedLocation, OsduContracts.ContextValueTyping, OsduContracts.Workflow, OsduContracts.Storage);
    }

    [Fact]
    public async Task A_data_job_that_anchors_an_eds_fetch_keeps_its_run_state_and_the_fetch_s_write_is_not_drift()
    {
        var platform = new FakeOsduPlatform();
        var fetches = 0;
        platform.Register("eds_ingest", new FakeOsduPlatform.Script
        {
            // The fetch the job names writes the job's run state, as EDS does after a run.
            Effect = (p, run) =>
            {
                Assert.Equal(JobId, run.Context["connectedSourceDataJobId"]!.GetValue<string>());
                fetches++;
                RunEds(p, string.Create(CultureInfo.InvariantCulture, $"2026-09-17T1{fetches}:00:00Z"), string.Create(CultureInfo.InvariantCulture, $"source:master-data--Well:w-{fetches}"));
            },
        });
        using var rig = new Rig(platform);
        var protocol = new OsduWorkflowProtocol(
            rig.Client,
            Rig.Options,
            new WorkflowRoute
            {
                Anchor = WorkflowAnchor.Storage,
                Stages = [new WorkflowStage { Workflow = "eds_ingest", Context = (JsonObject)JsonNode.Parse("""{ "connectedSourceDataJobId": "{record:id}" }""")! }],
            },
            Samples.Logger<OsduWorkflowProtocol>(),
            Secrets);

        // The job is written, the fetch runs and writes its run state, and the ledger holds the version it left.
        var first = await protocol.DeliverAsync(Work(Job()) with { DeliverPayload = true, ForcedParts = new HashSet<string>(StringComparer.Ordinal) { PayloadParts.Workflow } });
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.Equal(platform.Records[JobId]["version"]!.GetValue<long>(), first.TargetVersion);
        Assert.Equal(OwnedContent.Hash(Job(), RunState), first.Returned[OwnedContent.HashValue]);
        Assert.Equal("2026-09-17T11:00:00Z", platform.Records[JobId]["data"]!["LastSuccessfulRunDateUTC"]!.GetValue<string>());

        // The scheduler runs the job again later: the version EDS writes is not drift.
        RunEds(platform, "2026-09-18T01:00:00Z", "source:master-data--Well:w-scheduled");
        IDeliveryProtocol verifying = protocol;
        Assert.Equal(VerifyOutcome.Match, Assert.Single(await verifying.VerifyBatchAsync([Request(first.TargetVersion, first.Returned)])).Outcome);

        // A rewrite of the job keeps the run state, and a try that resumes past the anchor keeps the anchor's hash.
        platform.Workflows["eds_ingest"] = new FakeOsduPlatform.Script { Terminal = "failed" };
        var steps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var renamed = Job(name: "Wells, renamed");
        var failed = await protocol.DeliverAsync(Work(renamed, existing: first.TargetVersion, state: first.Returned, steps: steps) with { DeliverPayload = true });
        Assert.False(failed.Succeeded);
        var put = JsonNode.Parse(platform.Calls.Last(c => c.Method == HttpMethod.Put).Body!)![0]!["data"]!;
        Assert.Equal("Wells, renamed", put["Name"]!.GetValue<string>());
        Assert.Equal("2026-09-18T01:00:00Z", put["LastSuccessfulRunDateUTC"]!.GetValue<string>());
        Assert.Equal(OwnedContent.Hash(renamed, RunState), steps[OsduWorkflowProtocol.AnchorStep][OwnedContent.HashValue]);

        platform.Workflows["eds_ingest"] = new FakeOsduPlatform.Script();
        var puts = platform.Calls.Count(c => c.Method == HttpMethod.Put);
        var resumed = await protocol.DeliverAsync(Work(renamed, existing: first.TargetVersion, state: first.Returned, steps: steps) with { DeliverPayload = true });
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(puts, platform.Calls.Count(c => c.Method == HttpMethod.Put));
        Assert.Equal(OwnedContent.Hash(renamed, RunState), resumed.Returned[OwnedContent.HashValue]);

        OsduContracts.AssertConform(
            platform.Calls, FakeOsduPlatform.ToSignedLocation, OsduContracts.ContextValueTyping, OsduContracts.Workflow, OsduContracts.Storage);
    }
}

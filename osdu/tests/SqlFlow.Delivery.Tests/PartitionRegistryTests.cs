using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The partition registry (osdu/docs/reference/concepts/partitions.md, The partition registry), on SQL Server:
/// registering partitions, the one default the first registration takes and a later one is given, describing and removing
/// them, the refusals that keep one default while partitions are registered, and the registry as a run reads it. Every
/// test starts from an empty module.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class PartitionRegistryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly OsduTestDatabase _module = new();

    public void Dispose() => _module.Dispose();

    private DeliveryPartitionRegistry Registry() => new(_module.CreateDbContext);

    [Fact]
    public async Task The_first_partition_registered_becomes_the_default_and_a_later_one_only_when_asked()
    {
        var registry = Registry();

        var dev = await registry.AddAsync(" dev ", "Development platform", makeDefault: false, "tester", Now);
        var test = await registry.AddAsync("test", null, makeDefault: false, "tester", Now);

        Assert.Equal(("dev", true, "Development platform", "tester"), (dev.Name, dev.IsDefault, dev.Description, dev.CreatedBy));
        Assert.False(test.IsDefault);

        var read = await registry.ReadAsync();
        Assert.Equal(["dev", "test"], read.Names);
        Assert.Equal("dev", read.Default);

        // Asked for, a later partition takes the default from the one that had it.
        await registry.AddAsync("prod", "Production", makeDefault: true, "tester", Now);
        read = await registry.ReadAsync();
        Assert.Equal("prod", read.Default);
        Assert.Single(read.All, p => p.IsDefault);
    }

    [Fact]
    public async Task A_partition_is_registered_once_whatever_its_case()
    {
        var registry = Registry();
        await registry.AddAsync("dev", null, makeDefault: false, "tester", Now);

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => registry.AddAsync("DEV", null, makeDefault: false, "tester", Now));

        Assert.Contains("'dev' is registered already", ex.Message, StringComparison.Ordinal);
        Assert.Equal("dev", (await registry.ReadAsync()).Find("Dev"));
    }

    [Theory]
    [InlineData("${env:OSDU_DATA_PARTITION}")]
    [InlineData("dev partition")]
    [InlineData("*")]
    public async Task A_name_that_is_no_partition_id_is_refused(string name)
    {
        var ex = await Assert.ThrowsAsync<FlowValidationException>(() => Registry().AddAsync(name, null, makeDefault: false, "tester", Now));

        Assert.Contains("The partition", ex.Message, StringComparison.Ordinal);
        Assert.Empty((await Registry().ReadAsync()).All);
    }

    [Fact]
    public async Task Describing_sets_and_clears_what_a_partition_is_for_and_says_who_changed_it()
    {
        var registry = Registry();
        await registry.AddAsync("dev", null, makeDefault: false, "tester", Now);

        var described = await registry.DescribeAsync("DEV", "  Shared development  ", "operator", Now.AddHours(1));
        Assert.Equal(("Shared development", "operator", Now.AddHours(1), "tester"), (described.Description, described.UpdatedBy, described.UpdatedUtc, described.CreatedBy));

        Assert.Null((await registry.DescribeAsync("dev", "   ", "operator", Now)).Description);

        var tooLong = await Assert.ThrowsAsync<FlowValidationException>(
            () => registry.DescribeAsync("dev", new string('d', DeliveryPartition.MaxDescriptionLength + 1), "operator", Now));
        Assert.Contains($"at most {DeliveryPartition.MaxDescriptionLength} characters", tooLong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partition_keeps_three_cache_versions_until_an_admin_sets_another_number_and_says_who_set_it()
    {
        var registry = Registry();
        var added = await registry.AddAsync("dev", null, makeDefault: false, "tester", Now);
        Assert.Equal(CacheRetention.DefaultVersions, added.CacheVersionsKept);

        var kept = await registry.KeepCacheVersionsAsync("DEV", 5, "admin", Now.AddHours(1));
        Assert.Equal((5, "admin", Now.AddHours(1)), (kept.CacheVersionsKept, kept.UpdatedBy, kept.UpdatedUtc));

        // Setting the number it keeps already changes nothing, not even who set it last.
        var again = await registry.KeepCacheVersionsAsync("dev", 5, "someone", Now.AddHours(2));
        Assert.Equal(("admin", Now.AddHours(1)), (again.UpdatedBy, again.UpdatedUtc));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.KeepCacheVersionsAsync("dev", CacheRetention.MinVersions - 1, "admin", Now));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.KeepCacheVersionsAsync("dev", CacheRetention.MaxVersions + 1, "admin", Now));
        await Assert.ThrowsAsync<PartitionNotRegisteredException>(() => registry.KeepCacheVersionsAsync("prod", 5, "admin", Now));
    }

    [Fact]
    public async Task A_partition_the_configuration_names_still_takes_the_number_of_cache_versions_it_keeps()
    {
        await using (var db = _module.CreateDbContext())
        {
            db.DeliveryPartitions.Add(new DeliveryPartition
            {
                Name = "dev", IsDefault = true, IsConfigured = true, CreatedUtc = Now, CreatedBy = "configuration", UpdatedUtc = Now, UpdatedBy = "configuration",
            });
            await db.SaveChangesAsync();
        }

        var registry = Registry();
        await Assert.ThrowsAsync<SetByConfigurationException>(() => registry.DescribeAsync("dev", "x", "admin", Now));
        Assert.Equal(4, (await registry.KeepCacheVersionsAsync("dev", 4, "admin", Now)).CacheVersionsKept);
    }

    [Fact]
    public async Task A_partition_that_is_not_registered_is_named_in_the_refusal_with_how_to_register_it()
    {
        var registry = Registry();
        await registry.AddAsync("dev", null, makeDefault: false, "tester", Now);

        var ex = await Assert.ThrowsAsync<PartitionNotRegisteredException>(() => registry.MakeDefaultAsync("prod", "tester", Now));

        Assert.Contains("'prod' is not registered", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sqlflow partition add prod", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the registered partitions are dev", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PartitionNotRegisteredException>(() => registry.DescribeAsync("prod", "x", "tester", Now));
        Assert.False(await registry.RemoveAsync("prod"));
    }

    [Fact]
    public async Task Making_another_partition_the_default_moves_the_mark()
    {
        var registry = Registry();
        await registry.AddAsync("dev", null, makeDefault: false, "tester", Now);
        await registry.AddAsync("test", null, makeDefault: false, "tester", Now);

        var test = await registry.MakeDefaultAsync("TEST", "operator", Now.AddHours(1));
        Assert.Equal(("test", true, "operator"), (test.Name, test.IsDefault, test.UpdatedBy));

        var read = await registry.ReadAsync();
        Assert.Equal("test", read.Default);
        Assert.Single(read.All, p => p.IsDefault);

        // Making the default the default again changes nothing.
        Assert.Equal("operator", (await registry.MakeDefaultAsync("test", "someone else", Now.AddHours(2))).UpdatedBy);
    }

    [Fact]
    public async Task The_default_is_removed_only_when_it_is_the_last_partition_and_nothing_else_goes_with_a_removal()
    {
        var registry = Registry();
        await registry.AddAsync("dev", null, makeDefault: false, "tester", Now);
        await registry.AddAsync("test", null, makeDefault: false, "tester", Now);
        await using (var seed = _module.CreateDbContext())
        {
            seed.DeliveryCacheMembers.Add(new DeliveryCacheMember { Scope = "test", TypeName = "CurveDictionary", RecordId = "GR", FlowName = "welldb-lookups-00-cache" });
            await seed.SaveChangesAsync();
        }

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => registry.RemoveAsync("dev"));
        Assert.Contains("make another partition the default before removing it", refused.Message, StringComparison.Ordinal);

        Assert.True(await registry.RemoveAsync("Test"));
        Assert.True(await registry.RemoveAsync("dev"));
        Assert.Empty((await registry.ReadAsync()).All);

        // What was kept under a removed partition stays.
        await using var db = _module.CreateDbContext();
        Assert.True(await db.DeliveryCacheMembers.AnyAsync(m => m.Scope == "test"));
    }

    [Fact]
    public async Task The_database_holds_one_default_even_when_written_behind_the_registry_s_back()
    {
        await using var db = _module.CreateDbContext();
        db.DeliveryPartitions.Add(new DeliveryPartition { Name = "dev", IsDefault = true, CreatedUtc = Now, CreatedBy = "t", UpdatedUtc = Now, UpdatedBy = "t" });
        db.DeliveryPartitions.Add(new DeliveryPartition { Name = "test", IsDefault = true, CreatedUtc = Now, CreatedBy = "t", UpdatedUtc = Now, UpdatedBy = "t" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_host_without_the_module_s_database_says_where_the_registry_lives()
    {
        var registry = new DeliveryPartitionRegistry(null);

        Assert.False(registry.Available);
        var ex = await Assert.ThrowsAsync<DeliveryException>(() => registry.ReadAsync());
        Assert.Contains("module's database", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DeliveryException>(() => UnavailablePartitionRegistry.Instance.ReadAsync());
    }

    [Fact]
    public void A_registry_read_with_two_defaults_is_refused()
    {
        var ex = Assert.Throws<DeliveryException>(() => new RegisteredPartitions(
            [new RegisteredPartition("dev", null, true), new RegisteredPartition("test", null, true)]));

        Assert.Contains("dev, test", ex.Message, StringComparison.Ordinal);
    }
}

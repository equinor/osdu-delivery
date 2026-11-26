using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Data;

public static partial class DeliveryModel
{
    /// <summary>Configures the mappings, search terms and their refinements, interfaces, ledger flows and templates of the repositories.</summary>
    private static void ConfigureRepositories(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeliveryMapping>(e =>
        {
            e.ToTable("Mapping", SchemaName);
            e.HasKey(m => m.Id);
            e.Property(m => m.Reference).HasMaxLength(200).IsRequired();
            e.Property(m => m.Name).HasMaxLength(150).IsRequired();
            e.Property(m => m.Version).HasMaxLength(50).IsRequired();
            e.Property(m => m.Kind).HasMaxLength(200).IsRequired();
            e.Property(m => m.TemplateVersion).HasMaxLength(64).IsRequired();
            e.Property(m => m.TemplatePath).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired().HasDefaultValue(string.Empty);
            e.Property(m => m.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(m => m.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(m => m.Yaml).IsRequired();
            e.Property(m => m.SummaryJson).IsRequired();
            e.Property(m => m.Status).HasMaxLength(16).IsRequired();
            e.Property(m => m.Message).HasMaxLength(4000);
            e.HasIndex(m => new { m.RepoId, m.Reference }).IsUnique();
            e.HasIndex(m => new { m.RepoId, m.Kind });
        });

        modelBuilder.Entity<DeliverySearchTerm>(e =>
        {
            e.ToTable("SearchTerm", SchemaName);
            e.HasKey(t => t.Id);
            e.Property(t => t.TermKey).HasMaxLength(SearchTermKeyLength).IsRequired();
            e.Property(t => t.Source).HasMaxLength(DeliverySearchTerm.MaxSourceLength).IsRequired();
            e.Property(t => t.EntityType).HasMaxLength(DeliverySearchTerm.MaxEntityTypeLength).IsRequired();
            e.Property(t => t.Column).HasMaxLength(DeliverySearchTerm.MaxColumnLength).IsRequired();
            e.Property(t => t.RoutesJson).IsRequired();
            e.Property(t => t.FlowsJson).IsRequired();
            e.HasIndex(t => new { t.RepoId, t.TermId, t.EntityType }).IsUnique();
            // The search terms page and the explorer list the terms of one entity type, from every repository.
            e.HasIndex(t => new { t.EntityType, t.TermId });
        });

        modelBuilder.Entity<DeliverySearchTermRefinement>(e =>
        {
            e.ToTable("SearchTermRefinement", SchemaName);
            e.HasKey(r => r.TermId);
            e.Property(r => r.TermKey).HasMaxLength(SearchTermKeyLength).IsRequired();
            e.Property(r => r.EntityType).HasMaxLength(DeliverySearchTerm.MaxEntityTypeLength).IsRequired();
            e.Property(r => r.Name).HasMaxLength(DeliverySearchTermRefinement.MaxNameLength);
            e.Property(r => r.Route).HasMaxLength(DeliverySearchTermRefinement.MaxRouteLength);
            e.Property(r => r.Note).HasMaxLength(DeliverySearchTermRefinement.MaxNoteLength);
            e.Property(r => r.UpdatedBy).HasMaxLength(200).IsRequired();
            // A name is the term's within its entity type: a rename checks it is free there.
            e.HasIndex(r => new { r.EntityType, r.Name });
        });

        modelBuilder.Entity<DeliveryInterface>(e =>
        {
            e.ToTable("Interface", SchemaName);
            e.HasKey(i => i.Id);
            e.Property(i => i.FlowName).HasMaxLength(400).IsRequired();
            e.Property(i => i.Interface).HasMaxLength(DeliveryInterface.MaxInterfaceLength).IsRequired();
            e.Property(i => i.LedgerName).HasMaxLength(200).IsRequired();
            e.Property(i => i.Route).HasMaxLength(32).IsRequired();
            e.Property(i => i.RouteReason).HasMaxLength(1000);
            e.Property(i => i.MappingReference).HasMaxLength(200).IsRequired();
            e.Property(i => i.CacheVersion).HasMaxLength(64);
            e.Property(i => i.Kind).HasMaxLength(200).IsRequired();
            e.Property(i => i.RecordObject).HasMaxLength(400).IsRequired();
            e.Property(i => i.AfterJson).HasMaxLength(4000).IsRequired();
            e.Property(i => i.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(i => i.Partition).HasMaxLength(200).IsRequired();
            e.Property(i => i.TargetPartition).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);
            e.Property(i => i.DocumentHash).HasMaxLength(64);
            e.HasIndex(i => new { i.RepoId, i.FlowName, i.Interface, i.Partition }).IsUnique();
            // A record's page, a submission's page and the record search find the pipeline behind a ledger identity.
            e.HasIndex(i => i.LedgerFlowId);
        });

        modelBuilder.Entity<DeliveryLedgerFlow>(e =>
        {
            e.ToTable("LedgerFlow", SchemaName);
            e.HasKey(d => d.Id);
            e.Property(d => d.FlowName).HasMaxLength(400).IsRequired();
            e.Property(d => d.Kind).HasMaxLength(DeliveryLedgerFlow.MaxKindLength).IsRequired();
            e.Property(d => d.DocumentHash).HasMaxLength(64).IsRequired();
            e.Property(d => d.RelativePath).HasMaxLength(1000).IsRequired();
            e.HasIndex(d => new { d.RepoId, d.FlowName }).IsUnique();
            // Whether a declared flow keeps the ledgers of a kind and name: the orphaned ledgers, and the pipeline behind one.
            e.HasIndex(d => new { d.Kind, d.FlowName });
        });

        modelBuilder.Entity<DeliveryRepositoryTemplate>(e =>
        {
            e.ToTable("RepositoryTemplate", SchemaName);
            e.HasKey(t => t.Id);
            e.Property(t => t.Path).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired();
            e.Property(t => t.Folder).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired();
            e.Property(t => t.Kind).HasMaxLength(200).IsRequired();
            e.Property(t => t.Version).HasMaxLength(64).IsRequired();
            e.Property(t => t.Problem).HasMaxLength(DeliveryRepositoryTemplate.MaxProblemLength);
            e.Property(t => t.Parent).HasMaxLength(200);
            // A sync reconciles one repository's files, and a page reads them to settle that repository's documents.
            e.HasIndex(t => new { t.RepoId, t.Path }).IsUnique();
        });
    }
}

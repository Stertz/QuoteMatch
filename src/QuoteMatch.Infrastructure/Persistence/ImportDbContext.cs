using Microsoft.EntityFrameworkCore;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed class ImportDbContext(DbContextOptions<ImportDbContext> options) : DbContext(options)
{
    internal DbSet<TemplateVersionRow> Templates => Set<TemplateVersionRow>();
    internal DbSet<AnalysisRevisionRow> Analyses => Set<AnalysisRevisionRow>();
    internal DbSet<PurchasePreparationRow> Purchases => Set<PurchasePreparationRow>();
    internal DbSet<OrderReconciliationRow> OrderReconciliations => Set<OrderReconciliationRow>();
    internal DbSet<OrderOperationRow> OrderOperations => Set<OrderOperationRow>();
    internal DbSet<SynchronizationOperationRow> SynchronizationOperations => Set<SynchronizationOperationRow>();
    internal DbSet<QuotationRow> Quotations => Set<QuotationRow>();
    internal DbSet<QuotationInputRevisionRow> QuotationInputs => Set<QuotationInputRevisionRow>();
    internal DbSet<ConfigurationRow> Configuration => Set<ConfigurationRow>();
    internal DbSet<ProposalRow> Proposals => Set<ProposalRow>();
    internal DbSet<ProductMappingRow> ProductMappings => Set<ProductMappingRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SynchronizationOperationRow>(entity =>
        {
            entity.ToTable("SynchronizationOperations"); entity.HasKey(row => row.Id);
            entity.Property(row => row.DataJson).IsRequired(); entity.Property(row => row.State).IsConcurrencyToken();
            entity.Property(row => row.CatalogIdentity).IsRequired(); entity.Property(row => row.TargetId).IsRequired();
            entity.HasOne<QuotationRow>().WithMany().HasForeignKey(row => row.QuotationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<QuotationInputRevisionRow>(entity =>
        {
            entity.ToTable("QuotationInputRevisions"); entity.HasKey(row => new { row.QuotationId, row.Version });
            entity.Property(row => row.DataJson).IsRequired();
            entity.HasOne<QuotationRow>().WithMany().HasForeignKey(row => row.QuotationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<OrderReconciliationRow>(entity =>
        {
            entity.ToTable("OrderReconciliations"); entity.HasKey(row => new { row.QuotationId, row.ProposalId, row.Version });
            entity.Property(row => row.DataJson).IsRequired();
            entity.HasOne<QuotationRow>().WithMany().HasForeignKey(row => row.QuotationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<OrderOperationRow>(entity =>
        {
            entity.ToTable("OrderOperations"); entity.HasKey(row => row.Id);
            entity.Property(row => row.DataJson).IsRequired(); entity.Property(row => row.State).IsConcurrencyToken();
            entity.HasOne<QuotationRow>().WithMany().HasForeignKey(row => row.QuotationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProductMappingRow>(entity =>
        {
            entity.ToTable("ProductMappingRevisions");
            entity.HasKey(row => new { row.CatalogIdentity, row.SourceKey, row.Version });
            entity.Property(row => row.DataJson).IsRequired();
        });
        modelBuilder.Entity<PurchasePreparationRow>(entity =>
        {
            entity.ToTable("PurchasePreparations"); entity.HasKey(row => new { row.QuotationId, row.Version });
            entity.Property(row => row.DataJson).IsRequired();
            entity.HasOne<QuotationRow>().WithMany().HasForeignKey(row => row.QuotationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AnalysisRevisionRow>(entity =>
        {
            entity.ToTable("AnalysisRevisions"); entity.HasKey(row => new { row.QuotationId, row.Version });
            entity.Property(row => row.DataJson).IsRequired();
            entity.HasOne<QuotationRow>().WithMany().HasForeignKey(row => row.QuotationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<QuotationRow>(entity =>
        {
            entity.ToTable("Quotations"); entity.HasKey(row => row.Id);
            entity.Property(row => row.Name).IsRequired(); entity.Property(row => row.Notes).IsRequired();
            entity.Property(row => row.Revision).IsConcurrencyToken();
        });
        modelBuilder.Entity<ConfigurationRow>(entity =>
        {
            entity.ToTable("Configuration"); entity.HasKey(row => row.Key); entity.Property(row => row.DataJson).IsRequired();
        });
        modelBuilder.Entity<TemplateVersionRow>(entity =>
        {
            entity.ToTable("TemplateVersions");
            entity.HasKey(row => new { row.Id, row.Version });
            entity.Property(row => row.Name).IsRequired();
            entity.Property(row => row.DataJson).IsRequired();
        });
        modelBuilder.Entity<ProposalRow>(entity =>
        {
            entity.ToTable("Proposals");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.FileName).IsRequired();
            entity.Property(row => row.SupplierName).IsRequired();
            entity.Property(row => row.TemplateName).IsRequired();
            entity.Property(row => row.ReportedItemTotalText).IsRequired();
            entity.Property(row => row.DataJson).IsRequired();
            entity.Property(row => row.OriginalPdf).IsRequired();
            entity.Property(row => row.SourceSha256).IsRequired();
            entity.HasIndex(row => row.SourceSha256);
            entity.HasOne<QuotationRow>().WithMany().HasForeignKey(row => row.QuotationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

internal sealed class OrderReconciliationRow
{
    public Guid QuotationId { get; set; }
    public Guid ProposalId { get; set; }
    public int Version { get; set; }
    public string DataJson { get; set; } = string.Empty;
}
internal sealed class OrderOperationRow
{
    public Guid Id { get; set; }
    public Guid QuotationId { get; set; }
    public QuoteMatch.Domain.Purchasing.OrderOperationState State { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string DataJson { get; set; } = string.Empty;
    public string? OrderId { get; set; }
}
internal sealed class SynchronizationOperationRow
{
    public Guid Id { get; set; }
    public Guid QuotationId { get; set; }
    public QuoteMatch.Domain.Purchasing.OrderOperationState State { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string DataJson { get; set; } = string.Empty;
    public string CatalogIdentity { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string? OrderId { get; set; }
}

internal sealed class ProductMappingRow
{
    public string CatalogIdentity { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public int Version { get; set; }
    public string DataJson { get; set; } = string.Empty;
}

internal sealed class TemplateVersionRow
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataJson { get; set; } = string.Empty;
}

internal sealed class ProposalRow
{
    public Guid Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public string ReportedItemTotalText { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string DataJson { get; set; } = string.Empty;
    public Guid? QuotationId { get; set; }
    public byte[] OriginalPdf { get; set; } = [];
}

internal sealed class QuotationRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public QuoteMatch.Domain.Quotations.QuotationState State { get; set; }
    public int Revision { get; set; }
}
internal sealed class QuotationInputRevisionRow
{
    public Guid QuotationId { get; set; }
    public int Version { get; set; }
    public string DataJson { get; set; } = string.Empty;
}
internal sealed class ConfigurationRow
{
    public string Key { get; set; } = string.Empty;
    public string DataJson { get; set; } = string.Empty;
}
internal sealed class AnalysisRevisionRow
{
    public Guid QuotationId { get; set; }
    public int Version { get; set; }
    public int InputsRevision { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string DataJson { get; set; } = string.Empty;
}
internal sealed class PurchasePreparationRow
{
    public Guid QuotationId { get; set; }
    public int Version { get; set; }
    public string DataJson { get; set; } = string.Empty;
}

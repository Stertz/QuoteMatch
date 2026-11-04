using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace QuoteMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ImportDbContext))]
[Migration("20261022000500_ProductMappings")]
public sealed class ProductMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable("ProductMappingRevisions", columns: table => new
    {
        CatalogIdentity = table.Column<string>(type: "TEXT", nullable: false),
        SourceKey = table.Column<string>(type: "TEXT", nullable: false),
        Version = table.Column<int>(type: "INTEGER", nullable: false),
        DataJson = table.Column<string>(type: "TEXT", nullable: false)
    }, constraints: table => table.PrimaryKey("PK_ProductMappingRevisions", row => new { row.CatalogIdentity, row.SourceKey, row.Version }));
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("ProductMappingRevisions");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => MappingSchema.Build(modelBuilder);
}

internal static class MappingSchema
{
    public static void Build(ModelBuilder modelBuilder)
    {
        PurchaseSchema.Build(modelBuilder);
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.ProductMappingRow", entity =>
        {
            entity.Property<string>("CatalogIdentity").HasColumnType("TEXT");
            entity.Property<string>("SourceKey").HasColumnType("TEXT");
            entity.Property<int>("Version").HasColumnType("INTEGER");
            entity.Property<string>("DataJson").IsRequired().HasColumnType("TEXT");
            entity.HasKey("CatalogIdentity", "SourceKey", "Version"); entity.ToTable("ProductMappingRevisions");
        });
    }
}

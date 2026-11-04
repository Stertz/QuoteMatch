using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace QuoteMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ImportDbContext))]
[Migration("20261022000400_PurchasePreparations")]
public sealed class PurchasePreparations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable("PurchasePreparations", columns: table => new
    {
        QuotationId = table.Column<Guid>(type: "TEXT", nullable: false),
        Version = table.Column<int>(type: "INTEGER", nullable: false),
        DataJson = table.Column<string>(type: "TEXT", nullable: false)
    }, constraints: table =>
    {
        table.PrimaryKey("PK_PurchasePreparations", row => new { row.QuotationId, row.Version });
        table.ForeignKey("FK_PurchasePreparations_Quotations_QuotationId", row => row.QuotationId, "Quotations", "Id", onDelete: ReferentialAction.Cascade);
    });
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PurchasePreparations");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => PurchaseSchema.Build(modelBuilder);
}
internal static class PurchaseSchema
{
    public static void Build(ModelBuilder modelBuilder)
    {
        AnalysisSchema.Build(modelBuilder);
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.PurchasePreparationRow", entity =>
        {
            entity.Property<Guid>("QuotationId").HasColumnType("TEXT"); entity.Property<int>("Version").HasColumnType("INTEGER");
            entity.Property<string>("DataJson").IsRequired().HasColumnType("TEXT");
            entity.HasKey("QuotationId", "Version"); entity.ToTable("PurchasePreparations");
            entity.HasOne("QuoteMatch.Infrastructure.Persistence.QuotationRow", null).WithMany().HasForeignKey("QuotationId").OnDelete(DeleteBehavior.Cascade);
        });
    }
}

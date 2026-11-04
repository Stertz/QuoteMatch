using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace QuoteMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ImportDbContext))]
[Migration("20261027000100_VersionedQuotationInputs")]
public sealed class VersionedQuotationInputs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable("QuotationInputRevisions", columns: table => new
    {
        QuotationId = table.Column<Guid>(type: "TEXT", nullable: false),
        Version = table.Column<int>(type: "INTEGER", nullable: false),
        DataJson = table.Column<string>(type: "TEXT", nullable: false)
    }, constraints: table =>
    {
        table.PrimaryKey("PK_QuotationInputRevisions", row => new { row.QuotationId, row.Version });
        table.ForeignKey("FK_QuotationInputRevisions_Quotations_QuotationId", row => row.QuotationId, "Quotations", "Id", onDelete: ReferentialAction.Cascade);
    });

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("QuotationInputRevisions");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => QuotationInputSchema.Build(modelBuilder);
}

internal static class QuotationInputSchema
{
    public static void Build(ModelBuilder modelBuilder)
    {
        OrderSchema.Build(modelBuilder);
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.QuotationInputRevisionRow", entity =>
        {
            entity.Property<Guid>("QuotationId").HasColumnType("TEXT"); entity.Property<int>("Version").HasColumnType("INTEGER");
            entity.Property<string>("DataJson").IsRequired().HasColumnType("TEXT");
            entity.HasKey("QuotationId", "Version"); entity.ToTable("QuotationInputRevisions");
            entity.HasOne("QuoteMatch.Infrastructure.Persistence.QuotationRow", null).WithMany().HasForeignKey("QuotationId").OnDelete(DeleteBehavior.Cascade);
        });
    }
}

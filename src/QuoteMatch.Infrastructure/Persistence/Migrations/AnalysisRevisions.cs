using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace QuoteMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ImportDbContext))]
[Migration("20261021000300_AnalysisRevisions")]
public sealed class AnalysisRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable("AnalysisRevisions", columns: table => new
    {
        QuotationId = table.Column<Guid>(type: "TEXT", nullable: false),
        Version = table.Column<int>(type: "INTEGER", nullable: false),
        InputsRevision = table.Column<int>(type: "INTEGER", nullable: false),
        CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
        DataJson = table.Column<string>(type: "TEXT", nullable: false)
    }, constraints: table =>
    {
        table.PrimaryKey("PK_AnalysisRevisions", row => new { row.QuotationId, row.Version });
        table.ForeignKey("FK_AnalysisRevisions_Quotations_QuotationId", row => row.QuotationId, "Quotations", "Id", onDelete: ReferentialAction.Cascade);
    });
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("AnalysisRevisions");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AnalysisSchema.Build(modelBuilder);
}
internal static class AnalysisSchema
{
    public static void Build(ModelBuilder modelBuilder)
    {
        QuotationSchema.Build(modelBuilder);
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.AnalysisRevisionRow", entity =>
        {
            entity.Property<Guid>("QuotationId").HasColumnType("TEXT"); entity.Property<int>("Version").HasColumnType("INTEGER");
            entity.Property<int>("InputsRevision").HasColumnType("INTEGER"); entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("DataJson").IsRequired().HasColumnType("TEXT"); entity.HasKey("QuotationId", "Version"); entity.ToTable("AnalysisRevisions");
            entity.HasOne("QuoteMatch.Infrastructure.Persistence.QuotationRow", null).WithMany().HasForeignKey("QuotationId").OnDelete(DeleteBehavior.Cascade);
        });
    }
}

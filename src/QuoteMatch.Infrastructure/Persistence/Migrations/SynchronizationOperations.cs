using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace QuoteMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ImportDbContext))]
[Migration("20261027000200_SynchronizationOperations")]
public sealed class SynchronizationOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("SynchronizationOperations", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            QuotationId = table.Column<Guid>(type: "TEXT", nullable: false),
            State = table.Column<int>(type: "INTEGER", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            DataJson = table.Column<string>(type: "TEXT", nullable: false),
            CatalogIdentity = table.Column<string>(type: "TEXT", nullable: false),
            TargetId = table.Column<string>(type: "TEXT", nullable: false),
            OrderId = table.Column<string>(type: "TEXT", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_SynchronizationOperations", row => row.Id);
            table.ForeignKey("FK_SynchronizationOperations_Quotations_QuotationId", row => row.QuotationId, "Quotations", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_SynchronizationOperations_QuotationId", "SynchronizationOperations", "QuotationId");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("SynchronizationOperations");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => SynchronizationSchema.Build(modelBuilder);
}

internal static class SynchronizationSchema
{
    public static void Build(ModelBuilder modelBuilder)
    {
        QuotationInputSchema.Build(modelBuilder);
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.SynchronizationOperationRow", entity =>
        {
            entity.Property<Guid>("Id").HasColumnType("TEXT"); entity.Property<Guid>("QuotationId").HasColumnType("TEXT");
            entity.Property<int>("State").IsConcurrencyToken().HasColumnType("INTEGER"); entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            foreach (string column in new[] { "DataJson", "CatalogIdentity", "TargetId" }) entity.Property<string>(column).IsRequired().HasColumnType("TEXT");
            entity.Property<string>("OrderId").HasColumnType("TEXT"); entity.HasKey("Id"); entity.HasIndex("QuotationId"); entity.ToTable("SynchronizationOperations");
            entity.HasOne("QuoteMatch.Infrastructure.Persistence.QuotationRow", null).WithMany().HasForeignKey("QuotationId").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

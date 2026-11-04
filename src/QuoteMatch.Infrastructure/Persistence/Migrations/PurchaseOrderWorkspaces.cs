using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace QuoteMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ImportDbContext))]
[Migration("20261023000100_PurchaseOrderWorkspaces")]
public sealed class PurchaseOrderWorkspaces : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("OrderReconciliations", columns: table => new
        {
            QuotationId = table.Column<Guid>(type: "TEXT", nullable: false),
            ProposalId = table.Column<Guid>(type: "TEXT", nullable: false),
            Version = table.Column<int>(type: "INTEGER", nullable: false),
            DataJson = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_OrderReconciliations", row => new { row.QuotationId, row.ProposalId, row.Version });
            table.ForeignKey("FK_OrderReconciliations_Quotations_QuotationId", row => row.QuotationId, "Quotations", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateTable("OrderOperations", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            QuotationId = table.Column<Guid>(type: "TEXT", nullable: false),
            State = table.Column<int>(type: "INTEGER", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            DataJson = table.Column<string>(type: "TEXT", nullable: false),
            OrderId = table.Column<string>(type: "TEXT", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_OrderOperations", row => row.Id);
            table.ForeignKey("FK_OrderOperations_Quotations_QuotationId", row => row.QuotationId, "Quotations", "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_OrderOperations_QuotationId", "OrderOperations", "QuotationId");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("OrderOperations"); migrationBuilder.DropTable("OrderReconciliations");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => OrderSchema.Build(modelBuilder);
}

internal static class OrderSchema
{
    public static void Build(ModelBuilder modelBuilder)
    {
        MappingSchema.Build(modelBuilder);
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.OrderReconciliationRow", entity =>
        {
            entity.Property<Guid>("QuotationId").HasColumnType("TEXT"); entity.Property<Guid>("ProposalId").HasColumnType("TEXT");
            entity.Property<int>("Version").HasColumnType("INTEGER"); entity.Property<string>("DataJson").IsRequired().HasColumnType("TEXT");
            entity.HasKey("QuotationId", "ProposalId", "Version"); entity.ToTable("OrderReconciliations");
            entity.HasOne("QuoteMatch.Infrastructure.Persistence.QuotationRow", null).WithMany().HasForeignKey("QuotationId").OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.OrderOperationRow", entity =>
        {
            entity.Property<Guid>("Id").HasColumnType("TEXT"); entity.Property<Guid>("QuotationId").HasColumnType("TEXT");
            entity.Property<int>("State").HasColumnType("INTEGER").IsConcurrencyToken(); entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("DataJson").IsRequired().HasColumnType("TEXT"); entity.Property<string>("OrderId").HasColumnType("TEXT");
            entity.HasKey("Id"); entity.HasIndex("QuotationId"); entity.ToTable("OrderOperations");
            entity.HasOne("QuoteMatch.Infrastructure.Persistence.QuotationRow", null).WithMany().HasForeignKey("QuotationId").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

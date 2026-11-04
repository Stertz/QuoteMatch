using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace QuoteMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ImportDbContext))]
[Migration("20261021000200_QuotationWorkspace")]
public sealed class QuotationWorkspace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Quotations", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            Name = table.Column<string>(type: "TEXT", nullable: false),
            Notes = table.Column<string>(type: "TEXT", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            State = table.Column<int>(type: "INTEGER", nullable: false),
            Revision = table.Column<int>(type: "INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Quotations", row => row.Id));
        migrationBuilder.CreateTable("Configuration", columns: table => new
        {
            Key = table.Column<string>(type: "TEXT", nullable: false),
            DataJson = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Configuration", row => row.Key));
        migrationBuilder.AddColumn<Guid>("QuotationId", "Proposals", type: "TEXT", nullable: true);
        migrationBuilder.Sql("INSERT INTO Quotations (Id, Name, Notes, CreatedAtUtc, UpdatedAtUtc, State, Revision) SELECT Id, FileName, '', CreatedAtUtc, CreatedAtUtc, 0, 1 FROM Proposals;");
        migrationBuilder.Sql("UPDATE Proposals SET QuotationId = Id;");
        migrationBuilder.CreateIndex("IX_Proposals_QuotationId", "Proposals", "QuotationId");
        migrationBuilder.AddForeignKey("FK_Proposals_Quotations_QuotationId", "Proposals", "QuotationId", "Quotations", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Proposals_Quotations_QuotationId", "Proposals");
        migrationBuilder.DropIndex("IX_Proposals_QuotationId", "Proposals");
        migrationBuilder.DropColumn("QuotationId", "Proposals");
        migrationBuilder.DropTable("Quotations"); migrationBuilder.DropTable("Configuration");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => QuotationSchema.Build(modelBuilder);
}
internal static class QuotationSchema
{
    public static void Build(ModelBuilder modelBuilder)
    {
        InitialSchema.Build(modelBuilder);
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.QuotationRow", entity =>
        {
            entity.Property<Guid>("Id").HasColumnType("TEXT"); entity.Property<string>("Name").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("Notes").IsRequired().HasColumnType("TEXT"); entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("TEXT"); entity.Property<int>("State").HasColumnType("INTEGER");
            entity.Property<int>("Revision").IsConcurrencyToken().HasColumnType("INTEGER"); entity.HasKey("Id"); entity.ToTable("Quotations");
        });
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.ConfigurationRow", entity =>
        {
            entity.Property<string>("Key").HasColumnType("TEXT"); entity.Property<string>("DataJson").IsRequired().HasColumnType("TEXT");
            entity.HasKey("Key"); entity.ToTable("Configuration");
        });
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.ProposalRow", entity =>
        {
            entity.Property<Guid?>("QuotationId").HasColumnType("TEXT"); entity.HasIndex("QuotationId");
            entity.HasOne("QuoteMatch.Infrastructure.Persistence.QuotationRow", null).WithMany().HasForeignKey("QuotationId").OnDelete(DeleteBehavior.Cascade);
        });
    }
}


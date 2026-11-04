using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace QuoteMatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ImportDbContext))]
[Migration("202610210001_InitialImportStorage")]
public sealed class InitialImportStorage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("TemplateVersions", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            Version = table.Column<int>(type: "INTEGER", nullable: false),
            Name = table.Column<string>(type: "TEXT", nullable: false),
            DataJson = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_TemplateVersions", row => new { row.Id, row.Version }));
        migrationBuilder.CreateTable("Proposals", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            FileName = table.Column<string>(type: "TEXT", nullable: false),
            SupplierName = table.Column<string>(type: "TEXT", nullable: false),
            TemplateName = table.Column<string>(type: "TEXT", nullable: false),
            ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
            ReportedItemTotalText = table.Column<string>(type: "TEXT", nullable: false),
            SourceSha256 = table.Column<string>(type: "TEXT", nullable: false),
            DataJson = table.Column<string>(type: "TEXT", nullable: false),
            OriginalPdf = table.Column<byte[]>(type: "BLOB", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Proposals", row => row.Id));
        migrationBuilder.CreateIndex("IX_Proposals_SourceSha256", "Proposals", "SourceSha256");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Proposals");
        migrationBuilder.DropTable("TemplateVersions");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => InitialSchema.Build(modelBuilder);
}

[DbContext(typeof(ImportDbContext))]
public sealed class ImportDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => SynchronizationSchema.Build(modelBuilder);
}

internal static class InitialSchema
{
    public static void Build(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.13");
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.TemplateVersionRow", entity =>
        {
            entity.Property<Guid>("Id").HasColumnType("TEXT");
            entity.Property<int>("Version").HasColumnType("INTEGER");
            entity.Property<string>("Name").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("DataJson").IsRequired().HasColumnType("TEXT");
            entity.HasKey("Id", "Version");
            entity.ToTable("TemplateVersions");
        });
        modelBuilder.Entity("QuoteMatch.Infrastructure.Persistence.ProposalRow", entity =>
        {
            entity.Property<Guid>("Id").HasColumnType("TEXT");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            entity.Property<int>("ItemCount").HasColumnType("INTEGER");
            foreach (string column in new[] { "FileName", "SupplierName", "TemplateName", "ReportedItemTotalText", "SourceSha256", "DataJson" })
            {
                entity.Property<string>(column).IsRequired().HasColumnType("TEXT");
            }

            entity.Property<byte[]>("OriginalPdf").IsRequired().HasColumnType("BLOB");
            entity.HasKey("Id");
            entity.HasIndex("SourceSha256");
            entity.ToTable("Proposals");
        });
    }
}



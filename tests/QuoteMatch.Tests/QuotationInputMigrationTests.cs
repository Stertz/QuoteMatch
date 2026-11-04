using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed class QuotationInputMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.InputMigration.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private DbContextOptions<ImportDbContext> Options => new DbContextOptionsBuilder<ImportDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "imports.db") }.ToString()).Options;

    [Fact]
    public async Task Upgrade_PreservesAllPreviousRowsAndRestartsWithLegacyCalculationRules()
    {
        Directory.CreateDirectory(_directory);
        Quotation quotation = new(Guid.NewGuid(), "Synthetic legacy quotation", "Preserved notes", Now, Now, QuotationState.Finalized, 4);
        Guid captureId = Guid.NewGuid();
        CapturedItem capture = new(captureId, 1, 1, "Synthetic captured record", new Dictionary<string, string> { ["quantity"] = "2,000", ["code"] = "000.01" },
            "000.01", "Synthetic original description", 2, 200.123456789m, 400.246913578m, "RL", []);
        PdfTemplate template = new() { Id = Guid.NewGuid(), Version = 1, Name = "Synthetic template", SupplierName = "Synthetic manufacturer" };
        SavedProposal proposal = new(Guid.NewGuid(), Now, "synthetic.pdf", "synthetic-sha256", template, new([new(1, "Synthetic page")], "Synthetic/1"),
            new([capture], [], 1, 400.246913578m), [new(captureId, "000.01", "Synthetic reviewed description", 2, 200.123456789m, 400.246913578m, "RL", false, "")])
        { QuotationId = quotation.Id };
        ErpProductCandidate product = new("001.01", "Synthetic wire", "", false, "legacy-supplier", "Synthetic manufacturer", "000.01", 100, "RL", new(2026, 10, 20),
            1.987654321m, "M", "M", "synthetic-catalog", Now);
        string groupKey = ErpProductIdentity.GroupKey(product);
        ItemLink link = new(proposal.Id, captureId, groupKey, product.Name, "M", 100, product, Now);
        AnalysisState firstAnalysis = new(quotation.Id, 1, 4, Now, [link], [new(groupKey, "M", 100)]);
        AnalysisState latestAnalysis = firstAnalysis with { Version = 2, Demands = [new(groupKey, "M", 150)] };
        PurchasePreparation firstPurchase = PurchasePreparationRules.Create(new(quotation, [proposal], firstAnalysis), [new(proposal.Id, captureId)], 1, Now);
        PurchasePreparation latestPurchase = PurchasePreparationRules.Create(new(quotation, [proposal], latestAnalysis), [new(proposal.Id, captureId)], 2, Now);
        PurchaseOrderReference reference = PurchaseOrderReference.From(latestPurchase, proposal.Id);
        PurchaseOrderSnapshot order = new(product.CatalogIdentity, new("order-1", "42", product.SupplierId, "Synthetic supplier", "branch-1", "open", new(2026, 10, 23), true), "order-revision", Now,
            [new("line-1", product.ProductCode, product.Name, "RL", "M", 2, 100, 200.123456789m, 400.246913578m, 0, false)]);
        OrderReconciliation reconciliation = new(Guid.NewGuid(), 1, Now, reference, [order], true);
        OrderWritePlan plan = new(Guid.NewGuid(), OrderWriteKind.Update, reference, order, "branch-1", "open", new(2026, 10, 23), "synthetic-script", [new("line-1", null, null, 199.123456789m, false)]);
        SupplierProductMapping mapping = new(Guid.NewGuid(), 1, SupplierProductReference.From(proposal, proposal.Review[0]), product, "M", 100, ProductMappingState.Active, Now, Now);
        Dictionary<string, string> before;
        await using (ImportDbContext context = new(Options))
        {
            await context.GetService<IMigrator>().MigrateAsync("PurchaseOrderWorkspaces", Token);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Quotations (Id, Name, Notes, CreatedAtUtc, UpdatedAtUtc, State, Revision) VALUES ({quotation.Id}, {quotation.Name}, {quotation.Notes}, {Now}, {Now}, {(int)quotation.State}, {quotation.Revision})", Token);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TemplateVersions (Id, Version, Name, DataJson) VALUES ({template.Id}, {1}, {template.Name}, {LegacyJson(template)})", Token);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Proposals (Id, CreatedAtUtc, FileName, SupplierName, TemplateName, ItemCount, ReportedItemTotalText, SourceSha256, DataJson, OriginalPdf, QuotationId) VALUES ({proposal.Id}, {Now}, {proposal.FileName}, {template.SupplierName}, {template.Name}, {1}, {"400.246913578"}, {proposal.SourceSha256}, {LegacyJson(proposal)}, {new byte[] { 1, 2, 3, 4 }}, {quotation.Id})", Token);
            foreach (var analysis in new[] { firstAnalysis, latestAnalysis })
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AnalysisRevisions (QuotationId, Version, InputsRevision, CreatedAtUtc, DataJson) VALUES ({quotation.Id}, {analysis.Version}, {4}, {Now}, {LegacyJson(analysis)})", Token);
            foreach (var purchase in new[] { firstPurchase, latestPurchase })
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PurchasePreparations (QuotationId, Version, DataJson) VALUES ({quotation.Id}, {purchase.Version}, {LegacyJson(purchase)})", Token);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProductMappingRevisions (CatalogIdentity, SourceKey, Version, DataJson) VALUES ({product.CatalogIdentity}, {mapping.Source.Key}, {1}, {LegacyJson(mapping)})", Token);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderReconciliations (QuotationId, ProposalId, Version, DataJson) VALUES ({quotation.Id}, {proposal.Id}, {1}, {LegacyJson(reconciliation)})", Token);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderOperations (Id, QuotationId, State, CreatedAtUtc, DataJson, OrderId) VALUES ({plan.OperationId}, {quotation.Id}, {(int)OrderOperationState.Unknown}, {Now}, {LegacyJson(plan)}, {order.Header.Id})", Token);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Configuration (Key, DataJson) VALUES ({"erp"}, {"{\"Server\":\"synthetic-server\",\"Database\":\"synthetic-database\"}"})", Token);
            before = await ReadExistingRowsAsync(context);
        }

        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        await using (ImportDbContext context = new(Options))
        {
            Assert.False(context.Database.HasPendingModelChanges());
            var after = await ReadExistingRowsAsync(context);
            Assert.Equal(before.Count, after.Count);
            foreach (var (table, rows) in before) Assert.Equal(rows, after[table]);
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "backups"), "*.db"));
        SqliteImportStore restarted = new(_directory); await restarted.InitializeAsync(Token);
        var input = await restarted.GetAnalysisInputAsync(quotation.Id, Token);
        Assert.Equal(quotation, input.Quotation);
        Assert.Equal(0, input.Inputs.Version); Assert.Equal(QuotationPriceReference.LastPurchase, input.Inputs.PriceReference);
        Assert.Equal(ProductGroupingVersion.ProductCodeFallback, input.Inputs.GroupingVersion);
        Assert.Equal(0, input.State.QuotationInputsVersion); Assert.Equal(2, input.State.Version);
        Assert.Equal(DemandOrigin.Manual, input.State.Demands[0].Origin); Assert.Empty(input.State.Demands[0].Sources);
        Assert.Equal(ProductGroupingVersion.ProductCodeFallback, input.State.Links[0].GroupingVersion);
        Assert.Null(input.State.Links[0].Product!.Brand);
        var group = Assert.Single(ImportedQuotationAnalysis.Calculate(input.Proposals, input.State));
        Assert.Equal("erp:synthetic-catalog:PRODUCT:001.01", group.Key);
        Assert.Equal(150, group.RequestedQuantity); Assert.Equal(400.246913578m, group.WinnerCost);
        Assert.Equal(1.987654321m, group.Winner!.Link!.Product!.LastPurchaseUnitPrice);
        var loadedProposal = Assert.Single(input.Proposals);
        Assert.Equal("Synthetic original description", loadedProposal.Extraction.Items[0].Description);
        Assert.Equal("Synthetic reviewed description", loadedProposal.Review[0].Description);
        Assert.Equal("2,000", loadedProposal.Extraction.Items[0].OriginalFields["quantity"]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await restarted.GetOriginalPdfAsync(proposal.Id, Token));
        Assert.Equal(proposal.SourceSha256, loadedProposal.SourceSha256);
        var purchaseInput = await restarted.GetPurchaseInputAsync(quotation.Id, Token);
        Assert.Equal(0, purchaseInput.Preparation!.QuotationInputsVersion);
        Assert.Equal(2, purchaseInput.Preparation.Version); Assert.Equal(150, purchaseInput.Preparation.Lines[0].RequestedQuantity);
        Assert.Equal(400.246913578m, purchaseInput.Preparation.ItemSubtotal);
        var savedComparison = await restarted.GetReconciliationAsync(quotation.Id, proposal.Id, Token);
        Assert.True(savedComparison!.ShortagesAccepted); Assert.Equal(200.123456789m, savedComparison.Orders[0].Lines[0].UnitPrice);
        Assert.Null(savedComparison.Orders[0].Lines[0].Brand); Assert.Null(savedComparison.Orders[0].Lines[0].SimilarityCode);
        var savedOperation = Assert.Single(await restarted.GetOrderOperationsAsync(quotation.Id, Token));
        Assert.Equal(OrderOperationState.Unknown, savedOperation.State); Assert.Equal(plan.OperationId, savedOperation.Plan.OperationId);
        Assert.Equal(199.123456789m, savedOperation.Plan.Changes[0].UnitPrice);
        Assert.Equal(100, (await restarted.FindMappingAsync(product.CatalogIdentity, mapping.Source, Token))!.ConversionFactor);
        Assert.Equal(template.Id, Assert.Single(await restarted.GetLatestTemplatesAsync(Token)).Id);
        Assert.Equal("synthetic-server", (await restarted.GetErpConfigurationAsync(Token)).Server);
    }

    private static string LegacyJson<T>(T value)
    {
        JsonNode node = JsonSerializer.SerializeToNode(value, SqliteImportStore.JsonOptions)!;
        RemoveNewFields(node);
        return node.ToJsonString();
    }

    private static void RemoveNewFields(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in new[] { "QuotationInputsVersion", "GroupingVersion", "Origin", "Sources", "Brand" }) obj.Remove(key);
            if (obj.ContainsKey("ReceivedQuantity")) obj.Remove("SimilarityCode");
            foreach (var child in obj.ToArray()) if (child.Value is not null) RemoveNewFields(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) RemoveNewFields(child);
    }

    private static async Task<Dictionary<string, string>> ReadExistingRowsAsync(ImportDbContext context)
    {
        Dictionary<string, string> result = [];
        await context.Database.OpenConnectionAsync(Token);
        foreach (string table in new[] { "Quotations", "TemplateVersions", "Proposals", "AnalysisRevisions", "PurchasePreparations", "ProductMappingRevisions", "OrderReconciliations", "OrderOperations", "Configuration" })
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT * FROM [{table}] ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync(Token);
            List<object[]> rows = [];
            while (await reader.ReadAsync(Token))
            {
                object[] values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values);
            }
            result[table] = JsonSerializer.Serialize(rows);
        }
        await context.Database.CloseConnectionAsync();
        return result;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

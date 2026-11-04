using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Erp;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed class PurchaseOrderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Order.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static PurchaseReferenceLine ReferenceLine(string id = "wire", string code = "000.01") => new(id, code, "Synthetic wire", "ROLL", "M", 10, 100, 200, 2000, 900);
    private static PurchaseOrderReference Reference(params PurchaseReferenceLine[] lines) => new(Guid.NewGuid(), 1, Guid.NewGuid(), "synthetic-catalog", "supplier-1", "Synthetic supplier", lines.Length == 0 ? [ReferenceLine()] : lines);
    private static PurchaseOrderLine Line(string id = "line-1", decimal quantity = 6, decimal price = 220) =>
        new(id, "000.01", "Synthetic wire", "ROLL", "M", quantity, 100, price, quantity * price, 0, false);
    private static PurchaseOrderSnapshot Order(string id = "order-1", params PurchaseOrderLine[] lines) =>
        new("synthetic-catalog", new(id, "1", "supplier-1", "Synthetic supplier", "branch-1", "open", new(2026, 10, 23), true), "revision-1", Now, lines.Length == 0 ? [Line()] : lines);
    private static OrderWritePlan Plan(PurchaseOrderReference reference, PurchaseOrderSnapshot? order = null, params OrderLineChange[] changes) =>
        new(Guid.NewGuid(), order is null ? OrderWriteKind.Create : OrderWriteKind.Update, reference, order, "branch-1", "open", new(2026, 10, 23), "synthetic-script-hash",
            changes.Length == 0 ? [new(null, reference.Lines[0].Id, 10, 200, false)] : changes);

    [Fact]
    public void Coverage_CombinesSplitOrdersAndPreservesIndividualPrices()
    {
        var result = PurchaseOrderRules.Compare(Reference(), [Order(), Order("order-2", Line("line-2", 4, 180))]);
        var line = Assert.Single(result.Lines);
        Assert.Equal(1000, line.QuotedQuantity); Assert.Equal(1000, line.CoveredQuantity); Assert.Equal(0, line.RemainingQuantity);
        Assert.Equal([600m, 400m], line.Contributions.Select(item => item.Quantity));
        Assert.Equal([10m, -10m], line.Contributions.Select(item => item.PriceDifferencePercent!.Value));
        Assert.Empty(result.Excess);
    }
    [Fact]
    public void Coverage_DoesNotDoubleCountRepeatedProductsOrMergeDifferentCodes()
    {
        var reference = Reference(ReferenceLine(), ReferenceLine("second"), ReferenceLine("punctuation", "00001"));
        var result = PurchaseOrderRules.Compare(reference, [Order("order-1", Line(), Line("line-2", 8))]);
        Assert.Equal([1000m, 400m, 0m], result.Lines.Select(row => row.CoveredQuantity));
        Assert.Equal([0m, 600m, 1000m], result.Lines.Select(row => row.RemainingQuantity));
        Assert.Equal(1400, result.Lines.SelectMany(row => row.Contributions).Sum(row => row.Quantity));
        Assert.Empty(result.Excess);
    }
    [Fact]
    public void Coverage_PreservesUnrelatedExcessUnitsAndCancellationWithoutSubstitution()
    {
        var result = PurchaseOrderRules.Compare(Reference(), [Order("order-1", Line() with { BaseUnit = "KG" },
            Line("cancelled") with { IsCancelled = true }, Line("other") with { ProductCode = "000.02" }, Line("valid", 12))]);
        Assert.Equal(1000, Assert.Single(result.Lines).CoveredQuantity);
        Assert.Equal([600m, 600m, 200m], result.Excess.Select(item => item.Quantity));
        Assert.DoesNotContain(result.Excess, item => item.Line.Id == "cancelled");
    }
    [Fact]
    public void Coverage_UsesExplicitPackageFactorsAndLeavesZeroReferencePercentageUnavailable()
    {
        var reference = Reference(ReferenceLine() with { UnitPrice = 0 });
        var result = PurchaseOrderRules.Compare(reference, [Order("order-1", Line() with { Quantity = 1000, BaseUnitsPerUnit = 1, UnitPrice = 2, Unit = "M" })]);
        var contribution = Assert.Single(Assert.Single(result.Lines).Contributions);
        Assert.Equal(2, contribution.BaseUnitPrice); Assert.Null(contribution.PriceDifferencePercent);
    }
    [Fact]
    public void ManualPairing_RequiresExplicitApprovalAndCannotAllocateTheSameLineTwice()
    {
        var reference = Reference(ReferenceLine(), ReferenceLine("other", "other-product"));
        var orders = new[] { Order() };
        Assert.Equal([600m, 0m], PurchaseOrderRules.Compare(reference, orders).Lines.Select(line => line.CoveredQuantity));
        OrderManualPair pair = new("order-1", "line-1", "other");
        var approved = PurchaseOrderRules.Compare(reference, orders, [pair]);
        Assert.Equal([0m, 600m], approved.Lines.Select(line => line.CoveredQuantity));
        Assert.Equal("000.01", orders[0].Lines[0].ProductCode);
        Assert.Throws<OrderException>(() => PurchaseOrderRules.Compare(reference, orders, [pair, pair]));
        Assert.Throws<OrderException>(() => PurchaseOrderRules.Compare(reference, orders, [pair with { ReferenceLineId = "missing" }]));
        Assert.Throws<OrderException>(() => PurchaseOrderRules.Compare(reference, [Order("order-1", Line() with { BaseUnit = "KG" })], [pair]));
    }
    [Fact]
    public void Coverage_RejectsDuplicateOrdersForeignCatalogsAndInvalidQuantities()
    {
        Assert.Equal(OrderError.DuplicateOrder, Assert.Throws<OrderException>(() => PurchaseOrderRules.Compare(Reference(), [Order(), Order()])).Code);
        Assert.Equal(OrderError.InvalidReference, Assert.Throws<OrderException>(() => PurchaseOrderRules.Compare(Reference(), [Order() with { CatalogIdentity = "other" }])).Code);
        Assert.Throws<OrderException>(() => PurchaseOrderRules.Compare(Reference(), [Order() with { Header = Order().Header with { SupplierId = "other" } }]));
        foreach (var line in new[] { Line() with { Quantity = -1 }, Line() with { BaseUnitsPerUnit = 0 }, Line() with { UnitPrice = -1 }, Line() with { ReceivedQuantity = -1 }, Line() with { Total = -1 } })
            Assert.Equal(OrderError.InvalidResult, Assert.Throws<OrderException>(() => PurchaseOrderRules.Validate(Order("order", line))).Code);
        Assert.Throws<OrderException>(() => PurchaseOrderRules.Validate(Order("order", Line(), Line())));
        Assert.Throws<OrderException>(() => PurchaseOrderRules.Compare(Reference(ReferenceLine() with { Quantity = 0 }), []));
        Assert.Throws<OrderException>(() => PurchaseOrderRules.Compare(Reference(ReferenceLine(), ReferenceLine()), []));
    }
    [Fact]
    public void EditPreview_PreservesUnselectedFieldsAndRejectsReceiptOrCancellationEdits()
    {
        var reference = Reference(); var order = Order();
        var priceOnly = Plan(reference, order, new OrderLineChange("line-1", null, null, 195, false));
        OrderChangeRules.Validate(priceOnly); Assert.Null(Assert.Single(priceOnly.Changes).Quantity);
        OrderChangeRules.Validate(Plan(reference, order, new OrderLineChange("line-1", null, 7, null, false)));
        OrderChangeRules.Validate(Plan(reference, order, new OrderLineChange("line-1", null, null, null, true)));
        foreach (var blocked in new[] { order with { Lines = [Line() with { ReceivedQuantity = 1 }] }, order with { Lines = [Line() with { IsCancelled = true }] }, order with { Header = order.Header with { CanEdit = false } } })
            Assert.Equal(OrderError.NotEditable, Assert.Throws<OrderException>(() => OrderChangeRules.Validate(priceOnly with { ExpectedOrder = blocked })).Code);
        Assert.Throws<OrderException>(() => OrderChangeRules.Validate(priceOnly with { Changes = [new("line-1", null, null, null, false)] }));
        Assert.Throws<OrderException>(() => OrderChangeRules.Validate(priceOnly with { Changes = [new("line-1", null, 6, null, true)] }));
        Assert.Throws<OrderException>(() => OrderChangeRules.Validate(priceOnly with { Changes = [priceOnly.Changes[0], priceOnly.Changes[0]] }));
        Assert.Throws<OrderException>(() => OrderChangeRules.Validate(priceOnly with { Changes = [new("line-1", null, 0, null, false)] }));
        Assert.Throws<OrderException>(() => OrderChangeRules.Validate(priceOnly with { Changes = [new("line-1", null, null, -1, false)] }));
    }
    [Fact]
    public void CreatePreview_RequiresExplicitIdentityBranchStatusAndChanges()
    {
        var plan = Plan(Reference()); OrderChangeRules.Validate(plan);
        foreach (var invalid in new[] { plan with { Changes = [] }, plan with { Branch = "" }, plan with { Status = "" }, plan with { ScriptIdentity = "" }, plan with { OperationId = Guid.Empty },
            plan with { Kind = OrderWriteKind.Update }, plan with { ExpectedOrder = Order() }, plan with { Changes = [new(null, "unknown", 1, 2, false)] },
            plan with { Changes = [new(null, "wire", 1, null, false)] }, plan with { Changes = [plan.Changes[0], plan.Changes[0]] } })
            Assert.Throws<OrderException>(() => OrderChangeRules.Validate(invalid));
    }
    [Fact]
    public void SnapshotFingerprint_IgnoresFetchTimeAndLineOrderingButDetectsBusinessChanges()
    {
        var order = Order("order-1", Line(), Line("line-2"));
        Assert.Equal(ErpPurchaseOrderWriter.Fingerprint(order), ErpPurchaseOrderWriter.Fingerprint(order with { FetchedAtUtc = Now.AddHours(1), Lines = order.Lines.Reverse().ToArray() }));
        Assert.NotEqual(ErpPurchaseOrderWriter.Fingerprint(order), ErpPurchaseOrderWriter.Fingerprint(order with { Lines = [Line() with { UnitPrice = 222 }, Line("line-2")] }));
        Assert.NotEqual(ErpPurchaseOrderWriter.Fingerprint(order), ErpPurchaseOrderWriter.Fingerprint(order with { Revision = "tax-or-header-change" }));
    }

    private async Task<(SqliteImportStore Store, PurchaseOrderReference Reference)> SeedAsync()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic order study", "", Now, Now, QuotationState.Draft, 0), Token);
        Guid proposalId = Guid.NewGuid(), captureId = Guid.NewGuid();
        SavedProposal proposal = new(proposalId, Now, "synthetic.pdf", "synthetic-source", new PdfTemplate { Id = Guid.NewGuid(), Version = 1, SupplierName = "Synthetic supplier" },
            new([], "Synthetic"), new([], [], null, null), [new(captureId, "SUP.001", "Synthetic wire", 10, 200, 2000, "ROLL", false, "")])
        { QuotationId = quotation.Id };
        await store.SaveProposalAsync(proposal, [1, 2, 3], Token);
        var input = await store.GetAnalysisInputAsync(quotation.Id, Token);
        ErpProductCandidate product = new("000.01", "Synthetic wire", "wire", false, "supplier-1", "Synthetic supplier", "SUP.001", 100, "ROLL", null, null, "M", "M", "synthetic-catalog", Now);
        string groupKey = ErpProductIdentity.GroupKey(product);
        var analysis = await store.SaveAnalysisAsync(input.State with { Links = [new(proposalId, captureId, groupKey, "Synthetic wire", "M", 100, product, Now)], Demands = [new(groupKey, "M", 900)] }, Token);
        var preparation = await store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, analysis.Version, 0, [new(proposalId, captureId)]), Token);
        return (store, PurchaseOrderReference.From(preparation, proposalId));
    }
    [Fact]
    public async Task SqliteComparison_RestartsOfflineWithFrozenHistoryDecimalsAndAcceptedShortage()
    {
        var (store, reference) = await SeedAsync();
        var order = Order("order", Line() with { UnitPrice = 2.1234567890m, Total = 12.7407m });
        var first = await store.SaveReconciliationAsync(new(Guid.Empty, 0, Now, reference, [order], false), Token);
        var second = await store.SaveReconciliationAsync(first with { ShortagesAccepted = true }, Token);
        var loaded = (await new SqliteImportStore(_directory).GetReconciliationAsync(reference.QuotationId, reference.ProposalId, Token))!;
        Assert.Equal(2, loaded.Version); Assert.True(loaded.ShortagesAccepted); Assert.Equal(900, loaded.Reference.Lines[0].RequestedQuantity);
        Assert.Equal(400, Assert.Single(PurchaseOrderRules.Compare(loaded.Reference, loaded.Orders).Lines).RemainingQuantity);
        Assert.Equal(2.1234567890m, loaded.Orders[0].Lines[0].UnitPrice); Assert.Equal(12.7407m, loaded.Orders[0].Lines[0].Total);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAtUtc.Kind); Assert.NotEqual(first.Id, second.Id);
        Assert.False(first.ShortagesAccepted);
        Assert.Equal(OrderError.Conflict, (await Assert.ThrowsAsync<OrderException>(() => store.SaveReconciliationAsync(first, Token))).Code);
        Assert.Equal(OrderError.InvalidReference, (await Assert.ThrowsAsync<OrderException>(() => store.SaveReconciliationAsync(second with { Reference = reference with { Lines = [reference.Lines[0] with { Quantity = 11 }] } }, Token))).Code);
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "imports.db") }.ToString());
        await connection.OpenAsync(Token); await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM OrderReconciliations";
        Assert.Equal(2L, await command.ExecuteScalarAsync(Token));
    }
    [Fact]
    public async Task ConfirmedWrites_AreJournaledBeforeDispatchAndNeverBlindlyRetried()
    {
        var (store, reference) = await SeedAsync();
        Writer writer = new(); PurchaseOrderService service = new(store, writer);
        var plan = await service.PreviewAsync(OrderWriteKind.Create, reference, null, "branch-1", "open", new(2026, 10, 23), [new(null, reference.Lines[0].Id, 10, 200, false)], Token);
        Assert.Empty(await store.GetOrderOperationsAsync(reference.QuotationId, Token)); Assert.Equal(0, writer.Writes);
        Assert.Equal(OrderError.ConfirmationRequired, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(plan, false, false, Token))).Code);
        writer.OnWrite = async current =>
        {
            var pending = Assert.Single(await new SqliteImportStore(_directory).GetOrderOperationsAsync(reference.QuotationId, Token));
            Assert.Equal(OrderOperationState.Pending, pending.State); Assert.Equal(current.OperationId, pending.Plan.OperationId);
            throw new OperationCanceledException();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConfirmAsync(plan, true, false, Token));
        var unknown = Assert.Single(await store.GetOrderOperationsAsync(reference.QuotationId, Token));
        Assert.Equal(OrderOperationState.Unknown, unknown.State);
        Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(plan with { OperationId = Guid.NewGuid() }, true, false, Token))).Code);
        Assert.Equal(1, writer.Writes);
        writer.Receipt = new(plan.OperationId, OrderOperationState.Committed, "order-committed");
        await service.ReconcileAsync(unknown, Token);
        Assert.Equal(1, writer.Writes); Assert.Equal(OrderOperationState.Committed, Assert.Single(await store.GetOrderOperationsAsync(reference.QuotationId, Token)).State);
        Assert.Equal(OrderError.PendingOperation, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(plan, true, false, Token))).Code);
    }
    [Fact]
    public async Task SuccessfulWrite_RecordsReceiptAndRejectsInvalidReconciliationOrCancellationConfirmation()
    {
        var (store, reference) = await SeedAsync(); Writer writer = new(); PurchaseOrderService service = new(store, writer);
        var plan = Plan(reference, Order(), new OrderLineChange("line-1", null, null, null, true));
        Assert.Equal(OrderError.ConfirmationRequired, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(plan, true, false, Token))).Code);
        var receipt = await service.ConfirmAsync(plan, true, true, Token);
        Assert.Equal(OrderOperationState.Committed, receipt.State);
        var committed = Assert.Single(await store.GetOrderOperationsAsync(reference.QuotationId, Token));
        await Assert.ThrowsAsync<OrderException>(() => service.ReconcileAsync(committed, Token));
        var next = Plan(reference); await store.BeginOrderOperationAsync(next, Token);
        var pending = (await store.GetOrderOperationsAsync(reference.QuotationId, Token)).Last();
        writer.Receipt = new(Guid.NewGuid(), OrderOperationState.Committed, "order");
        await Assert.ThrowsAsync<OrderException>(() => service.ReconcileAsync(pending, Token));
        writer.Receipt = new(next.OperationId, OrderOperationState.NotApplied, null);
        await service.ReconcileAsync(pending, Token);
        Assert.Equal(OrderOperationState.NotApplied, (await store.GetOrderOperationsAsync(reference.QuotationId, Token)).Last().State);
    }
    [Fact]
    public async Task InvalidWriteReceipt_PreservesUnknownOutcomeAndBlocksAnotherSubmission()
    {
        var (store, reference) = await SeedAsync(); Writer writer = new() { Receipt = new(Guid.NewGuid(), OrderOperationState.Committed, "wrong-operation") };
        var service = new PurchaseOrderService(store, writer);
        var receipt = await service.ConfirmAsync(Plan(reference), true, false, Token);
        Assert.Equal(OrderOperationState.Unknown, receipt.State); Assert.Null(receipt.OrderId);
        await Assert.ThrowsAsync<OrderException>(() => store.BeginOrderOperationAsync(Plan(reference), Token));
        await Assert.ThrowsAsync<OrderException>(() => store.CompleteOrderOperationAsync(receipt with { State = OrderOperationState.Pending }, Token));
    }
    [Fact]
    public async Task StaleReference_RejectsDispatchWhilePreservingHistoricalComparison()
    {
        var (store, reference) = await SeedAsync();
        var saved = await store.SaveReconciliationAsync(new(Guid.Empty, 0, Now, reference, [Order()], false), Token);
        var input = await store.GetAnalysisInputAsync(reference.QuotationId, Token);
        await store.SaveAnalysisAsync(input.State with { Demands = [input.State.Demands[0] with { Quantity = 950 }] }, Token);
        Writer writer = new(); var service = new PurchaseOrderService(store, writer);
        Assert.Equal(OrderError.Conflict, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(Plan(reference), true, false, Token))).Code);
        Assert.Equal(0, writer.Writes); Assert.Empty(await store.GetOrderOperationsAsync(reference.QuotationId, Token));
        var loaded = (await store.GetReconciliationAsync(reference.QuotationId, reference.ProposalId, Token))!;
        Assert.Equal(saved.Id, loaded.Id); Assert.Equal(900, loaded.Reference.Lines[0].RequestedQuantity);
        input = await store.GetAnalysisInputAsync(reference.QuotationId, Token);
        await store.PreparePurchaseAsync(new(reference.QuotationId, input.Quotation.Revision, input.State.Version, 1,
            [new(reference.ProposalId, input.Proposals[0].Review[0].CaptureId)]), Token);
        Assert.Equal(OrderError.Conflict, (await Assert.ThrowsAsync<OrderException>(() => service.ConfirmAsync(Plan(reference), true, false, Token))).Code);
        Assert.Equal(0, writer.Writes);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrderHistory_PreventsQuotationDeletionAndPreservesRecovery(bool comparison)
    {
        var (store, reference) = await SeedAsync();
        if (comparison) await store.SaveReconciliationAsync(new(Guid.Empty, 0, Now, reference, [], false), Token);
        else await store.BeginOrderOperationAsync(Plan(reference), Token);
        var quotation = (await store.GetQuotationsAsync(Token)).Single().Quotation;
        Assert.Equal(QuotationError.OrderHistory, (await Assert.ThrowsAsync<QuotationException>(() => store.DeleteQuotationAsync(quotation.Id, quotation.Revision, Token))).Code);
        Assert.Single(await store.GetQuotationsAsync(Token));
        if (comparison) Assert.NotNull(await store.GetReconciliationAsync(reference.QuotationId, reference.ProposalId, Token));
        else Assert.Equal(OrderOperationState.Pending, Assert.Single(await store.GetOrderOperationsAsync(reference.QuotationId, Token)).State);
    }
    [Fact]
    public async Task Reconciliation_RejectsFutureStatesAndWrongUpdateTargets()
    {
        var (store, reference) = await SeedAsync();
        var plan = Plan(reference, Order(), new OrderLineChange("line-1", null, null, 190, false));
        await store.BeginOrderOperationAsync(plan, Token);
        var operation = Assert.Single(await store.GetOrderOperationsAsync(reference.QuotationId, Token));
        Writer writer = new() { Receipt = new(plan.OperationId, (OrderOperationState)99, null) };
        var service = new PurchaseOrderService(store, writer);
        await Assert.ThrowsAsync<OrderException>(() => service.ReconcileAsync(operation, Token));
        writer.Receipt = new(plan.OperationId, OrderOperationState.Committed, "other-order");
        await Assert.ThrowsAsync<OrderException>(() => service.ReconcileAsync(operation, Token));
        await Assert.ThrowsAsync<OrderException>(() => store.CompleteOrderOperationAsync(new(plan.OperationId, (OrderOperationState)99, null), Token));
        Assert.Equal(OrderOperationState.Pending, Assert.Single(await store.GetOrderOperationsAsync(reference.QuotationId, Token)).State);
    }
    [Fact]
    public async Task MigrationFromPurchaseSchema_PreservesPurchaseEvidenceAndCreatesOrderStorage()
    {
        Directory.CreateDirectory(_directory);
        await using (ImportDbContext context = new(new DbContextOptionsBuilder<ImportDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "imports.db") }.ToString()).Options))
            await context.GetService<IMigrator>().MigrateAsync("PurchasePreparations", Token);
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "backups"), "*.db"));
        Assert.Empty(await store.GetOrderOperationsAsync(Guid.NewGuid(), Token));
        Assert.Null(await store.GetReconciliationAsync(Guid.NewGuid(), Guid.NewGuid(), Token));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"Id\":\"order\",\"Number\":\"001\",\"SupplierId\":\"supplier\",\"SupplierName\":\"Synthetic supplier\",\"Branch\":\"1\",\"Status\":\"open\",\"Date\":\"2026-10-23\",\"CanEdit\":true}]")]
    public async Task ScriptReader_ConsumesOneTypedJsonResultFromARealProvider(string json)
    {
        await using SqliteConnection connection = new("Data Source=:memory:"); await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT @json AS ResultJson"; command.Parameters.AddWithValue("@json", json);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var result = await PurchaseOrderScripts.ReadResultAsync<PurchaseOrderHeader[]>(reader, Token);
        Assert.Equal(json == "[]" ? 0 : 1, result.Length);
        if (result.Length > 0) { Assert.Equal("001", result[0].Number); Assert.Equal(new DateOnly(2026, 10, 23), result[0].Date); }
    }
    [Theory]
    [InlineData("SELECT '[]' AS WrongColumn")]
    [InlineData("SELECT NULL AS ResultJson")]
    [InlineData("SELECT '{}' AS ResultJson")]
    [InlineData("SELECT '[{}]' AS ResultJson")]
    [InlineData("SELECT '[]' AS ResultJson UNION ALL SELECT '[]'")]
    [InlineData("SELECT '[]' AS ResultJson; SELECT '[]' AS ResultJson")]
    [InlineData("SELECT 'broken-json' AS ResultJson")]
    [InlineData("SELECT '[]' AS ResultJson WHERE 0")]
    public async Task ScriptReader_RejectsMissingAmbiguousOrMalformedContracts(string sql)
    {
        await using SqliteConnection connection = new("Data Source=:memory:"); await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.Equal(ErpError.InvalidResult, (await Assert.ThrowsAsync<ErpException>(() => PurchaseOrderScripts.ReadResultAsync<PurchaseOrderHeader[]>(reader, Token))).Code);
    }
    [Fact]
    public void Scripts_PreserveProceduresLoopsCursorsAndInvalidateChangedPreviewProfiles()
    {
        const string script = "SET NOCOUNT ON; DECLARE @i int = 0; WHILE @i < 2 BEGIN EXEC dbo.SyntheticProcedure @RequestJson; SET @i += 1; END; DECLARE c CURSOR LOCAL FOR SELECT 1;";
        var config = new ErpConfiguration
        {
            Server = "synthetic",
            Database = "synthetic",
            UserName = "reader",
            WriteUserName = "writer",
            Queries = new()
            {
                [ErpOperation.CreatePurchaseOrder] = script,
                [ErpOperation.ReadPurchaseOrder] = "EXEC dbo.SyntheticRead @RequestJson",
                [ErpOperation.ReconcilePurchaseOperation] = "EXEC dbo.SyntheticReceipt @RequestJson"
            }
        };
        Assert.Equal(script, PurchaseOrderScripts.Get(config, ErpOperation.CreatePurchaseOrder));
        var hash = PurchaseOrderScripts.Identity(config, OrderWriteKind.Create);
        Assert.NotEqual(hash, PurchaseOrderScripts.Identity(config with { WriteUserName = "other-writer" }, OrderWriteKind.Create));
        config.Queries[ErpOperation.ReadPurchaseOrder] += ";";
        Assert.NotEqual(hash, PurchaseOrderScripts.Identity(config, OrderWriteKind.Create));
        Assert.Equal(ErpAuthentication.SqlLogin, PurchaseOrderScripts.WriteConfiguration(config).Authentication);
        Assert.Equal("writer", PurchaseOrderScripts.WriteConfiguration(config).UserName);
        Assert.Throws<OrderException>(() => PurchaseOrderScripts.Identity(config with { WriteUserName = "" }, OrderWriteKind.Create));
        Assert.Throws<ErpException>(() => PurchaseOrderScripts.Get(new(), ErpOperation.ReadPurchaseOrder));
        Assert.Throws<ErpException>(() => PurchaseOrderScripts.Get(new() { Queries = new() { [ErpOperation.ReadPurchaseOrder] = new('x', 1_000_001) } }, ErpOperation.ReadPurchaseOrder));
    }

    [Theory]
    [InlineData(ErpAuthentication.SqlLogin)]
    [InlineData(ErpAuthentication.Integrated)]
    public void WriteProfile_CanExplicitlyReuseTheCurrentConnectionAndPinsTheAuditOperator(ErpAuthentication authentication)
    {
        ErpConfiguration config = new()
        {
            Server = "synthetic",
            Database = "synthetic",
            Authentication = authentication,
            UserName = "current-user",
            UseReadConnectionForWrites = true,
            WriteOperatorCode = 112,
            Queries = new()
            {
                [ErpOperation.CreatePurchaseOrder] = "EXEC dbo.SyntheticCreate @RequestJson, @OperatorCode",
                [ErpOperation.ReadPurchaseOrder] = "EXEC dbo.SyntheticRead @RequestJson",
                [ErpOperation.ReconcilePurchaseOperation] = "EXEC dbo.SyntheticReceipt @RequestJson"
            }
        };
        var write = PurchaseOrderScripts.WriteConfiguration(config);
        Assert.Equal(authentication, write.Authentication);
        Assert.Equal("current-user", write.UserName);
        Assert.Equal(ProtectedErpCredentials.Identity(config), ProtectedErpCredentials.Identity(write));
        var identity = PurchaseOrderScripts.Identity(config, OrderWriteKind.Create);
        Assert.NotEqual(identity, PurchaseOrderScripts.Identity(config with { WriteOperatorCode = 113 }, OrderWriteKind.Create));
        Assert.NotEqual(identity, PurchaseOrderScripts.Identity(config with { UseReadConnectionForWrites = false, WriteUserName = "dedicated-writer" }, OrderWriteKind.Create));
        Assert.Throws<OrderException>(() => PurchaseOrderScripts.Identity(config with { UseReadConnectionForWrites = false }, OrderWriteKind.Create));
        Assert.Throws<OrderException>(() => PurchaseOrderScripts.Identity(config with { WriteOperatorCode = 0 }, OrderWriteKind.Create));
    }

    private sealed class Writer : IPurchaseOrderWriter
    {
        public int Writes { get; private set; }
        public OrderWriteReceipt? Receipt { get; set; }
        public Func<OrderWritePlan, Task<OrderWriteReceipt>>? OnWrite { get; set; }
        public Task<string> GetScriptIdentityAsync(OrderWriteKind kind, CancellationToken token) => Task.FromResult("synthetic-script-hash");
        public async Task<OrderWriteReceipt> ExecuteAsync(OrderWritePlan plan, CancellationToken token)
        { Writes++; return OnWrite is not null ? await OnWrite(plan) : Receipt ?? new(plan.OperationId, OrderOperationState.Committed, plan.ExpectedOrder?.Header.Id ?? "order-created"); }
        public Task<OrderWriteReceipt> ReconcileAsync(OrderOperation operation, CancellationToken token) => Task.FromResult(Receipt ?? new(operation.Plan.OperationId, OrderOperationState.Unknown, null));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

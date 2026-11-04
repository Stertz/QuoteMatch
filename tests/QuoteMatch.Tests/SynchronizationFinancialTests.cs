using System.Text.Json;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Infrastructure.Erp;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed partial class PurchaseSynchronizationTests
{
    [Fact]
    public async Task FinancialPreparation_DoesNotTransmitRawDocumentEvidenceInAnErpRequest()
    {
        const string sourceText = "SYNTHETIC DOCUMENT FOOTER EVIDENCE FOR LOCAL REVIEW ONLY";
        await using var fixture = await ExecutionFixture.CreateAsync(documentFinancials: new(new() { Freight = 17.123456789m },
            [new(FinancialField.Freight, "17,123456789", 17.123456789m, 2, sourceText)]));
        string request = JsonSerializer.Serialize(fixture.Plan, SqliteImportStore.JsonOptions);
        Assert.DoesNotContain(sourceText, request, StringComparison.Ordinal);
        var proposal = fixture.Plan.Selection.Preview.Proposal;
        var source = Assert.Single(proposal.FinancialSources);
        Assert.Equal(17.123456789m, source.Values.Freight); Assert.Equal(1, source.SourceItemCount);
        var input = await fixture.Store.GetAnalysisInputAsync(fixture.Plan.Selection.Preview.Proposal.QuotationId, fixture.Token);
        Assert.Equal(sourceText, Assert.Single(input.Proposals[0].Extraction.DocumentFinancials.Evidence).SourceText);
        Assert.Equal(input.Proposals[0].Id, source.ProposalId); Assert.Equal(input.Proposals[0].SourceSha256, source.SourceSha256);
        PurchaseSynchronizationService previews = new(fixture.Store, fixture.Erp, fixture.Erp);
        var reloaded = JsonSerializer.Deserialize<BrandPurchaseProposal>(JsonSerializer.Serialize(proposal, SqliteImportStore.JsonOptions), SqliteImportStore.JsonOptions)!;
        await previews.PreviewAsync(reloaded, "branch-1", "order-1", [], fixture.Token);
        Assert.Equal(SynchronizationError.Conflict, (await Assert.ThrowsAsync<SynchronizationException>(() => previews.PreviewAsync(reloaded with
        { FinancialSources = [source with { Values = new() { Freight = 99 } }] }, "branch-1", "order-1", [], fixture.Token))).Code);
    }

    [Fact]
    public async Task FinancialEnabledProfile_PreviewsItsDefaultQueryPolicyAndCreationKeepsExplicitZeroDistinctFromMissing()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var selection = await FinancialSelectionAsync(fixture);
        selection = PurchaseSynchronization.Select(selection.Preview, ["price:line-1"], false);
        fixture.Writer.FinancialPreview = _ => new(SynchronizationFinancialStatus.Ready, "default-policy-revision", true, new() { Freight = 7 }, 407.246913578m,
            [new(0, "line-1", "000.01", "RL", "M", 2, 100, 200.123456789m, 400.246913578m, new())], []);
        var plan = await fixture.Service.PreviewAsync(selection, fixture.Token);
        Assert.False(plan.FinancialPolicy.IsExplicit); Assert.True(plan.RequiresFinancialPreview);
        Assert.Equal(407.246913578m, plan.FinancialPreview!.OrderTotal); Assert.Equal(1, fixture.Writer.FinancialReads);
        await using var creation = await ExecutionFixture.CreateAsync(create: true);
        creation.Writer.FinancialPreview = draft => FinancialResult(draft) with
        {
            Header = new() { Freight = 0 },
            OrderTotal = 0,
            Lines = FinancialResult(draft).Lines.Select(line => line with { UnitPrice = 0, ItemTotal = 0 }).ToArray()
        };
        Assert.Equal(OrderError.FinancialPolicyUnsupported, (await Assert.ThrowsAsync<OrderException>(() => creation.Service.PreviewAsync(creation.Plan.Selection, creation.Token, "open", new(2026, 10, 27),
            new() { Freight = SynchronizationFinancialSource.KeepExisting }))).Code);
        var zero = await creation.Service.PreviewAsync(creation.Plan.Selection, creation.Token, "open", new(2026, 10, 27),
            new() { Prices = SynchronizationPriceSource.Zero, Freight = SynchronizationFinancialSource.Zero });
        Assert.Equal(0, zero.FinancialPreview!.Lines[0].UnitPrice); Assert.Equal(0, zero.FinancialPreview.OrderTotal);
        Assert.Equal(200.123456789m, zero.Selection.Preview.Proposal.Lines[0].Offer.Item.UnitPrice);
    }

    private static async Task<SynchronizationSelection> FinancialSelectionAsync(ExecutionFixture fixture)
    {
        fixture.Erp.Order = fixture.Erp.Order with
        {
            Financials = new() { Freight = 4 },
            OrderTotal = 424,
            Lines = fixture.Erp.Order.Lines.Select(line => line with { Financials = new() }).ToArray()
        };
        PurchaseSynchronizationService previews = new(fixture.Store, fixture.Erp, fixture.Erp);
        var preview = await previews.PreviewAsync(fixture.Plan.Selection.Preview.Proposal, "branch-1", "order-1", [], fixture.Token);
        return PurchaseSynchronization.Select(preview, [], false, true);
    }
    private static SynchronizationFinancialPreview FinancialResult(SynchronizationWritePlan plan) => new(SynchronizationFinancialStatus.Ready, "synthetic-financial-revision", true,
        new() { Freight = 0 }, 420, plan.Selection.Lines.Select((line, index) => new SynchronizationFinancialLine(index, line.Control, line.ProductCode,
            line.Unit, line.BaseUnit, line.Quantity, line.Factor, 210, 420, new())).ToArray(), []);

    [Fact]
    public async Task FinancialOnlyOperation_PreviewsAndJournalsTheApprovedQueryValuesWithoutRewritingThePdfPrice()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var selection = await FinancialSelectionAsync(fixture);
        fixture.Writer.FinancialPreview = FinancialResult;
        SynchronizationFinancialPolicy policy = new() { Prices = SynchronizationPriceSource.KeepExisting, Freight = SynchronizationFinancialSource.Zero };
        var plan = await fixture.Service.PreviewAsync(selection, fixture.Token, financialPolicy: policy);
        Assert.Empty(selection.SelectedChanges); Assert.Equal(210, Assert.Single(plan.FinancialPreview!.Lines).UnitPrice);
        Assert.Equal(200.123456789m, plan.Selection.Preview.Proposal.Lines[0].Offer.Item.UnitPrice);
        Assert.Equal(0, plan.FinancialPreview.Header.Freight); Assert.Equal(420, plan.FinancialPreview.OrderTotal);
        fixture.Writer.OnWrite = async approved =>
        {
            var pending = Assert.Single(await new SqliteImportStore(fixture.Directory).GetSynchronizationOperationsAsync(selection.Preview.Proposal.QuotationId, fixture.Token));
            Assert.Equal(OrderOperationState.Pending, pending.State); Assert.Equal(policy, pending.Plan.FinancialPolicy);
            Assert.Equal(ErpPurchaseSynchronizationWriter.FinancialFingerprint(approved.FinancialPreview!), ErpPurchaseSynchronizationWriter.FinancialFingerprint(pending.Plan.FinancialPreview!));
            return new(approved.OperationId, OrderOperationState.Committed, "order-1") { FinancialRevision = approved.FinancialPreview!.Revision };
        };
        var receipt = await fixture.Service.ConfirmAsync(plan, true, false, fixture.Token);
        Assert.Equal(OrderOperationState.Committed, receipt.State); Assert.Equal(1, fixture.Writer.FinancialReads); Assert.Equal(1, fixture.Writer.Writes);
        var saved = (await fixture.Store.GetPurchaseInputAsync(selection.Preview.Proposal.QuotationId, fixture.Token)).Preparation!;
        Assert.Equal(200.123456789m, saved.Lines[0].Offer.Item.UnitPrice);
    }

    [Theory]
    [InlineData(SynchronizationFinancialStatus.MissingDocumentValues, OrderError.FinancialDataMissing)]
    [InlineData(SynchronizationFinancialStatus.MissingHistory, OrderError.FinancialHistoryMissing)]
    [InlineData(SynchronizationFinancialStatus.UnsupportedPolicy, OrderError.FinancialPolicyUnsupported)]
    public async Task FinancialQuery_ReportsUnresolvedPolicyBeforeAnyJournalOrDispatch(SynchronizationFinancialStatus status, OrderError expected)
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var selection = await FinancialSelectionAsync(fixture);
        fixture.Writer.FinancialPreview = plan => FinancialResult(plan) with { Status = status };
        var error = await Assert.ThrowsAsync<OrderException>(() => fixture.Service.PreviewAsync(selection, fixture.Token, financialPolicy: new() { Freight = SynchronizationFinancialSource.Zero }));
        Assert.Equal(expected, error.Code); Assert.Equal(0, fixture.Writer.Writes);
        Assert.Empty(await fixture.Store.GetSynchronizationOperationsAsync(selection.Preview.Proposal.QuotationId, fixture.Token));
    }

    [Fact]
    public async Task LegacyProfilesAndMissingDocumentValues_BlockExplicitPoliciesWithoutSilentlyFallingBack()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var selection = await FinancialSelectionAsync(fixture);
        Assert.Equal(OrderError.FinancialPolicyUnsupported, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.PreviewAsync(selection, fixture.Token,
            financialPolicy: new() { Prices = SynchronizationPriceSource.KeepExisting }))).Code);
        fixture.Writer.FinancialPreview = FinancialResult;
        Assert.Equal(OrderError.FinancialDataMissing, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.PreviewAsync(selection, fixture.Token,
            financialPolicy: new() { Taxes = SynchronizationFinancialSource.Document }))).Code);
        var include = selection with { Lines = [selection.Lines[0] with { Control = null }] };
        Assert.Equal(OrderError.FinancialDataMissing, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.PreviewAsync(include, fixture.Token,
            financialPolicy: new() { Prices = SynchronizationPriceSource.KeepExisting }))).Code);
        Assert.Equal(0, fixture.Writer.Writes); Assert.Equal(0, fixture.Writer.FinancialReads);
    }

    [Fact]
    public async Task FinancialPreview_RejectsChangedIdentityQuantityZeroPolicyViolationsAndMissingHistoryEvidence()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var selection = await FinancialSelectionAsync(fixture);
        fixture.Writer.FinancialPreview = FinancialResult;
        var plan = await fixture.Service.PreviewAsync(selection, fixture.Token, financialPolicy: new() { Prices = SynchronizationPriceSource.KeepExisting, Freight = SynchronizationFinancialSource.Zero });
        var financial = plan.FinancialPreview!; var line = financial.Lines[0];
        foreach (var invalid in new[]
        {
            financial with { Lines = [line with { Quantity = 999 }] }, financial with { Lines = [line with { Control = "foreign-control" }] },
            financial with { Lines = [line with { UnitPrice = 999 }] }, financial with { Header = new() { Freight = 3 } },
            financial with { Lines = [line with { Values = new() { IpiRate = 101 } }] }, financial with { Revision = "" },
            financial with { Lines = [] }, financial with { OrderTotal = null }, financial with { Sources = [new("other-catalog", "history", "revision", new(2026, 10, 20))] }
        }) Assert.Equal(OrderError.InvalidResult, Assert.Throws<OrderException>(() => SynchronizationFinancialRules.ValidatePreview(plan, invalid)).Code);
        Assert.Equal(OrderError.FinancialHistoryMissing, Assert.Throws<OrderException>(() => SynchronizationFinancialRules.ValidatePreview(plan with
        { FinancialPolicy = new() { Prices = SynchronizationPriceSource.LastPurchaseOrder } }, financial)).Code);
        Assert.Equal(OrderError.NoChanges, Assert.Throws<OrderException>(() => SynchronizationFinancialRules.ValidatePreview(plan, financial with { HasChanges = false })).Code);
        Assert.Equal(OrderError.FinancialDataMissing, Assert.Throws<OrderException>(() => SynchronizationWriteRules.Validate(plan with { FinancialPreview = null }, true)).Code);
    }

    [Fact]
    public async Task FinancialHistoryAndWrittenValues_AreComparedIncludingExactPricesTotalsAndHeaderCharges()
    {
        await using var fixture = await ExecutionFixture.CreateAsync();
        var selection = await FinancialSelectionAsync(fixture);
        fixture.Writer.FinancialPreview = plan => FinancialResult(plan) with { Sources = [new("catalog", "historical-order", "historical-financial-revision", new(2026, 10, 26)), new("catalog", "freight-reference", "freight-revision", new(2026, 10, 25))] };
        var plan = await fixture.Service.PreviewAsync(selection, fixture.Token, financialPolicy: new() { Prices = SynchronizationPriceSource.LastPurchaseOrder });
        var expected = plan.FinancialPreview!;
        var actual = fixture.Erp.Order with { Financials = new() { Freight = 0 }, OrderTotal = 420 };
        SynchronizationFinancialRules.ValidateWrittenOrder(plan, actual);
        foreach (var invalid in new[] { actual with { Financials = new() { Freight = 5 } }, actual with { OrderTotal = 421 },
            actual with { Lines = [actual.Lines[0] with { UnitPrice = 200 }] }, actual with { Lines = [] },
            actual with { Header = actual.Header with { SupplierId = "wrong-supplier" } } })
            Assert.Equal(OrderError.InvalidResult, Assert.Throws<OrderException>(() => SynchronizationFinancialRules.ValidateWrittenOrder(plan, invalid)).Code);
        var changed = expected with { Sources = expected.Sources.Select((source, index) => index == 0 ? source with { Revision = "changed-reference" } : source).ToArray() };
        Assert.NotEqual(ErpPurchaseSynchronizationWriter.FinancialFingerprint(expected), ErpPurchaseSynchronizationWriter.FinancialFingerprint(changed));
        Assert.Equal(ErpPurchaseSynchronizationWriter.FinancialFingerprint(expected), ErpPurchaseSynchronizationWriter.FinancialFingerprint(expected with { Sources = expected.Sources.Reverse().ToArray() }));
        fixture.Writer.Receipt = new(plan.OperationId, OrderOperationState.Committed, "order-1");
        Assert.Equal(OrderOperationState.Unknown, (await fixture.Service.ConfirmAsync(plan, true, false, fixture.Token)).State);
        var operation = Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(selection.Preview.Proposal.QuotationId, fixture.Token));
        Assert.Equal(OrderError.InvalidResult, (await Assert.ThrowsAsync<OrderException>(() => fixture.Service.ReconcileAsync(operation, fixture.Token))).Code);
        fixture.Writer.Receipt = fixture.Writer.Receipt with { FinancialRevision = expected.Revision };
        Assert.Equal(OrderOperationState.Committed, (await fixture.Service.ReconcileAsync(operation, fixture.Token)).State);
    }

    [Fact]
    public void PreparedDocumentFinancialSources_PreserveValuesAndProvenanceAcrossIndependentDeserialization()
    {
        PurchaseDocumentFinancialSource source = new(Guid.NewGuid(), "synthetic-hash", Guid.NewGuid(), 2, 4, new() { Freight = 17.123456789m });
        var reloaded = JsonSerializer.Deserialize<PurchaseDocumentFinancialSource>(JsonSerializer.Serialize(source, SqliteImportStore.JsonOptions), SqliteImportStore.JsonOptions)!;
        Assert.Equal(source, reloaded);
        Assert.NotEqual(source, reloaded with { Values = new() { Freight = 99 } });
        Assert.Equal(4, reloaded.SourceItemCount); Assert.Equal(17.123456789m, reloaded.Values.Freight);
    }
}

using Microsoft.Data.Sqlite;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed class OrderInputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.OrderInput.Tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static AnalysisInput Input(string similarity = "wire-family")
    {
        Quotation quotation = new(Guid.NewGuid(), "Synthetic comparison", "", Now, Now, QuotationState.Draft, 1);
        SavedProposal[] proposals = [Proposal("brand-A"), Proposal("brand-B")];
        var links = proposals.Select((proposal, index) =>
        {
            ErpProductCandidate product = new("000.0" + (index + 1), "Synthetic wire", similarity, false, proposal.Template.SupplierName, "Synthetic manufacturer", proposal.Review[0].ProductCode,
                100, "RL", new(2026, 10, 20), 9, "M", "M", "catalog", Now);
            return new ItemLink(proposal.Id, proposal.Review[0].CaptureId, ErpProductIdentity.GroupKey(product), product.Name, "M", 100, product, Now);
        }).ToArray();
        PurchaseOrderLine first = new("line-1", "SIL-01", "Synthetic wire", "RL", "M", 6, 100, 200, 1200, 2, false) { SimilarityCode = similarity };
        PurchaseOrderSnapshot order = new("catalog", new("order-1", "42", "distributor", "Synthetic distributor", "branch-1", "open", new(2026, 10, 26), true), "revision-1", Now,
            [first, first with { Id = "line-2", Quantity = 4, UnitPrice = 300, Total = 1200, ReceivedQuantity = 0 }]);
        return new(quotation, proposals, new(quotation.Id, 0, 1, Now, links, []))
        { Inputs = new(quotation.Id, 1, Now, QuotationPriceReference.LinkedOrder, ProductGroupingVersion.ProductNameFallback, [order]) };
    }
    private static SavedProposal Proposal(string supplier) => new(Guid.NewGuid(), Now, "synthetic.pdf", "synthetic-hash", new PdfTemplate { SupplierName = supplier }, new([], "Synthetic"),
        new([], [], null, null), [new(Guid.NewGuid(), "000-01", "Synthetic original PDF description", 10, 250, 2500, "RL", false, "")]);

    [Theory]
    [InlineData("wire-family")]
    [InlineData("")]
    public void Demand_Shares600Plus400AcrossBrandsWithoutChangingSourceLotsOrSelectingAProduct(string similarity)
    {
        var input = Input(similarity);
        var state = OrderInputRules.Apply(input, false);
        var group = Assert.Single(ImportedQuotationAnalysis.Calculate(input.Proposals, state));
        Assert.Equal(1000, group.RequestedQuantity); Assert.Equal(2, group.Offers.Count); Assert.Null(group.Winner);
        Assert.Equal(DemandOrigin.LinkedOrder, Assert.Single(state.Demands).Origin);
        Assert.Equal(2, state.Demands[0].Sources.Count); Assert.All(group.Offers, offer => Assert.Equal(10, offer.Item.Quantity));
        Assert.Equal(100, state.Links[0].BaseUnitsPerQuotedUnit);
        Assert.Equal(1000, Assert.Single(OrderInputRules.Apply(input with { State = state }, false).Demands).Quantity);
        Assert.Equal(240m / 100m, OrderInputRules.Price(group, input.Inputs)!.UnitPrice);
        Assert.Equal(9, state.Links[0].Product!.LastPurchaseUnitPrice);
    }

    [Fact]
    public void NullSimilarity_UsesTheExactErpNameForDemandAndPriceAcrossBrands()
    {
        var input = Input("");
        var order = input.Inputs.Orders[0];
        input = input with { Inputs = input.Inputs with { Orders = [order with { Lines = order.Lines.Select(line => line with { SimilarityCode = null }).ToArray() }] } };
        var state = OrderInputRules.Apply(input, false);
        var demand = Assert.Single(state.Demands);
        Assert.Equal(1000, demand.Quantity); Assert.Equal(DemandOrigin.LinkedOrder, demand.Origin);
        var group = Assert.Single(ImportedQuotationAnalysis.Calculate(input.Proposals, state));
        Assert.Equal(2.4m, OrderInputRules.Price(group, input.Inputs)!.UnitPrice);
        Assert.Equal(2, demand.Sources.Count);
        Assert.DoesNotContain(group.Offers, offer => offer.Link!.Product!.ProductCode == order.Lines[0].ProductCode);
        foreach (var changed in new[] { order with { CatalogIdentity = "different-catalog" }, order with { Lines = [order.Lines[0] with { SimilarityCode = null, Description = "Different ERP product" }] },
            order with { Lines = [order.Lines[0] with { SimilarityCode = null, BaseUnit = "KG" }] } })
            Assert.Empty(OrderInputRules.MatchLines(group, input.Inputs with { Orders = [changed] }));
    }

    [Fact]
    public void NullSimilarity_DoesNotRewriteLegacyProductCodeGrouping()
    {
        var input = Input(""); var order = input.Inputs.Orders[0];
        input = input with
        {
            Inputs = input.Inputs with
            {
                GroupingVersion = ProductGroupingVersion.ProductCodeFallback,
                Orders = [order with { Lines = order.Lines.Select(line => line with { SimilarityCode = null }).ToArray() }]
            }
        };
        var state = OrderInputRules.Apply(input, false);
        Assert.Empty(state.Demands);
        Assert.Equal(2, ImportedQuotationAnalysis.Calculate(input.Proposals, state).Count);
        Assert.All(state.Links, link => Assert.Equal(ProductGroupingVersion.ProductCodeFallback, link.GroupingVersion));
    }

    [Fact]
    public void ManualDemand_RequiresExplicitReplacementAndRefreshDoesNotRewriteOldSnapshots()
    {
        var input = Input(); string key = input.State.Links[0].GroupKey;
        input = input with { State = input.State with { Demands = [new(key, "M", 800)] } };
        Assert.Equal(800, Assert.Single(OrderInputRules.Apply(input, false).Demands).Quantity);
        var imported = OrderInputRules.Apply(input, true);
        Assert.Equal(1000, Assert.Single(imported.Demands).Quantity);
        var order = input.Inputs.Orders[0];
        var refreshed = input.Inputs with { Version = 2, Orders = [order with { Revision = "revision-2", Lines = [order.Lines[0] with { Quantity = 12 }] }] };
        Assert.Equal(1200, Assert.Single(OrderInputRules.Apply(input with { Inputs = refreshed, State = imported }, false).Demands).Quantity);
        Assert.Equal(1000, imported.Demands[0].Quantity); Assert.Equal("revision-1", imported.Demands[0].Sources[0].OrderRevision);
    }

    [Fact]
    public void IncompatibleMissingAndCancelledLines_LeaveDemandAndOrderReferenceUnavailable()
    {
        var input = Input(); var order = input.Inputs.Orders[0];
        foreach (var lines in new IReadOnlyList<PurchaseOrderLine>[]
        {
            [order.Lines[0] with { IsCancelled = true }], [order.Lines[0] with { BaseUnit = "KG" }],
            [order.Lines[0], order.Lines[1] with { BaseUnit = "KG" }],
            [order.Lines[0] with { SimilarityCode = "other" }]
        })
        {
            var changed = input with { Inputs = input.Inputs with { Orders = [order with { Lines = lines }] } };
            var state = OrderInputRules.Apply(changed, false);
            var group = Assert.Single(ImportedQuotationAnalysis.Calculate(changed.Proposals, state));
            Assert.Null(group.RequestedQuantity); Assert.Null(OrderInputRules.Price(group, changed.Inputs));
            Assert.Empty(state.Demands);
        }
    }

    [Fact]
    public void NameFallback_WithConflictingReviewedVariantsLeavesOrderDemandAndPurchasePending()
    {
        var input = Input("");
        var proposals = input.Proposals.Select((proposal, index) => proposal with
        {
            Review = [proposal.Review[0] with { Color = index == 0 ? "Blue" : "Red", UnitPrice = index == 0 ? 250 : 200, ReportedTotal = index == 0 ? 2500 : 2000 }]
        }).ToArray();
        var state = OrderInputRules.Apply(input with { Proposals = proposals }, false);
        Assert.Empty(state.Demands);
        var group = Assert.Single(ImportedQuotationAnalysis.Calculate(proposals, state));
        Assert.Null(group.Winner);
        Assert.All(group.Offers, offer => Assert.False(PurchasePreparationRules.IsEligible(group, offer)));
    }

    [Fact]
    public void MultipleOrders_AccumulateOnceAndKeepEachPriceContribution()
    {
        var input = Input(); var order = input.Inputs.Orders[0];
        input = input with { Inputs = input.Inputs with { Orders = [order, order with { Header = order.Header with { Id = "order-2" }, Lines = [order.Lines[0] with { Quantity = 5, UnitPrice = 400 }] }] } };
        var state = OrderInputRules.Apply(input, false);
        Assert.Equal(1500, Assert.Single(state.Demands).Quantity);
        var group = Assert.Single(ImportedQuotationAnalysis.Calculate(input.Proposals, state));
        var price = OrderInputRules.Price(group, input.Inputs)!;
        Assert.Equal(3, price.Contributions.Count); Assert.Equal(1500, price.Quantity);
        Assert.Equal(4400m / 1500m, price.UnitPrice);
    }

    [Fact]
    public async Task WorkspaceSave_IsAtomicPersistentAndProtectsFrozenPurchasesAndManualConflicts()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        var input = Input("");
        var quotation = await store.SaveQuotationAsync(input.Quotation with { Revision = 0 }, Token);
        foreach (var proposal in input.Proposals) await store.SaveProposalAsync(proposal with { QuotationId = quotation.Id }, [1, 2, 3], Token);
        var current = await store.GetAnalysisInputAsync(quotation.Id, Token);
        await store.SaveAnalysisAsync(current.State with { Links = input.State.Links, Demands = [new(input.State.Links[0].GroupKey, "M", 500), new(input.State.Links[1].GroupKey, "M", 600)] }, Token);
        var request = input.Inputs with { Version = 0 };
        Assert.Equal(AnalysisError.DemandReviewRequired, (await Assert.ThrowsAsync<AnalysisException>(() => store.SaveQuotationWorkspaceAsync(current.Quotation with { Name = "Uncommitted" }, request, false, Token))).Code);
        var afterFailure = await store.GetAnalysisInputAsync(quotation.Id, Token);
        Assert.Equal(current.Quotation, afterFailure.Quotation); Assert.Equal(0, afterFailure.Inputs.Version);
        Assert.Equal(2, afterFailure.State.Demands.Count);
        await store.SaveQuotationWorkspaceAsync(current.Quotation, request, true, Token);
        var linked = await store.GetAnalysisInputAsync(quotation.Id, Token);
        Assert.Equal(1000, Assert.Single(linked.State.Demands).Quantity);
        var purchase = await store.PreparePurchaseAsync(new(quotation.Id, linked.Quotation.Revision, linked.State.Version, 0, [new(input.Proposals[0].Id, input.Proposals[0].Review[0].CaptureId)]), Token);
        Assert.Equal(1, purchase.QuotationInputsVersion);
        var changed = linked.Inputs with { PriceReference = QuotationPriceReference.LastPurchase };
        await store.SaveQuotationWorkspaceAsync(linked.Quotation, changed, false, Token);
        var restarted = new SqliteImportStore(_directory); await restarted.InitializeAsync(Token);
        var loaded = await restarted.GetPurchaseInputAsync(quotation.Id, Token);
        Assert.Equal(2, loaded.Analysis.Inputs.Version); Assert.Equal(1, loaded.Preparation!.QuotationInputsVersion);
        Assert.Equal(1000, loaded.Preparation.Lines[0].RequestedQuantity);
        Assert.Equal(QuotationPriceReference.LinkedOrder, (await restarted.GetQuotationInputsAsync(quotation.Id, 1, Token)).PriceReference);
        Assert.Equal(QuotationPriceReference.LastPurchase, loaded.Analysis.Inputs.PriceReference);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.SaveQuotationWorkspaceAsync(loaded.Analysis.Quotation, loaded.Analysis.Inputs, false, canceled.Token));
        Assert.Equal(2, (await restarted.GetQuotationInputsAsync(quotation.Id, cancellationToken: Token)).Version);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Tests;

public sealed class SupplierQuotationComparisonTests
{
    private static AnalysisState WithPurchaseHistory(AnalysisState state)
    {
        ErpProductCandidate product = new("001", "Synthetic cable", "cable", false, "1", "Synthetic", "", null, "",
            new DateOnly(2026, 9, 1), 8m, "MT", "MT", "synthetic-catalog", DateTime.UnixEpoch);
        string key = ErpProductIdentity.GroupKey(product);
        return state with
        {
            Links = state.Links.Select(link => link with { GroupKey = key, Product = product }).ToArray(),
            Demands = state.Demands.Select(demand => demand with { GroupKey = key }).ToArray()
        };
    }

    [Fact]
    public void PurchaseTotals_UseEachSuppliersActualBaseQuantityAndSignedVariation()
    {
        var (proposals, state) = Scenario(); state = WithPurchaseHistory(state);
        proposals[1] = proposals[1] with { Review = [proposals[1].Review[0] with { Quantity = 20, Unit = "RL", UnitPrice = 780 }] };
        state = state with { Links = state.Links.Select((link, index) => index == 1 ? link with { BaseUnitsPerQuotedUnit = 100 } : link).ToArray() };
        var totals = SupplierQuotationComparison.Calculate(proposals, ImportedQuotationAnalysis.Calculate(proposals, state));
        Assert.Equal([12000m, 16000m, 12000m], totals.Select(row => row.LastPurchaseTotal));
        Assert.Equal([2.5m, -2.5m, 0m], totals.Select(row => row.PurchaseVariationPercent));
        totals = SupplierQuotationComparison.Calculate([proposals[1]], ImportedQuotationAnalysis.Calculate([proposals[1]], state with { Demands = [] }));
        Assert.Equal(-2.5m, Assert.Single(totals).PurchaseVariationPercent);
        Assert.Null(totals[0].CommonTotal);
    }

    [Fact]
    public void PurchaseTotals_WeightAllIncludedProductsByCostInsteadOfAveragingPercentages()
    {
        var (proposals, state) = Scenario(); state = WithPurchaseHistory(state);
        var extra = proposals[0].Review[0] with { CaptureId = Guid.NewGuid(), Quantity = 10, UnitPrice = 100, ReportedTotal = 1000 };
        proposals[0] = proposals[0] with { Review = [.. proposals[0].Review, extra, extra with { CaptureId = Guid.NewGuid(), IsExcluded = true }] };
        var product = state.Links[0].Product! with { ProductCode = "002", SimilarityCode = "extra", LastPurchaseUnitPrice = 50 };
        state = state with { Links = [.. state.Links, state.Links[0] with { CaptureId = extra.CaptureId, GroupKey = ErpProductIdentity.GroupKey(product), Product = product }] };
        var totals = SupplierQuotationComparison.Calculate(proposals, ImportedQuotationAnalysis.Calculate(proposals, state));
        Assert.Equal(12500m, totals[0].LastPurchaseTotal);
        Assert.Equal(13300m, totals[0].QuotedTotal);
        Assert.Equal(6.4m, totals[0].PurchaseVariationPercent);
        Assert.Equal(12300m, totals[0].CommonTotal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("unit")]
    [InlineData("base-unit")]
    [InlineData("quantity")]
    [InlineData("overflow")]
    public void PurchaseTotals_DoNotInventAReferenceWhenAnyItemHasInvalidHistory(string condition)
    {
        var (proposals, state) = Scenario(); state = WithPurchaseHistory(state);
        var link = state.Links[0]; var product = link.Product!;
        product = condition switch
        {
            "missing" => product with { LastPurchaseUnitPrice = null },
            "zero" => product with { LastPurchaseUnitPrice = 0 },
            "negative" => product with { LastPurchaseUnitPrice = -8 },
            "unit" => product with { LastPurchaseUnit = "KG" },
            "base-unit" => product with { BaseUnit = null },
            "overflow" => product with { LastPurchaseUnitPrice = decimal.MaxValue },
            _ => product
        };
        state = state with { Links = [link with { Product = product }, .. state.Links.Skip(1)] };
        if (condition == "quantity") proposals[0] = proposals[0] with { Review = [proposals[0].Review[0] with { Quantity = null }] };
        var totals = SupplierQuotationComparison.Calculate(proposals, ImportedQuotationAnalysis.Calculate(proposals, state));
        Assert.Null(totals[0].LastPurchaseTotal); Assert.Null(totals[0].PurchaseVariationPercent);
        Assert.Equal(16000m, totals[1].LastPurchaseTotal); Assert.Equal(-2.5m, totals[1].PurchaseVariationPercent);
    }

    [Fact]
    public void PurchaseTotals_RejectPartialHistoryEvenWhenTheCommonBasketHasHistory()
    {
        var (proposals, state) = Scenario(); state = WithPurchaseHistory(state);
        proposals[0] = proposals[0] with { Review = [.. proposals[0].Review, new(Guid.NewGuid(), "UNKNOWN", "Synthetic extra", 1, 10, 10, "MT", false, "")] };
        var totals = SupplierQuotationComparison.Calculate(proposals, ImportedQuotationAnalysis.Calculate(proposals, state));
        Assert.NotNull(totals[0].CommonTotal); Assert.Null(totals[0].LastPurchaseTotal); Assert.Null(totals[0].PurchaseVariationPercent);
    }

    [Fact]
    public void EqualQuotedPackages_AreComparableBeforePurchaseDemandIsSet()
    {
        var (proposals, state) = Scenario();
        proposals[1] = proposals[1] with { Review = [proposals[1].Review[0] with { Quantity = 1500, ReportedTotal = 11700m }] };
        state = state with { Demands = [] };
        var groups = ImportedQuotationAnalysis.Calculate(proposals, state);
        var totals = SupplierQuotationComparison.Calculate(proposals, groups);
        Assert.Equal([12300m, 11700m, 12000m], totals.Select(row => row.CommonTotal));
        Assert.Equal(5.13m, decimal.Round(totals[0].PremiumPercent!.Value, 2));
        Assert.All(groups, group => Assert.Null(group.Winner));
        Assert.Equal([0, 1, 0], totals.Select(row => row.LowestPriceItems));
        Assert.All(totals, total => Assert.Equal(PackageComparisonPending.None, total.PendingReason));
    }

    [Fact]
    public void LowestPriceCounts_SeparateTiesAndExplainWhyDifferentQuantitiesNeedDemand()
    {
        var (proposals, state) = Scenario();
        proposals[2] = proposals[2] with { Review = [proposals[2].Review[0] with { UnitPrice = 7.8m, ReportedTotal = 11700m }] };
        var groups = ImportedQuotationAnalysis.Calculate(proposals, state with { Demands = [] });
        var totals = SupplierQuotationComparison.Calculate(proposals, groups);
        Assert.Equal([0, 0, 0], totals.Select(row => row.LowestPriceItems));
        Assert.Equal([0, 1, 1], totals.Select(row => row.TiedLowestPriceItems));
        Assert.All(totals, total => Assert.Equal(PackageComparisonPending.MissingDemand, total.PendingReason));
        groups = ImportedQuotationAnalysis.Calculate(proposals, state with { Links = [] });
        totals = SupplierQuotationComparison.Calculate(proposals, groups);
        Assert.All(totals, total => { Assert.Equal(0, total.LowestPriceItems); Assert.Equal(PackageComparisonPending.MissingIdentity, total.PendingReason); });
    }

    private static (SavedProposal[] Proposals, AnalysisState State) Scenario()
    {
        var proposals = new[] { ("A", 1500m, 8.2m), ("B", 2000m, 7.8m), ("C", 1500m, 8m) }.Select(row =>
            new SavedProposal(Guid.NewGuid(), DateTime.UnixEpoch, "synthetic.pdf", "synthetic", new() { SupplierName = row.Item1 }, new([], "Synthetic"), new([], [], null, null),
                [new(Guid.NewGuid(), row.Item1, "Synthetic cable", row.Item2, row.Item3, row.Item2 * row.Item3, "MT", false, "")])).ToArray();
        AnalysisState state = new(Guid.NewGuid(), 0, 0, DateTime.UnixEpoch,
            proposals.Select(proposal => new ItemLink(proposal.Id, proposal.Review[0].CaptureId, "manual:cable", "Synthetic cable", "MT", 1, null, DateTime.UnixEpoch)).ToArray(), [new("manual:cable", "MT", 1500)]);
        return (proposals, state);
    }

    [Fact]
    public void Totals_CompareWholeLotsCoveringTheSameDemandWithoutChangingTheWinner()
    {
        var (proposals, state) = Scenario();
        var groups = ImportedQuotationAnalysis.Calculate(proposals, state);
        var totals = SupplierQuotationComparison.Calculate(proposals, groups);
        Assert.Equal("C", Assert.Single(groups).Winner!.SupplierName);
        Assert.Equal(7.8m, groups[0].LowestUnitPrice);
        Assert.Equal([12300m, 15600m, 12000m], totals.Select(row => row.QuotedTotal));
        Assert.Equal([12300m, 15600m, 12000m], totals.Select(row => row.CommonTotal));
        Assert.Equal([2.5m, 30m, 0m], totals.Select(row => row.PremiumPercent));
        Assert.All(totals, row => Assert.Equal(1, row.CommonGroups));
    }

    [Fact]
    public void Totals_KeepUnmatchedItemsInQuotedTotalsAndOutOfTheCommonBasket()
    {
        var (proposals, state) = Scenario();
        proposals[0] = proposals[0] with
        {
            Review = [.. proposals[0].Review, new(Guid.NewGuid(), "EXTRA", "Synthetic extra", 1, 300m, 300m, "MT", false, ""),
            new(Guid.NewGuid(), "EXCLUDED", "Synthetic excluded", 1, 900m, 900m, "MT", true, "Synthetic exclusion")]
        };
        var totals = SupplierQuotationComparison.Calculate(proposals, ImportedQuotationAnalysis.Calculate(proposals, state));
        Assert.Equal(12600m, totals[0].QuotedTotal); Assert.Equal(2, totals[0].IncludedItems);
        Assert.Equal(12300m, totals[0].CommonTotal); Assert.Equal(2.5m, totals[0].PremiumPercent);
    }

    [Theory]
    [InlineData("missing-demand")]
    [InlineData("insufficient")]
    [InlineData("unlinked")]
    [InlineData("incompatible")]
    [InlineData("ambiguous")]
    [InlineData("single")]
    public void Totals_LeaveUncomparableBasketsPending(string condition)
    {
        var (proposals, state) = Scenario();
        if (condition == "missing-demand") state = state with { Demands = [] };
        if (condition == "insufficient") state = state with { Demands = [new("manual:cable", "MT", 2000)] };
        if (condition == "unlinked") state = state with { Links = state.Links.Skip(1).ToArray() };
        if (condition == "incompatible") state = state with { Links = state.Links.Select((link, index) => index == 0 ? link with { BaseUnit = "KG" } : link).ToArray() };
        if (condition == "single") proposals = [proposals[0]];
        if (condition == "ambiguous")
        {
            var duplicate = proposals[0].Review[0] with { CaptureId = Guid.NewGuid() };
            proposals[0] = proposals[0] with { Review = [.. proposals[0].Review, duplicate] };
            state = state with { Links = [.. state.Links, state.Links[0] with { CaptureId = duplicate.CaptureId }] };
        }
        var totals = SupplierQuotationComparison.Calculate(proposals, ImportedQuotationAnalysis.Calculate(proposals, state));
        Assert.All(totals, row => { Assert.NotNull(row.QuotedTotal); Assert.Null(row.CommonTotal); Assert.Null(row.PremiumPercent); Assert.Equal(0, row.CommonGroups); });
    }

    [Fact]
    public void Totals_KeepOverflowUnknownAndDoNotInventZero()
    {
        var (proposals, state) = Scenario();
        proposals[0] = proposals[0] with { Review = [.. proposals[0].Review, new(Guid.NewGuid(), "LIMIT", "Synthetic limit", 1, decimal.MaxValue, decimal.MaxValue, "MT", false, "")] };
        var totals = SupplierQuotationComparison.Calculate(proposals, ImportedQuotationAnalysis.Calculate(proposals, state));
        Assert.Null(totals[0].QuotedTotal); Assert.Equal(12300m, totals[0].CommonTotal);
    }

    [Theory]
    [InlineData(12, 10, 20)]
    [InlineData(8, 10, -20)]
    [InlineData(10, 10, 0)]
    [InlineData(0, 10, -100)]
    public void Variation_UsesTheSignedReferencePrice(decimal current, decimal reference, decimal expected) => Assert.Equal(expected, PriceVariation.Percent(current, reference));

    [Fact]
    public void Variation_RequiresKnownValuesAndAPositiveReference()
    {
        Assert.Null(PriceVariation.Percent(null, 1)); Assert.Null(PriceVariation.Percent(1, null));
        Assert.Null(PriceVariation.Percent(1, 0)); Assert.Null(PriceVariation.Percent(-1, 1));
        Assert.Null(PriceVariation.Percent(decimal.MaxValue, 0.0000000000000000000000000001m));
    }
}

using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Tests;

public sealed class QuotationAnalysisTests
{
    [Fact]
    public void Calculate_SumsItemWinnersAndAllocatesOrdersBySupplier()
    {
        QuotationSummary result = QuotationAnalysis.Calculate(
        [
            new("001", 1500m, [new("A", 1500m, 8.2m), new("B", 2000m, 7.8m), new("C", 1500m, 8m)]),
            new("002", 10m, [new("A", 10m, 2m), new("B", 10m, 3m)]),
            new("003", 5m, [new("A", 5m, 8m), new("B", 5m, 4m)])
        ]);

        Assert.Equal(12040m, result.SelectedTotal);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.PendingCount);
        Assert.Equal(new SupplierAllocation("A", 1, 20m), result.Suppliers[0]);
        Assert.Equal(new SupplierAllocation("B", 1, 20m), result.Suppliers[1]);
        Assert.Equal(new SupplierAllocation("C", 1, 12000m), result.Suppliers[2]);
    }

    [Fact]
    public void Calculate_DoesNotCountTiesOrInsufficientOffersAsConfirmedWinners()
    {
        QuotationSummary result = QuotationAnalysis.Calculate(
        [
            new("001", 10m, [new("A", 10m, 2m), new("B", 10m, 2m)]),
            new("002", 50m, [new("A", 10m, 1m)]),
            new("003", 5m, [new("B", 5m, 3m)])
        ]);

        Assert.Equal(15m, result.SelectedTotal);
        Assert.Equal(2, result.PendingCount);
        Assert.False(result.IsComplete);
        Assert.True(result.Items[0].HasTie);
        Assert.Null(result.Items[0].Winner);
        Assert.False(result.Items[1].HasTie);
        Assert.Null(result.Items[1].Winner);
        Assert.Equal(new SupplierAllocation("A", 0, 0m), result.Suppliers[0]);
        Assert.Equal(new SupplierAllocation("B", 1, 15m), result.Suppliers[1]);
    }

    [Fact]
    public void Calculate_HandlesFiftyItemsWithoutLosingTotalsOrOrder()
    {
        QuotationLineOffers[] items = Enumerable.Range(1, 50)
            .Select(index => new QuotationLineOffers($"ITEM-{index:000}", 10m,
                [new("A", 10m, index), new("B", 10m, index + 1m)]))
            .ToArray();

        QuotationSummary result = QuotationAnalysis.Calculate(items);

        Assert.Equal(50, result.Items.Count);
        Assert.Equal("ITEM-050", result.Items[^1].ItemCode);
        Assert.Equal(12750m, result.SelectedTotal);
        Assert.Equal(new SupplierAllocation("A", 50, 12750m), result.Suppliers[0]);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Calculate_RejectsRepeatedItemIdentity() =>
        Assert.Throws<ArgumentException>(() => QuotationAnalysis.Calculate(
        [
            new("001", 1m, [new("A", 1m, 1m)]),
            new("001", 1m, [new("B", 1m, 1m)])
        ]));
}


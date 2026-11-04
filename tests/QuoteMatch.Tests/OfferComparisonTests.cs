using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Tests;

public sealed class OfferComparisonTests
{
    private static readonly SupplierOffer[] Offers =
    [
        new("A", 1500m, 8.20m),
        new("B", 2000m, 7.80m),
        new("C", 1500m, 8.00m)
    ];

    [Fact]
    public void Calculate_DistinguishesCheapestUnitFromCheapestFulfillment()
    {
        ComparisonResult result = OfferComparison.Calculate(1500m, Offers);

        Assert.Equal(7.80m, result.LowestUnitPrice);
        Assert.Equal(12000m, result.LowestFulfillmentCost);
        Assert.Equal("C", Assert.Single(result.Offers, offer => offer.IsRecommended).Offer.SupplierCode);

        OfferEvaluation cheapestUnit = Assert.Single(result.Offers, offer => offer.HasLowestUnitPrice);
        Assert.Equal("B", cheapestUnit.Offer.SupplierCode);
        Assert.Equal(15600m, cheapestUnit.Total);
        Assert.Equal(500m, cheapestUnit.Excess);
        Assert.False(cheapestUnit.IsRecommended);
        Assert.Equal(4.8780487804878048780487804900m, result.UnitPriceReductionPercent);
    }

    [Fact]
    public void Calculate_ExcludesInsufficientLotsWithoutInventingStock()
    {
        ComparisonResult result = OfferComparison.Calculate(1800m, Offers);

        Assert.Equal(15600m, result.LowestFulfillmentCost);
        Assert.Equal("B", Assert.Single(result.Offers, offer => offer.IsRecommended).Offer.SupplierCode);
        OfferEvaluation offerA = result.Offers.Single(offer => offer.Offer.SupplierCode == "A");
        Assert.Equal(300m, offerA.Shortfall);
        Assert.False(offerA.CanFulfill);
    }

    [Fact]
    public void Calculate_WhenNoLotCoversRequest_ReturnsNoRecommendation()
    {
        ComparisonResult result = OfferComparison.Calculate(2500m, Offers);

        Assert.Null(result.LowestFulfillmentCost);
        Assert.DoesNotContain(result.Offers, offer => offer.IsRecommended);
        Assert.All(result.Offers, offer => Assert.False(offer.CanFulfill));
    }

    [Fact]
    public void Calculate_PreservesFinancialPrecisionWithoutRoundingIntermediateValues()
    {
        ComparisonResult result = OfferComparison.Calculate(1m, [new("A", 3.125m, 0.123456m)]);

        Assert.Equal(0.385800000m, result.LowestFulfillmentCost);
        Assert.Equal(2.125m, result.Offers[0].Excess);
        Assert.Equal(0m, result.UnitPriceReductionPercent);
    }

    [Fact]
    public void Calculate_ReportsAllEqualEligibleTotalsAsTies()
    {
        ComparisonResult result = OfferComparison.Calculate(100m, [new("A", 100m, 2m), new("B", 200m, 1m)]);

        Assert.Equal(2, result.Offers.Count(offer => offer.IsRecommended));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_RejectsNonpositiveRequest(int quantity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OfferComparison.Calculate(quantity, Offers));

    [Fact]
    public void Calculate_RejectsMissingOffers() =>
        Assert.Throws<ArgumentException>(() => OfferComparison.Calculate(1500m, []));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void Calculate_RejectsInvalidOffer(int quantity, int unitPrice) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfferComparison.Calculate(1m, [new("A", quantity, unitPrice)]));
}


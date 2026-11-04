using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;

namespace QuoteMatch.Tests;

public sealed class OfferPricePositionTests
{
    private static AnalyzedGroup Group(bool linked = true, params (string Supplier, decimal Quantity, decimal Price, string Unit, decimal Factor)[] offers)
    {
        var proposals = offers.Select(offer => new SavedProposal(Guid.NewGuid(), DateTime.UnixEpoch, "synthetic.pdf", "synthetic", new() { SupplierName = offer.Supplier },
            new([], "Synthetic"), new([], [], null, null), [new(Guid.NewGuid(), offer.Supplier + "-001", "Synthetic cable", offer.Quantity, offer.Price, offer.Quantity * offer.Price, offer.Unit, false, "")])).ToArray();
        var links = linked ? proposals.Select((proposal, index) => new ItemLink(proposal.Id, proposal.Review[0].CaptureId, "manual:cable", "Synthetic cable",
            offers[index].Unit == "KG" ? "KG" : "MT", offers[index].Factor, null, DateTime.UnixEpoch)).ToArray() : [];
        return ImportedQuotationAnalysis.Calculate(proposals, new(Guid.NewGuid(), 0, 1, DateTime.UnixEpoch, links, [new("manual:cable", "MT", 1000)])).First();
    }

    [Fact]
    public void PricePosition_DistinguishesTheLowestUnitPriceFromTheCheapestCompleteLot()
    {
        var group = Group(true, ("A", 1000, 1.8m, "MT", 1), ("B", 2000, 1.7m, "MT", 1), ("C", 1000, 1.9m, "MT", 1));
        var lowest = group.Offers.Single(offer => offer.SupplierName == "B");
        var position = OfferPricePosition.Calculate(group, lowest);
        Assert.True(position.CanCompare); Assert.True(position.IsLowest); Assert.False(position.IsTiedLowest); Assert.Equal(0m, position.PremiumPercent);
        Assert.Equal("A", group.Winner!.SupplierName);
        Assert.Equal(5.88m, decimal.Round(OfferPricePosition.Calculate(group, group.Winner).PremiumPercent!.Value, 2));
        Assert.Equal(10.53m, decimal.Round(group.UnitPriceSpreadPercent!.Value, 2));
    }

    [Fact]
    public void PricePosition_LabelsEveryLowestPriceTieWithoutChoosingAWinner()
    {
        var group = Group(true, ("A", 1000, 1.8m, "MT", 1), ("B", 1000, 1.8m, "MT", 1), ("C", 1000, 2m, "MT", 1));
        Assert.True(group.HasTie); Assert.Null(group.Winner);
        foreach (var offer in group.Offers.Take(2))
        {
            var position = OfferPricePosition.Calculate(group, offer);
            Assert.True(position.IsLowest); Assert.True(position.IsTiedLowest); Assert.Equal(0m, position.PremiumPercent);
        }
        Assert.Equal(11.11m, decimal.Round(OfferPricePosition.Calculate(group, group.Offers[2]).PremiumPercent!.Value, 2));
        Assert.Equal(10m, group.UnitPriceSpreadPercent);
    }

    [Fact]
    public void PricePosition_UsesConfirmedBasePricesAcrossQuotedPackageUnits()
    {
        var group = Group(true, ("A", 10, 180m, "RL", 100), ("B", 1000, 1.9m, "MT", 1));
        Assert.Equal(1.8m, group.Offers[0].BaseUnitPrice);
        Assert.True(OfferPricePosition.Calculate(group, group.Offers[0]).IsLowest);
        Assert.Equal(5.56m, decimal.Round(OfferPricePosition.Calculate(group, group.Offers[1]).PremiumPercent!.Value, 2));
    }

    [Fact]
    public void PricePosition_DoesNotRankUnidentifiedSingleOrIncompatibleOffers()
    {
        foreach (var group in new[] { Group(false, ("A", 1000, 1.8m, "MT", 1)), Group(true, ("A", 1000, 1.8m, "MT", 1)),
            Group(true, ("A", 1000, 1.8m, "MT", 1), ("B", 1000, 1.9m, "KG", 1)) })
        {
            Assert.Null(group.UnitPriceSpreadPercent);
            foreach (var offer in group.Offers)
            {
                var position = OfferPricePosition.Calculate(group, offer);
                Assert.False(position.CanCompare); Assert.False(position.IsLowest); Assert.Null(position.PremiumPercent);
            }
        }
    }

    [Fact]
    public void PricePosition_KeepsAnOverflowingPercentageUnknownInsteadOfZero()
    {
        var group = Group(true, ("A", 1, 0.0000000000000000000000000001m, "MT", 1), ("B", 1, 1m, "MT", 1));
        var position = OfferPricePosition.Calculate(group, group.Offers[1]);
        Assert.True(position.CanCompare); Assert.False(position.IsLowest); Assert.Null(position.PremiumPercent);
        Assert.True(OfferPricePosition.Calculate(group, group.Offers[0]).IsLowest);
    }
}

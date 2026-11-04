using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Tests;

public sealed partial class PurchaseSynchronizationTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ErpBrandIdentity Brand = new("catalog", "manufacturer-1", "Synthetic brand");
    private static PurchaseLine Source(string group = "wire")
    {
        ErpProductCandidate product = new("000.01", "Synthetic wire", group, false, "manufacturer-1", "Synthetic manufacturer", "001-01", 100, "RL", null, null, "M", null, "catalog", Now) { Brand = Brand };
        ReviewedItem item = new(Guid.NewGuid(), "001-01", "Synthetic PDF description", 2, 200.123456789m, 400.246913578m, "RL", false, "");
        ItemLink link = new(Guid.NewGuid(), item.CaptureId, ErpProductIdentity.GroupKey(product), product.Name, "M", 100, product, Now);
        return new(link.GroupKey, "Synthetic wire", "M", 150, new(link.ProposalId, item, "Synthetic seller", link, 200, 2.00123456789m), "synthetic-hash", "synthetic.pdf", Guid.NewGuid(), 1);
    }
    private static BrandPurchaseProposal Proposal() => new(Guid.NewGuid(), 1, 3, 2, Brand, [Source()]);
    private static BuyingSupplierSnapshot Supplier() => new(Brand, new("catalog", "distributor-9", "Synthetic distributor"), "history-order", "history-revision", "branch-1", new(2026, 10, 26), Now);
    private static PurchaseOrderLine Line(string id = "line-1") => new(id, "000.01", "Synthetic wire", "RL", "M", 2, 100, 200.123456789m, 400.246913578m, 0, false) { SimilarityCode = "wire", Brand = Brand };
    private static PurchaseOrderSnapshot Order(params PurchaseOrderLine[] lines) => new("catalog", new("order-1", "42", "distributor-9", "Synthetic distributor", "branch-1", "open", new(2026, 10, 27), true), "revision-1", Now, lines.Length == 0 ? [Line()] : lines);

    [Fact]
    public void Preparation_IsPartitionedByExplicitBrandAndLeavesUnknownBrandsPending()
    {
        var source = Source(); var second = Source("other"); var secondBrand = Brand with { Id = "manufacturer-2", Name = "Other brand" };
        second = second with { Offer = second.Offer with { Link = second.Offer.Link! with { Product = second.Offer.Link.Product! with { Brand = secondBrand } } } };
        var unknown = Source("unknown"); unknown = unknown with { Offer = unknown.Offer with { Link = unknown.Offer.Link! with { Product = unknown.Offer.Link.Product! with { Brand = null } } } };
        var proposals = PurchaseSynchronization.Proposals(new(Guid.NewGuid(), 1, 3, 2, Now, 0, [source, second, unknown]));
        Assert.Equal(3, proposals.Count); Assert.Equal([Brand, secondBrand, null], proposals.Select(proposal => proposal.Brand));
        Assert.All(proposals, proposal => Assert.Single(proposal.Lines));
        Assert.Equal(SynchronizationError.MissingBrand, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Compare(proposals[2], Supplier(), "branch-1", null)).Code);
        Assert.NotEqual(source.Offer.Link!.Product!.SupplierId, Supplier().Supplier.Id);
    }

    [Fact]
    public void PriceOnlyDifference_OffersOnlyPriceAndPreservesEveryUnselectedField()
    {
        var order = Order(Line() with { UnitPrice = 210, Total = 420 });
        var preview = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", order);
        var change = Assert.Single(preview.Differences); Assert.Equal(SynchronizationChangeKind.Price, change.Kind);
        var selection = PurchaseSynchronization.Select(preview, [change.Id], false);
        var line = Assert.Single(selection.Lines);
        Assert.Equal("line-1", line.Control); Assert.Equal("000.01", line.ProductCode); Assert.Equal(2, line.Quantity); Assert.Equal(100, line.Factor);
        Assert.Equal(200.123456789m, line.UnitPrice); Assert.Equal(400.246913578m, line.Total); Assert.Empty(selection.RemovedControls);
        Assert.Equal(210, order.Lines[0].UnitPrice);
    }

    [Fact]
    public void EqualInputs_DoNotProduceAnEmptyOperation_AndQuantityChangesRemainSeparate()
    {
        var equal = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", Order());
        Assert.Empty(equal.Differences);
        Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(equal, [], false));
        var quantity = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", Order(Line() with { Quantity = 3 }));
        Assert.Equal(SynchronizationChangeKind.Quantity, Assert.Single(quantity.Differences).Kind);
        Assert.Equal(2, Assert.Single(PurchaseSynchronization.Select(quantity, [quantity.Differences[0].Id], false).Lines).Quantity);
    }

    [Fact]
    public void BrandReplacement_RequiresCoherentProductPackagingSupplierAndExplicitExtraLineRemoval()
    {
        var oldBrand = Brand with { Id = "old-brand", Name = "Old synthetic brand" };
        var old = Line() with { ProductCode = "old-product", Unit = "M", Quantity = 200, BaseUnitsPerUnit = 1, UnitPrice = 2, Brand = oldBrand };
        var extra = old with { Id = "extra-control", ProductCode = "other-product", SimilarityCode = "other" };
        var order = Order(old, extra); order = order with { Header = order.Header with { SupplierId = "old-distributor" } };
        var preview = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", order);
        Assert.Equal([SynchronizationChangeKind.Supplier, SynchronizationChangeKind.ProductAndPackage, SynchronizationChangeKind.Remove], preview.Differences.Select(change => change.Kind));
        var replacement = preview.Differences.Single(change => change.Kind == SynchronizationChangeKind.ProductAndPackage).Id;
        Assert.Equal(SynchronizationError.MissingSupplier, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview, [replacement], false)).Code);
        Assert.Equal(SynchronizationError.MixedBrands, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview, ["supplier", replacement], false)).Code);
        var ids = preview.Differences.Select(change => change.Id).ToArray();
        Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview, ids, false));
        var selected = PurchaseSynchronization.Select(preview, ids, true);
        var line = Assert.Single(selected.Lines); Assert.Equal("line-1", line.Control); Assert.Equal("000.01", line.ProductCode);
        Assert.Equal("RL", line.Unit); Assert.Equal(100, line.Factor); Assert.Equal(2, line.Quantity); Assert.Equal(200.123456789m, line.UnitPrice);
        Assert.Equal("distributor-9", selected.SupplierId); Assert.Equal(["extra-control"], selected.RemovedControls);
    }

    [Fact]
    public void ExtraLines_StayUnlessSelectedAndReceivedOrReadOnlyLinesCannotBeModified()
    {
        var extra = Line("extra-control") with { ProductCode = "extra-product", SimilarityCode = "other" };
        var preview = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", Order(Line() with { UnitPrice = 210 }, extra));
        var kept = PurchaseSynchronization.Select(preview, ["price:line-1"], false);
        Assert.Equal(2, kept.Lines.Count); Assert.Empty(kept.RemovedControls);
        var received = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", Order(Line() with { UnitPrice = 210, ReceivedQuantity = 1 }));
        Assert.Equal(SynchronizationError.ReadOnly, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(received, ["price:line-1"], false)).Code);
        Assert.Equal(SynchronizationError.ReadOnly, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview with { Target = preview.Target! with { Header = preview.Target.Header with { CanEdit = false } } }, ["price:line-1"], false)).Code);
    }

    [Fact]
    public void UnknownBrandExtra_CanBeExplicitlyRemovedButCannotBeKeptOrRemovedAfterReceiving()
    {
        var extra = Line("extra-control") with { ProductCode = "extra-product", SimilarityCode = "other", Brand = null };
        var preview = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", Order(Line() with { UnitPrice = 210 }, extra));
        Assert.Equal(SynchronizationError.MissingBrand, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview, ["price:line-1"], false)).Code);
        Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview, ["remove:extra-control"], false));
        var removed = PurchaseSynchronization.Select(preview, ["remove:extra-control"], true);
        Assert.Equal("line-1", Assert.Single(removed.Lines).Control); Assert.Equal(Brand, removed.Lines[0].Brand);
        Assert.Equal(210, removed.Lines[0].UnitPrice); Assert.Equal(["extra-control"], removed.RemovedControls);
        var received = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", Order(Line(), extra with { ReceivedQuantity = 1 }));
        Assert.Equal(SynchronizationError.ReadOnly, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(received, ["remove:extra-control"], true)).Code);
    }

    [Fact]
    public void NullSimilarity_SuggestsACoherentNameFallbackReplacementWithoutIncludingOrRemovingLines()
    {
        var source = Source(); var product = source.Offer.Link!.Product! with { SimilarityCode = "" };
        var link = source.Offer.Link with { Product = product, GroupKey = ErpProductIdentity.GroupKey(product, QuoteMatch.Domain.Quotations.ProductGroupingVersion.ProductNameFallback), GroupingVersion = QuoteMatch.Domain.Quotations.ProductGroupingVersion.ProductNameFallback };
        source = source with { GroupKey = link.GroupKey, Offer = source.Offer with { Link = link } };
        var proposal = Proposal() with { Lines = [source] };
        var old = Line() with { ProductCode = "other-brand-product", SimilarityCode = null, Brand = Brand with { Id = "old-brand" } };
        var preview = PurchaseSynchronization.Compare(proposal, Supplier(), "branch-1", Order(old));
        var change = Assert.Single(preview.Differences); Assert.Equal(SynchronizationChangeKind.ProductAndPackage, change.Kind);
        Assert.Equal("line-1", change.Target!.Id);
        Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview, [], false));
        var selected = PurchaseSynchronization.Select(preview, [change.Id], false);
        Assert.Equal("line-1", Assert.Single(selected.Lines).Control); Assert.Equal(Brand, selected.Lines[0].Brand);
        Assert.Empty(selected.RemovedControls);
    }

    [Fact]
    public void AmbiguousRepeatedProducts_RequireAnExplicitPairByLineControl()
    {
        var proposal = Proposal(); var order = Order(Line() with { UnitPrice = 210 }, Line("line-2"));
        Assert.Equal(SynchronizationError.InvalidPairing, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Compare(proposal, Supplier(), "branch-1", order)).Code);
        var preview = PurchaseSynchronization.Compare(proposal, Supplier(), "branch-1", order, [new("line-1", proposal.Lines[0].GroupKey)]);
        Assert.Equal([SynchronizationChangeKind.Price, SynchronizationChangeKind.Remove], preview.Differences.Select(change => change.Kind));
        Assert.Equal(2, PurchaseSynchronization.Select(preview, ["price:line-1"], false).Lines.Count);
        Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Compare(proposal, Supplier(), "branch-1", order, [new("missing", proposal.Lines[0].GroupKey)]));
    }

    [Fact]
    public void Create_IncludesOnlySelectedCompleteLotsAndNeverInventsAnOrderControl()
    {
        var preview = PurchaseSynchronization.Compare(Proposal(), Supplier(), "branch-1", null);
        var change = Assert.Single(preview.Differences); Assert.Equal(SynchronizationChangeKind.Include, change.Kind);
        var selected = PurchaseSynchronization.Select(preview, [change.Id], false);
        Assert.Null(Assert.Single(selected.Lines).Control); Assert.Equal(2, selected.Lines[0].Quantity);
        Assert.Equal(400.246913578m, selected.ItemSubtotal); Assert.Equal(150, preview.Proposal.Lines[0].RequestedQuantity);
        Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Compare(Proposal(), Supplier(), "other-branch", null));
    }
}

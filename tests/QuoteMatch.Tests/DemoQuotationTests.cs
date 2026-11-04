using QuoteMatch.Desktop.Demo;
using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Tests;

public sealed class DemoQuotationTests
{
    [Fact]
    public void Create_ProvidesFiftyDistinctItemsWithCompleteSupplierAllocations()
    {
        IReadOnlyList<DemoQuotationItem> items = DemoQuotation.Create();
        QuotationSummary result = QuotationAnalysis.Calculate(items.Select(item => item.Line).ToArray());

        Assert.Equal(50, items.Count);
        Assert.Equal(50, items.Select(item => item.Line.ItemCode).Distinct().Count());
        Assert.Equal("ITEM-050", items[^1].Line.ItemCode);
        Assert.Equal(98847m, result.SelectedTotal);
        Assert.True(result.IsComplete);
        Assert.Equal(50, result.Suppliers.Sum(supplier => supplier.ItemCount));
        Assert.Equal(98847m, result.Suppliers.Sum(supplier => supplier.Total));
        Assert.Equal(3, result.Suppliers.Count);
        Assert.All(result.Suppliers, supplier => Assert.True(supplier.ItemCount > 0));
        Assert.Equal("C", result.Items[0].Winner!.Offer.SupplierCode);
    }
}


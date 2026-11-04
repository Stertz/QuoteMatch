using QuoteMatch.Domain.Analysis;

namespace QuoteMatch.Desktop.Demo;

public sealed record DemoQuotationItem(string NameKey, string UnitKey, int Variant, QuotationLineOffers Line);

public static class DemoQuotation
{
    public static IReadOnlyList<DemoQuotationItem> Create()
    {
        (string Name, string Unit)[] catalog =
        [
            ("SampleCable", "MetreUnit"), ("SampleAdapter", "PieceUnit"),
            ("SamplePaper", "BoxUnit"), ("SampleConnector", "PieceUnit"),
            ("SampleLamp", "PieceUnit"), ("SampleGloves", "PairUnit"),
            ("SampleScrew", "BoxUnit"), ("SampleCleaner", "LitreUnit"),
            ("SampleTape", "PieceUnit"), ("SampleStorage", "PieceUnit")
        ];

        return Enumerable.Range(1, 50).Select(index =>
        {
            (string name, string unit) = catalog[(index - 1) % catalog.Length];
            decimal quantity = index == 1 ? 1500m : 10m * (index % 8 + 1);
            decimal basePrice = 5m + index * 1.35m;
            SupplierOffer[] offers = index == 1
                ? [new("A", 1500m, 8.20m), new("B", 2000m, 7.80m), new("C", 1500m, 8.00m)]
                : Enumerable.Range(0, 3).Select(supplier => new SupplierOffer(
                    ((char)('A' + supplier)).ToString(),
                    quantity,
                    basePrice + ((supplier + index) % 3) * 0.65m)).ToArray();

            return new DemoQuotationItem(name, unit, (index - 1) / catalog.Length + 1,
                new QuotationLineOffers($"ITEM-{index:000}", quantity, offers));
        }).ToArray();
    }
}

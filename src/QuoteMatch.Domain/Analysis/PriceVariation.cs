namespace QuoteMatch.Domain.Analysis;

public static class PriceVariation
{
    public static decimal? Percent(decimal? current, decimal? reference)
    {
        if (current is null or < 0 || reference is null or <= 0) return null;
        try { return (current.Value - reference.Value) / reference.Value * 100m; }
        catch (OverflowException) { return null; }
    }
}

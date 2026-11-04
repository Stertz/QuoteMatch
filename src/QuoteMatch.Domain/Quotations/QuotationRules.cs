namespace QuoteMatch.Domain.Quotations;

public enum QuotationError { InvalidName, Conflict, ReadOnly, Empty, OrderHistory, InvalidInputs }
public sealed class QuotationException(QuotationError code) : Exception($"Quotation operation failed: {code}.")
{
    public QuotationError Code { get; } = code;
}
public static class QuotationRules
{
    public static void ValidateSave(Quotation? existing, Quotation candidate, bool hasProposals)
    {
        if (candidate.Id == Guid.Empty || string.IsNullOrWhiteSpace(candidate.Name) || candidate.Name.Length > 160
            || candidate.Notes.Length > 4000 || !Enum.IsDefined(candidate.State)) throw new QuotationException(QuotationError.InvalidName);
        if (existing is null)
        {
            if (candidate.Revision != 0 || candidate.State != QuotationState.Draft) throw new QuotationException(QuotationError.Conflict);
        }
        else
        {
            if (candidate.Revision != existing.Revision) throw new QuotationException(QuotationError.Conflict);
            if (existing.State == QuotationState.Finalized && (candidate.State != QuotationState.Draft || existing.Name != candidate.Name || existing.Notes != candidate.Notes))
                throw new QuotationException(QuotationError.ReadOnly);
        }
        if (candidate.State == QuotationState.Finalized && !hasProposals) throw new QuotationException(QuotationError.Empty);
    }
}

namespace QuoteMatch.Domain.Quotations;

public enum QuotationState { Draft, Finalized }

public sealed record Quotation(Guid Id, string Name, string Notes, DateTime CreatedAtUtc, DateTime UpdatedAtUtc,
    QuotationState State, int Revision);

public sealed record QuotationSummary(Quotation Quotation, int ProposalCount);

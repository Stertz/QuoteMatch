using QuoteMatch.Domain.Quotations;

namespace QuoteMatch.Application.Quotations;

public interface IQuotationRepository
{
    Task<IReadOnlyList<QuotationSummary>> GetQuotationsAsync(CancellationToken cancellationToken = default);
    Task<Quotation> SaveQuotationAsync(Quotation quotation, CancellationToken cancellationToken = default);
    Task DeleteQuotationAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default);
    Task<QuotationInputs> GetQuotationInputsAsync(Guid quotationId, int? version = null, CancellationToken cancellationToken = default);
    Task<QuotationInputs> SaveQuotationInputsAsync(QuotationInputs inputs, int expectedQuotationRevision, CancellationToken cancellationToken = default);
    Task<Quotation> SaveQuotationWorkspaceAsync(Quotation quotation, QuotationInputs inputs, bool replaceManualDemand, CancellationToken cancellationToken = default);
}



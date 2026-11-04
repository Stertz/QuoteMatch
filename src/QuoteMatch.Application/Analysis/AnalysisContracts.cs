using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
namespace QuoteMatch.Application.Analysis;

public sealed record ProductReviewResult(AnalysisInput Input, IReadOnlyList<SupplierProductMapping> Mappings)
{
    public AnalysisState State => Input.State;
}
public sealed record ProductReviewOptions
{
    public QuotationInputs? Inputs { get; init; }
    public bool AllowLinkedUpdates { get; init; }
    public bool ReplaceManualDemand { get; init; }
    public IReadOnlyList<ProductDemandChange>? Demands { get; init; }
    public IReadOnlyList<SupplierProductMapping> RequiredMappings { get; init; } = [];
}

public interface IQuotationAnalysisRepository
{
    Task<AnalysisInput> GetAnalysisInputAsync(Guid quotationId, CancellationToken cancellationToken = default);
    Task<AnalysisState> SaveAnalysisAsync(AnalysisState state, CancellationToken cancellationToken = default);
    Task<ProductReviewResult> SaveProductReviewAsync(AnalysisState state, IReadOnlyList<SupplierProductMapping> mappings, CancellationToken cancellationToken = default, ProductReviewOptions? options = null);
}

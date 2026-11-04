using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Application.Purchasing;

public interface IPurchasePreparationRepository
{
    Task<PurchaseInput> GetPurchaseInputAsync(Guid quotationId, CancellationToken cancellationToken = default);
    Task<PurchasePreparation> PreparePurchaseAsync(PreparePurchaseRequest request, CancellationToken cancellationToken = default);
}

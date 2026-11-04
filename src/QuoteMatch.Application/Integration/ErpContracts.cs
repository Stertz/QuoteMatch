using QuoteMatch.Domain.Integration;
namespace QuoteMatch.Application.Integration;

public interface IErpProductLookup
{
    Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string supplierProductCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string similarityCode, CancellationToken cancellationToken);
}
public interface IErpConfigurationRepository
{
    Task<ErpConfiguration> GetErpConfigurationAsync(CancellationToken cancellationToken = default);
}
public interface IErpBuyingSupplierLookup
{
    Task<BuyingSupplierSnapshot?> FindAsync(ErpBrandIdentity brand, string branch, CancellationToken cancellationToken);
}
public interface IErpCredentials
{
    bool CanPersist { get; }
    Task SaveAsync(ErpConfiguration configuration, string password, CancellationToken cancellationToken);
    Task<string?> GetAsync(ErpConfiguration configuration, CancellationToken cancellationToken);
}

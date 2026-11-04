using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Application.Integration;

public interface IProductMappingRepository
{
    Task<SupplierProductMapping?> FindMappingAsync(string catalogIdentity, SupplierProductReference source, CancellationToken cancellationToken);
    Task<SupplierProductMapping> SaveMappingAsync(SupplierProductMapping mapping, CancellationToken cancellationToken);
}

public interface IErpProductSearch
{
    Task<string> GetCatalogIdentityAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string productCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken cancellationToken);
}

public enum ProductIdentificationOrigin { NotFound, AlternativeCode, SavedMapping, InternalCode }
public sealed record ProductIdentificationResult(IReadOnlyList<ErpProductCandidate> Candidates,
    ProductIdentificationOrigin Origin, SupplierProductMapping? Mapping = null, bool IsOffline = false);

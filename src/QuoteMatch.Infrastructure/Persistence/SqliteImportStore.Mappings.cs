using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Infrastructure.Persistence;

public sealed partial class SqliteImportStore : IProductMappingRepository
{
    public async Task<SupplierProductMapping?> FindMappingAsync(string catalogIdentity, SupplierProductReference source, CancellationToken cancellationToken)
    {
        await using ImportDbContext context = CreateContext();
        string key = source.Key;
        string? json = await context.ProductMappings.AsNoTracking().Where(row => row.CatalogIdentity == catalogIdentity && row.SourceKey == key)
            .OrderByDescending(row => row.Version).Select(row => row.DataJson).FirstOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<SupplierProductMapping>(json, JsonOptions);
    }

    public async Task<SupplierProductMapping> SaveMappingAsync(SupplierProductMapping mapping, CancellationToken cancellationToken)
    {
        await using ImportDbContext context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var saved = await AddMappingAsync(context, mapping, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    private static async Task<SupplierProductMapping> AddMappingAsync(ImportDbContext context, SupplierProductMapping mapping, CancellationToken cancellationToken)
    {
        mapping.Validate();
        string key = mapping.Source.Key;
        string catalog = mapping.Product.CatalogIdentity;
        var latest = await context.ProductMappings.AsNoTracking().Where(row => row.CatalogIdentity == catalog && row.SourceKey == key)
            .OrderByDescending(row => row.Version).FirstOrDefaultAsync(cancellationToken);
        SupplierProductMapping? previous = latest is null ? null : JsonSerializer.Deserialize<SupplierProductMapping>(latest.DataJson, JsonOptions);
        if (mapping.Version != (previous?.Version ?? 0) || (previous is not null && previous.Id != mapping.Id))
            throw new ProductMappingException(ProductMappingError.Conflict);
        DateTime now = DateTime.UtcNow;
        SupplierProductMapping saved = mapping with { Version = mapping.Version + 1, CreatedAtUtc = previous?.CreatedAtUtc ?? now, UpdatedAtUtc = now };
        context.ProductMappings.Add(new() { CatalogIdentity = catalog, SourceKey = key, Version = saved.Version, DataJson = JsonSerializer.Serialize(saved, JsonOptions) });
        return saved;
    }
}

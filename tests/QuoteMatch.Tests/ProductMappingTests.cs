using Microsoft.Data.Sqlite;
using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed class ProductMappingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Mapping.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SupplierProductReference Source => new("Synthetic supplier", "000.01", "Synthetic cable", "Blue", "RL");
    private static ErpProductCandidate Product(string code = "123", string catalog = "synthetic-catalog") =>
        new(code, "Synthetic cable", "Synthetic group", false, "1", "Synthetic supplier", "full-code", 100, "RL", null, null, "MT", "MT", catalog, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    private static SupplierProductMapping Mapping => new(Guid.NewGuid(), 0, Source, Product(), "MT", 100,
        ProductMappingState.Active, DateTime.UnixEpoch, DateTime.UnixEpoch);

    [Fact]
    public void Reference_SeparatesSupplierCodeDescriptionColorUnitAndRetainsCodeFormatting()
    {
        Assert.Equal(Source.Key, (Source with { Supplier = " SYNTHETIC  supplier ", Description = "synthetic CABLE" }).Key);
        foreach (var other in new[] { Source with { Supplier = "Other" }, Source with { Code = "00001" }, Source with { Code = "1" },
            Source with { Description = "Synthetic cable 2" }, Source with { Color = "Red" }, Source with { Unit = "MT" } })
            Assert.NotEqual(Source.Key, other.Key);
        Assert.Equal("000.01 · Synthetic cable · Blue · RL", Source.Display);
        Assert.NotEqual(new SupplierProductReference("a", "bc", "d", "", "MT").Key, new SupplierProductReference("ab", "c", "d", "", "MT").Key);
    }

    [Fact]
    public async Task SqliteMappings_MigratePersistReviseAndProtectCreationMetadata()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        var first = await store.SaveMappingAsync(Mapping, Token);
        Assert.Equal(1, first.Version); Assert.Equal(DateTimeKind.Utc, first.CreatedAtUtc.Kind);
        Assert.NotEqual(DateTime.UnixEpoch, first.CreatedAtUtc);
        var second = await store.SaveMappingAsync(first with { Product = Product("456"), ConversionFactor = 250.123456789m, CreatedAtUtc = DateTime.MaxValue }, Token);
        Assert.Equal(first.Id, second.Id); Assert.Equal(2, second.Version); Assert.Equal(first.CreatedAtUtc, second.CreatedAtUtc);
        Assert.Equal(second, await new SqliteImportStore(_directory).FindMappingAsync("synthetic-catalog", Source, Token));
        Assert.Null(await store.FindMappingAsync("other-catalog", Source, Token));
        Assert.Null(await store.FindMappingAsync("synthetic-catalog", Source with { Supplier = "Other" }, Token));
        Assert.Equal(ProductMappingError.Conflict, (await Assert.ThrowsAsync<ProductMappingException>(() => store.SaveMappingAsync(first, Token))).Code);
        Assert.Equal(ProductMappingError.Conflict, (await Assert.ThrowsAsync<ProductMappingException>(() => store.SaveMappingAsync(second with { Id = Guid.NewGuid() }, Token))).Code);

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "imports.db") }.ToString());
        await connection.OpenAsync(Token);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DataJson FROM ProductMappingRevisions ORDER BY Version";
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token)); Assert.Contains("\"ProductCode\": \"123\"", reader.GetString(0));
        Assert.True(await reader.ReadAsync(Token)); Assert.Contains("250.123456789", reader.GetString(0));
        Assert.False(await reader.ReadAsync(Token));
    }

    [Theory]
    [InlineData(0, false, "MT")]
    [InlineData(-1, false, "MT")]
    [InlineData(100, true, "MT")]
    [InlineData(100, false, "RL")]
    public async Task SqliteMappings_RejectInvalidConversionsInactiveProductsAndRelabeledBaseUnits(decimal factor, bool inactive, string unit)
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        var invalid = Mapping with { ConversionFactor = factor, Product = Product() with { IsInactive = inactive }, BaseUnit = unit };
        Assert.Equal(ProductMappingError.Invalid, (await Assert.ThrowsAsync<ProductMappingException>(() => store.SaveMappingAsync(invalid, Token))).Code);
        Assert.Null(await store.FindMappingAsync("synthetic-catalog", Source, Token));
    }

    [Fact]
    public async Task Resolver_PrefersExactAlternativeCodeAndPreservesAmbiguousResults()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        await store.SaveMappingAsync(Mapping, Token);
        Lookup lookup = new() { Rows = [Product("700") with { SupplierProductCode = Source.Code }, Product("701") with { SupplierProductCode = Source.Code }, Product("999")] };
        var result = await new ErpIdentificationService(lookup).ResolveAsync(Source, "synthetic-catalog", store, Token);
        Assert.Equal(ProductIdentificationOrigin.AlternativeCode, result.Origin);
        Assert.Equal(new[] { "700", "701" }, result.Candidates.Select(row => row.ProductCode)); Assert.Null(result.Mapping);
    }

    [Fact]
    public async Task Resolver_ReusesConfirmedFactorForTheSameExactProductWithoutOverridingIdentityOrUnit()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        await store.SaveMappingAsync(Mapping with { ConversionFactor = 250 }, Token);
        var fresh = Product() with { SupplierProductCode = Source.Code, AlternativeQuantity = 100 };
        var result = await new ErpIdentificationService(new Lookup { Rows = [fresh] }).ResolveAsync(Source, "synthetic-catalog", store, Token);
        Assert.Equal(ProductIdentificationOrigin.AlternativeCode, result.Origin);
        Assert.Equal(fresh, Assert.Single(result.Candidates)); Assert.Equal(250, result.Mapping?.ConversionFactor);
        foreach (var changed in new[] { fresh with { ProductCode = "different" }, fresh with { BaseUnit = "KG" }, fresh with { IsInactive = true } })
        {
            var unresolved = await new ErpIdentificationService(new Lookup { Rows = [changed] }).ResolveAsync(Source, "synthetic-catalog", store, Token);
            Assert.Equal(changed, Assert.Single(unresolved.Candidates)); Assert.Null(unresolved.Mapping);
        }
    }

    [Fact]
    public async Task Resolver_UsesSavedMappingOnlyForTheSameReferenceAndCatalog()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        var saved = await store.SaveMappingAsync(Mapping, Token);
        ErpIdentificationService resolver = new(new Lookup());
        var result = await resolver.ResolveAsync(Source, "synthetic-catalog", store, Token);
        Assert.Equal(ProductIdentificationOrigin.SavedMapping, result.Origin); Assert.Equal(saved, result.Mapping);
        Assert.Equal("123", Assert.Single(result.Candidates).ProductCode); Assert.False(result.IsOffline);
        Assert.Equal(ProductIdentificationOrigin.NotFound, (await resolver.ResolveAsync(Source with { Color = "Red" }, "synthetic-catalog", store, Token)).Origin);
        Assert.Equal(ProductIdentificationOrigin.NotFound, (await resolver.ResolveAsync(Source, "another-catalog", store, Token)).Origin);
        await store.SaveMappingAsync(saved with { State = ProductMappingState.Disabled }, Token);
        Assert.Empty((await resolver.ResolveAsync(Source, "synthetic-catalog", store, Token)).Candidates);
    }

    [Theory]
    [InlineData(ErpError.ConnectionFailed)]
    [InlineData(ErpError.CredentialsUnavailable)]
    public async Task Resolver_ExposesOfflineReferenceWithoutDiscardingSavedMappings(ErpError failure)
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        var saved = await store.SaveMappingAsync(Mapping, Token);
        ErpIdentificationService resolver = new(new Lookup { Failure = failure });
        var result = await resolver.ResolveAsync(Source, "synthetic-catalog", store, Token);
        Assert.True(result.IsOffline); Assert.Equal(saved.Product, Assert.Single(result.Candidates));
        await Assert.ThrowsAsync<ErpException>(() => resolver.ResolveAsync(Source with { Unit = "KM" }, "synthetic-catalog", store, Token));
        Assert.Equal(saved, await store.FindMappingAsync("synthetic-catalog", Source, Token));
    }

    [Fact]
    public async Task Resolver_DoesNotHideInvalidQueriesOrCancellationBehindSavedReferences()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        await store.SaveMappingAsync(Mapping, Token);
        var error = await Assert.ThrowsAsync<ErpException>(() => new ErpIdentificationService(new Lookup { Failure = ErpError.QueryFailed }).ResolveAsync(Source, "synthetic-catalog", store, Token));
        Assert.Equal(ErpError.QueryFailed, error.Code);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ErpIdentificationService(new Lookup()).ResolveAsync(Source, "synthetic-catalog", store, canceled.Token));
    }

    private sealed class Lookup : IErpProductLookup
    {
        public List<string> Requests { get; } = [];
        public IReadOnlyList<ErpProductCandidate> Rows { get; init; } = [];
        public ErpError? Failure { get; init; }
        public Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string supplierProductCode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(supplierProductCode);
            if (Failure is { } failure) throw new ErpException(failure);
            return Task.FromResult(Rows);
        }
        public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string similarityCode, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Batch_SeparatesCompositeReferencesWhileQueryingEachAlternativeCodeOnce()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        await store.SaveMappingAsync(Mapping, Token);
        await store.SaveMappingAsync(Mapping with { Id = Guid.NewGuid(), Source = Source with { Color = "Red" }, Product = Product("456") }, Token);
        var quote = new QuoteMatch.Domain.Quotations.Quotation(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuoteMatch.Domain.Quotations.QuotationState.Draft, 1);
        QuoteMatch.Domain.Importing.ReviewedItem Item(string color) => new(Guid.NewGuid(), Source.Code, Source.Description, 2, 180, 360, Source.Unit, false, "") { Color = color };
        var proposal = new QuoteMatch.Domain.Importing.SavedProposal(Guid.NewGuid(), DateTime.UtcNow, "synthetic.pdf", "hash", new() { SupplierName = Source.Supplier }, new([], "Synthetic"), new([], [], null, null), [Item("Blue"), Item("Red"), Item("Blue")]);
        var input = new QuoteMatch.Domain.Analysis.AnalysisInput(quote, [proposal], new(quote.Id, 0, 1, DateTime.UtcNow, [], []));
        Lookup lookup = new();
        var result = await new ErpIdentificationService(lookup).ResolvePendingAsync(input, "synthetic-catalog", store, Token);
        Assert.Equal(new[] { Source.Code }, lookup.Requests); Assert.Equal(2, result.Count);
        Assert.Equal("123", Assert.Single(result[Source.Key].Candidates).ProductCode);
        Assert.Equal("456", Assert.Single(result[(Source with { Color = "Red" }).Key].Candidates).ProductCode);
        Assert.Empty(input.State.Links);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

using Microsoft.Data.Sqlite;
using QuoteMatch.Application.Analysis;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed partial class ProductReviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.ProductReview.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private async Task<(SqliteImportStore Store, AnalysisInput Input, ProductConfirmation[] Rows)> CreateAsync(bool duplicate = false)
    {
        var store = new SqliteImportStore(_directory); await store.InitializeAsync(Token);
        var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic bulk review", "", Now, Now, QuotationState.Draft, 0), Token);
        List<ProductConfirmation> rows = [];
        for (int index = 0; index < (duplicate ? 3 : 2); index++)
        {
            bool direct = index == 1;
            var item = new ReviewedItem(Guid.NewGuid(), direct ? "000.02" : "ALT.001", "Synthetic product", direct ? 3 : 2,
                direct ? 8 : 180, direct ? 24 : 360, direct ? "UN" : "RL", false, "")
            { Color = "Blue" };
            var template = new PdfTemplate
            {
                Id = Guid.NewGuid(),
                Version = 1,
                SupplierName = "Synthetic supplier",
                SourceFormat = direct ? ImportSourceFormat.Excel : ImportSourceFormat.Pdf,
                CodeKind = direct ? ProductCodeKind.Internal : ProductCodeKind.Supplier
            };
            var proposal = new SavedProposal(Guid.NewGuid(), Now, direct ? "synthetic.xlsx" : "synthetic.pdf", "synthetic-hash", template,
                new([], "Synthetic"), new([], [], null, null), [item])
            { QuotationId = quote.Id };
            await store.SaveProposalAsync(proposal, [1, 2], Token);
            var product = new ErpProductCandidate(direct ? "000.02" : "000.01", "Synthetic ERP product", "Shared similarity", false,
                "brand", "Synthetic brand", item.ProductCode, 100, "RL", new(2025, 1, 1), 1.7m,
                direct ? "UN" : "MT", direct ? "UN" : "MT", "catalog", Now);
            rows.Add(new(proposal.Id, item.CaptureId, product, product.BaseUnit!, direct ? 1 : 100));
        }
        return (store, await store.GetAnalysisInputAsync(quote.Id, Token), rows.ToArray());
    }
    private static SupplierProductReference Source(AnalysisInput input, ProductConfirmation row)
    {
        var proposal = input.Proposals.Single(proposal => proposal.Id == row.ProposalId);
        return SupplierProductReference.From(proposal, proposal.Review.Single(item => item.CaptureId == row.CaptureId));
    }
    private static SupplierProductMapping Mapping(AnalysisInput input, ProductConfirmation row) => new(Guid.NewGuid(), 0, Source(input, row),
        row.Product, row.BaseUnit, row.ConversionFactor, ProductMappingState.Active, Now, Now);

    [Fact]
    public async Task Confirm_PersistsAllSelectedLinksAndMappingsInOneRevision_WithoutChangingSources()
    {
        var (store, input, rows) = await CreateAsync();
        var result = await new ProductReviewService(store, store).ConfirmAsync(input, rows, true, "catalog", Token);
        var saved = result.State;
        Assert.Equal(1, saved.Version); Assert.Equal(2, saved.Links.Count);
        Assert.Equal(ProductGroupingVersion.ExactProductCode, saved.Links.Single(link => link.ProposalId == rows[1].ProposalId).GroupingVersion);
        var restarted = new SqliteImportStore(_directory); var reloaded = await restarted.GetAnalysisInputAsync(input.Quotation.Id, Token);
        Assert.Equal(saved.Version, reloaded.State.Version); Assert.Equal(saved.Links.ToArray(), reloaded.State.Links.ToArray());
        foreach (var row in rows)
        {
            var mapping = (await restarted.FindMappingAsync("catalog", Source(input, row), Token))!;
            Assert.Contains(mapping, result.Mappings);
            Assert.Equal(row.ConversionFactor, mapping.ConversionFactor); Assert.Equal(1, mapping.Version);
            Assert.Equal(Source(input, row), mapping.Source);
            var before = input.Proposals.Single(proposal => proposal.Id == row.ProposalId);
            Assert.Equal(before.Review.Single(), reloaded.Proposals.Single(proposal => proposal.Id == row.ProposalId).Review.Single());
            Assert.Equal([1, 2], (await restarted.GetOriginalPdfAsync(row.ProposalId, Token))!);
        }
        var offers = ImportedQuotationAnalysis.Calculate(reloaded.Proposals, reloaded.State).SelectMany(group => group.Offers).ToArray();
        Assert.Equal(200m, offers.Single(offer => offer.ProposalId == rows[0].ProposalId).BaseQuantity);
        Assert.Equal(1.8m, offers.Single(offer => offer.ProposalId == rows[0].ProposalId).BaseUnitPrice);
    }

    [Fact]
    public async Task Confirm_OnlySelectedRows_LeavesOthersPendingAndDoesNotSaveMappingsWhenDisabled()
    {
        var (store, input, rows) = await CreateAsync();
        var result = await new ProductReviewService(store, store).ConfirmAsync(input, [rows[1] with { ConversionFactor = 1.1234567890123456789012345678m }], false, "catalog", Token);
        var saved = result.State; Assert.Empty(result.Mappings);
        Assert.Equal(rows[1].CaptureId, Assert.Single(saved.Links).CaptureId);
        Assert.Equal(1.1234567890123456789012345678m, saved.Links[0].BaseUnitsPerQuotedUnit);
        Assert.Null(await store.FindMappingAsync("catalog", Source(input, rows[1]), Token));
    }

    [Theory]
    [InlineData("factor")]
    [InlineData("unit")]
    [InlineData("inactive")]
    [InlineData("code")]
    [InlineData("overflow")]
    public async Task InvalidLastRow_WritesNeitherEarlierLinksNorMappings(string invalid)
    {
        var (store, input, rows) = await CreateAsync();
        rows[1] = invalid switch
        {
            "factor" => rows[1] with { ConversionFactor = 0 },
            "unit" => rows[1] with { BaseUnit = "KG" },
            "inactive" => rows[1] with { Product = rows[1].Product with { IsInactive = true } },
            "code" => rows[1] with { Product = rows[1].Product with { ProductCode = "DIFFERENT" } },
            _ => rows[1] with { ConversionFactor = decimal.MaxValue }
        };
        Assert.Equal(AnalysisError.InvalidLink, (await Assert.ThrowsAsync<AnalysisException>(() => new ProductReviewService(store, store).ConfirmAsync(input, rows, true, "catalog", Token))).Code);
        Assert.Empty((await store.GetAnalysisInputAsync(input.Quotation.Id, Token)).State.Links);
        Assert.Null(await store.FindMappingAsync("catalog", Source(input, rows[0]), Token));
    }

    [Fact]
    public async Task MappingConflictInsideSqlite_RollsBackTheWholeReview()
    {
        var (store, input, rows) = await CreateAsync();
        var mappings = rows.Select(row => Mapping(input, row)).ToArray();
        var concurrent = await store.SaveMappingAsync(mappings[1], Token);
        var state = ProductReviewRules.Apply(input, rows, Now);
        Assert.Equal(ProductMappingError.Conflict, (await Assert.ThrowsAsync<ProductMappingException>(() => store.SaveProductReviewAsync(state, mappings, Token))).Code);
        var reloaded = await store.GetAnalysisInputAsync(input.Quotation.Id, Token);
        Assert.Equal(0, reloaded.State.Version); Assert.Empty(reloaded.State.Links);
        Assert.Null(await store.FindMappingAsync("catalog", mappings[0].Source, Token));
        Assert.Equal(concurrent, await store.FindMappingAsync("catalog", mappings[1].Source, Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleQuotationOrAnalysis_DoesNotSaveAnyMapping(bool changeQuotation)
    {
        var (store, input, rows) = await CreateAsync();
        if (changeQuotation) await store.SaveQuotationAsync(input.Quotation with { Notes = "Synthetic concurrent change" }, Token);
        else await store.SaveAnalysisAsync(input.State, Token);
        Assert.Equal(AnalysisError.Conflict, (await Assert.ThrowsAsync<AnalysisException>(() => new ProductReviewService(store, store).ConfirmAsync(input, rows, true, "catalog", Token))).Code);
        Assert.Empty((await store.GetAnalysisInputAsync(input.Quotation.Id, Token)).State.Links);
        Assert.Null(await store.FindMappingAsync("catalog", Source(input, rows[0]), Token));
    }

    [Fact]
    public async Task RepeatedReferences_ShareOneMapping_AndRejectConflictingConversions()
    {
        var (store, input, rows) = await CreateAsync(true);
        var service = new ProductReviewService(store, store);
        Assert.Equal(ProductMappingError.ConflictingConversion, (await Assert.ThrowsAsync<ProductMappingException>(() => service.ConfirmAsync(input,
            [rows[0], rows[2] with { ConversionFactor = 50 }], true, "catalog", Token))).Code);
        Assert.Empty((await store.GetAnalysisInputAsync(input.Quotation.Id, Token)).State.Links);
        var saved = await service.ConfirmAsync(input, [rows[0], rows[2]], true, "catalog", Token);
        Assert.Equal(2, saved.State.Links.Count); Assert.Single(saved.Mappings);
        Assert.Equal(1, (await store.FindMappingAsync("catalog", Source(input, rows[0]), Token))!.Version);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("source")]
    [InlineData("catalog")]
    [InlineData("empty")]
    [InlineData("remember")]
    [InlineData("cancel")]
    public async Task InvalidBatchContracts_LeaveTheSavedAnalysisUnchanged(string invalid)
    {
        var (store, input, rows) = await CreateAsync();
        if (invalid == "duplicate") await Assert.ThrowsAsync<AnalysisException>(() => new ProductReviewService(store, store).ConfirmAsync(input, [rows[0], rows[0]], false, "catalog", Token));
        if (invalid == "source") await Assert.ThrowsAsync<AnalysisException>(() => new ProductReviewService(store, store).ConfirmAsync(input, [rows[0] with { CaptureId = Guid.NewGuid() }], false, "catalog", Token));
        if (invalid == "catalog") await Assert.ThrowsAsync<ProductMappingException>(() => new ProductReviewService(store, store).ConfirmAsync(input, rows, true, "other", Token));
        if (invalid == "empty") await Assert.ThrowsAsync<AnalysisException>(() => new ProductReviewService(store, store).ConfirmAsync(input, [], false, "catalog", Token));
        if (invalid == "remember") await Assert.ThrowsAsync<ProductMappingException>(() => new ProductReviewService(store, null).ConfirmAsync(input, rows, true, "catalog", Token));
        if (invalid == "cancel")
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProductReviewService(store, store).ConfirmAsync(input, rows, true, "catalog", canceled.Token));
        }
        Assert.Equal(0, (await store.GetAnalysisInputAsync(input.Quotation.Id, Token)).State.Version);
    }

    [Fact]
    public async Task Sqlite_RejectsAMappingThatDoesNotMatchTheConfirmedRow()
    {
        var (store, input, rows) = await CreateAsync();
        var state = ProductReviewRules.Apply(input, rows, Now);
        var mapping = Mapping(input, rows[0]) with { ConversionFactor = 999 };
        Assert.Equal(ProductMappingError.Invalid, (await Assert.ThrowsAsync<ProductMappingException>(() => store.SaveProductReviewAsync(state, [mapping], Token))).Code);
        mapping = Mapping(input, rows[0]);
        Assert.Equal(ProductMappingError.ConflictingConversion, (await Assert.ThrowsAsync<ProductMappingException>(() => store.SaveProductReviewAsync(state, [mapping, mapping], Token))).Code);
        Assert.Equal(ProductMappingError.Invalid, (await Assert.ThrowsAsync<ProductMappingException>(() => store.SaveProductReviewAsync(state, [mapping with { State = ProductMappingState.Disabled }], Token))).Code);
        Assert.Empty((await store.GetAnalysisInputAsync(input.Quotation.Id, Token)).State.Links);
    }

    [Fact]
    public async Task ConfirmedMapping_UpdatesItsVersionAndPreservesCreationMetadata()
    {
        var (store, input, rows) = await CreateAsync();
        var previous = await store.SaveMappingAsync(Mapping(input, rows[0]) with { ConversionFactor = 50 }, Token);
        await new ProductReviewService(store, store).ConfirmAsync(input, rows, true, "catalog", Token);
        var saved = (await store.FindMappingAsync("catalog", previous.Source, Token))!;
        Assert.Equal(previous.Id, saved.Id); Assert.Equal(previous.CreatedAtUtc, saved.CreatedAtUtc); Assert.Equal(2, saved.Version);
        Assert.Equal(100m, saved.ConversionFactor);
    }

    [Fact]
    public async Task FinalizedOrAlreadyConfirmedRows_CannotBeSubmittedAgain()
    {
        var (store, input, rows) = await CreateAsync();
        var service = new ProductReviewService(store, store);
        await service.ConfirmAsync(input, [rows[0]], false, "catalog", Token);
        var current = await store.GetAnalysisInputAsync(input.Quotation.Id, Token);
        Assert.Equal(AnalysisError.InvalidLink, (await Assert.ThrowsAsync<AnalysisException>(() => service.ConfirmAsync(current, [rows[0]], false, "catalog", Token))).Code);
        await store.SaveQuotationAsync(current.Quotation with { State = QuotationState.Finalized }, Token);
        current = await store.GetAnalysisInputAsync(input.Quotation.Id, Token);
        Assert.Equal(AnalysisError.ReadOnly, (await Assert.ThrowsAsync<AnalysisException>(() => service.ConfirmAsync(current, [rows[1]], false, "catalog", Token))).Code);
        Assert.Single(current.State.Links);
    }

    [Fact]
    public async Task LinkedOrderDemand_IsCalculatedForTheExactInternalProductAfterBulkConfirmation()
    {
        var (_, input, rows) = await CreateAsync();
        var row = rows[1];
        var order = new PurchaseOrderSnapshot("catalog", new("order", "42", "supplier", "Synthetic buyer", "branch", "open", new(2026, 1, 1), true), "revision", Now,
            [new("line", row.Product.ProductCode, row.Product.Name, "UN", "UN", 9, 1, 8, 72, 0, false) { SimilarityCode = row.Product.SimilarityCode }]);
        input = input with { Inputs = input.Inputs with { Orders = [order], GroupingVersion = ProductGroupingVersion.ProductNameFallback } };
        var state = ProductReviewRules.Apply(input, [row], Now);
        var demand = Assert.Single(state.Demands); Assert.Equal(9m, demand.Quantity); Assert.Equal(DemandOrigin.LinkedOrder, demand.Origin);
        Assert.Equal(ErpProductIdentity.GroupKey(row.Product, ProductGroupingVersion.ExactProductCode), demand.GroupKey);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

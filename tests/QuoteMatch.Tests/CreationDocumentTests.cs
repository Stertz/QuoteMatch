using System.Text.Json;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Testing;

namespace QuoteMatch.Tests;

public sealed class CreationDocumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SupplierDocumentChoice Choice(DocumentCombinationFixture fixture, DocumentQuantityChoice quantity, DocumentPriceChoice price = DocumentPriceChoice.NotRequired) =>
        new(CreationDocumentRules.Groups(fixture.Input.Analysis, fixture.Proposal).Single().SupplierKey, quantity, price);

    [Theory]
    [InlineData("Synthetic seller", true)]
    [InlineData(" synthetic   SELLER ", true)]
    [InlineData("Other seller", false)]
    public async Task OnlyMultipleDocumentsOfTheSelectedSupplier_RequireAQuantityDecision(string secondSupplier, bool expected)
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", 10, 5), new(secondSupplier, 20, 5));
        Assert.Equal(expected ? 1 : 0, CreationDocumentRules.Groups(fixture.Input.Analysis, fixture.Proposal).Count);
        if (expected)
            Assert.Equal(SynchronizationError.DocumentChoiceRequired, (await Assert.ThrowsAsync<SynchronizationException>(() => fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token))).Code);
        else
        {
            var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token);
            Assert.Null(preview.CreationDocuments);
        }
        var update = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", "existing-order", null, Token);
        Assert.Null(update.CreationDocuments);
    }

    [Theory]
    [InlineData(DocumentQuantityChoice.KeepSelected, 10, 50, 1)]
    [InlineData(DocumentQuantityChoice.Combine, 30, 150, 2)]
    public async Task SamePrice_AddsOnlyAfterExplicitYes_AndPreservesEveryOriginal(DocumentQuantityChoice quantity, int expectedQuantity, int expectedTotal, int documents)
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", 10, 5), new("Synthetic seller", 20, 5), new("Other seller", 100, 1), new("Synthetic seller", 99, 3, Product: "P2"));
        Assert.False(CreationDocumentRules.PriceConflictSuppliers(fixture.Input.Analysis, fixture.Proposal).Contains(Choice(fixture, quantity).SupplierKey));
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, quantity)]);
        var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
        var line = Assert.Single(selection.Lines); Assert.Equal(expectedQuantity, line.Quantity); Assert.Equal(5m, line.UnitPrice); Assert.Equal(expectedTotal, line.Total);
        Assert.Equal(documents, preview.CreationDocuments!.Sources.Count);
        Assert.DoesNotContain(preview.CreationDocuments.Sources, source => source.Line.Offer.SupplierName == "Other seller" || source.Line.Offer.Item.ProductCode == "P2");
        var reloaded = await fixture.Store.GetAnalysisInputAsync(fixture.Proposal.QuotationId, Token);
        Assert.Equal([1m, 3m, 5m, 5m], reloaded.Proposals.Select(proposal => proposal.Review[0].UnitPrice!.Value).Order());
        Assert.Equal(10m, fixture.Input.Preparation!.Lines.Single().Offer.Item.Quantity);
    }

    [Theory]
    [InlineData(DocumentPriceChoice.Highest, 1, 8, 240)]
    [InlineData(DocumentPriceChoice.WeightedAverage, 1, 7, 210)]
    [InlineData(DocumentPriceChoice.Separate, 2, 0, 210)]
    public async Task DifferentPrices_UseOnlyTheExplicitlyChosenTreatment(DocumentPriceChoice treatment, int lineCount, int expectedPrice, int expectedTotal)
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", 10, 5), new("Synthetic seller", 20, 8));
        Assert.True(CreationDocumentRules.PriceConflictSuppliers(fixture.Input.Analysis, fixture.Proposal).Contains(Choice(fixture, DocumentQuantityChoice.Combine).SupplierKey));
        Assert.Equal(SynchronizationError.DocumentPriceChoiceRequired, (await Assert.ThrowsAsync<SynchronizationException>(() => fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine)]))).Code);
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine, treatment)]);
        var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
        Assert.Equal(lineCount, selection.Lines.Count); Assert.Equal(30m, selection.Lines.Sum(line => line.Quantity)); Assert.Equal(expectedTotal, selection.ItemSubtotal);
        if (lineCount == 1) Assert.Equal(expectedPrice, selection.Lines[0].UnitPrice);
        else Assert.Equal([5m, 8m], selection.Lines.Select(line => line.UnitPrice).Order());
        var plan = await fixture.Writes.PreviewAsync(selection, Token, "open", new(2026, 10, 30));
        var roundTrip = JsonSerializer.Deserialize<SynchronizationWritePlan>(JsonSerializer.Serialize(plan, SqliteImportStore.JsonOptions), SqliteImportStore.JsonOptions)!;
        SynchronizationWriteRules.Validate(roundTrip); await fixture.Previews.ValidatePreviewAsync(roundTrip.Selection.Preview, Token);
        Assert.Equal(2, roundTrip.Selection.Preview.FinancialSources.Count);
    }

    [Fact]
    public async Task DifferentPackagesVariantsAndUnlinkedRows_DoNotBecomeImplicitConversionsOrSubstitutions()
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token,
            new("Synthetic seller", 10, 5), new("Synthetic seller", 2, 100, Unit: "BOX", Factor: 20),
            new("Synthetic seller", 9, 4, Color: "Blue"), new("Synthetic seller", 8, 3, Link: false));
        Assert.False(CreationDocumentRules.PriceConflictSuppliers(fixture.Input.Analysis, fixture.Proposal).Contains(Choice(fixture, DocumentQuantityChoice.Combine).SupplierKey));
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine)]);
        var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
        Assert.Equal(2, selection.Lines.Count); Assert.Equal(10, selection.Lines.Single(line => line.Unit == "UN").Quantity);
        Assert.Equal(2, selection.Lines.Single(line => line.Unit == "BOX").Quantity); Assert.Equal(20, selection.Lines.Single(line => line.Unit == "BOX").Factor);
        Assert.Equal(250m, selection.ItemSubtotal); Assert.Equal(2, preview.CreationDocuments!.Sources.Count);
    }

    [Fact]
    public async Task ConfirmedDecisionsAndSources_AreJournaledAndReconciledWithoutReplayingCreation()
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", 10, 5), new("Synthetic seller", 20, 8));
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine, DocumentPriceChoice.WeightedAverage)]);
        var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
        var plan = await fixture.Writes.PreviewAsync(selection, Token, "open", new(2026, 10, 30));
        fixture.LoseAcknowledgment = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Writes.ConfirmAsync(plan, true, false, Token));
        var pending = Assert.Single(await new SqliteImportStore(fixture.DirectoryPath).GetSynchronizationOperationsAsync(fixture.Proposal.QuotationId, Token));
        Assert.Equal(OrderOperationState.Unknown, pending.State); Assert.Equal(7m, pending.Plan.Selection.Lines.Single().UnitPrice);
        Assert.Equal(2, pending.Plan.Selection.Preview.CreationDocuments!.Sources.Count);
        Assert.Equal(DocumentPriceChoice.WeightedAverage, pending.Plan.Selection.Preview.CreationDocuments.Choices.Single().Prices);
        await fixture.Writes.ReconcileAsync(pending, Token);
        Assert.Equal(1, fixture.WriteCount); Assert.Equal(1, fixture.ReserveCount);
        Assert.Equal(OrderOperationState.Committed, (await fixture.Store.GetSynchronizationOperationsAsync(fixture.Proposal.QuotationId, Token)).Single().State);
    }

    [Fact]
    public async Task TamperedSourcesAndStaleInputs_AreRejectedBeforeJournaling()
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", 10, 5), new("Synthetic seller", 20, 5));
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine)]);
        var combination = preview.CreationDocuments!;
        var original = combination.Sources[0];
        var forged = original with { Line = original.Line with { SourceSha256 = "forged-hash" } };
        var tampered = preview with { CreationDocuments = combination with { Sources = [forged, .. combination.Sources.Skip(1)] } };
        Assert.Equal(SynchronizationError.Conflict, (await Assert.ThrowsAsync<SynchronizationException>(() => fixture.Previews.ValidatePreviewAsync(tampered, Token))).Code);
        var selection = PurchaseSynchronization.Select(tampered, tampered.Differences.Select(change => change.Id).ToArray(), false);
        var invalid = new SynchronizationWritePlan(Guid.NewGuid(), selection, new("synthetic-profile", 112) { Kind = OrderWriteKind.Create }) { Status = "open", Date = new(2026, 10, 30) };
        Assert.Equal(OrderError.InvalidReference, (await Assert.ThrowsAsync<OrderException>(() => fixture.Store.BeginSynchronizationAsync(invalid, Token))).Code);
        Assert.Empty(await fixture.Store.GetSynchronizationOperationsAsync(fixture.Proposal.QuotationId, Token));
        var input = await fixture.Store.GetAnalysisInputAsync(fixture.Proposal.QuotationId, Token);
        await fixture.Store.SaveQuotationAsync(input.Quotation with { Name = "Changed inputs" }, Token);
        Assert.Equal(SynchronizationError.Conflict, (await Assert.ThrowsAsync<SynchronizationException>(() => fixture.Previews.ValidatePreviewAsync(preview, Token))).Code);
        Assert.Equal(0, fixture.WriteCount);
    }

    [Fact]
    public async Task OneDocumentDoesNotRequireAQuestion_AndDecimalOverflowCannotProduceAPartialOrder()
    {
        await using (var single = await DocumentCombinationFixture.CreateAsync(Token, new CombinationDocument("Synthetic seller", 10, 5)))
            Assert.Empty(CreationDocumentRules.Groups(single.Input.Analysis, single.Proposal));
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", decimal.MaxValue, 1), new("Synthetic seller", 1, 1));
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine)]);
        Assert.Equal(SynchronizationError.NumericLimit, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false)).Code);
    }

    [Fact]
    public async Task SeparatePriceLines_RejectAnOverflowingOrderTotal()
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", decimal.MaxValue, 1), new("Synthetic seller", 1, 2));
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine, DocumentPriceChoice.Separate)]);
        Assert.Equal(SynchronizationError.NumericLimit, Assert.Throws<SynchronizationException>(() => PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false)).Code);
    }

    [Theory]
    [InlineData(DocumentPriceChoice.Highest)]
    [InlineData(DocumentPriceChoice.WeightedAverage)]
    public async Task PriceTreatment_DoesNotRecalculateReportedTotalsForProductsWithoutDifferentPrices(DocumentPriceChoice price)
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", 10, 5), new("Synthetic seller", 20, 8),
            new("Synthetic seller", 10, 7, Product: "P2", ReportedTotal: 100m));
        await fixture.SelectProductsAsync(Token, "P1", "P2");
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine, price)]);
        var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
        var unchanged = selection.Lines.Single(line => line.ProductCode == "P2");
        Assert.Equal(10m, unchanged.Quantity); Assert.Equal(7m, unchanged.UnitPrice); Assert.Equal(100m, unchanged.Total);
    }

    [Fact]
    public async Task FinancialPolicies_RecognizeValuesFromAdditionalConfirmedDocuments()
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", 10, 5),
            new("Synthetic seller", 20, 5, Financials: new() { IpiAmount = 7m }));
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [Choice(fixture, DocumentQuantityChoice.Combine)]);
        var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
        Assert.Contains(preview.CreationDocuments!.Sources, source => source.Line.Offer.Item.Financials.IpiAmount == 7m);
        var plan = new SynchronizationWritePlan(Guid.NewGuid(), selection, new("synthetic-financial", 112) { Kind = OrderWriteKind.Create, SupportsFinancialPolicies = true })
        { Status = "open", Date = new(2026, 10, 30), FinancialPolicy = new() { Taxes = SynchronizationFinancialSource.Document } };
        SynchronizationFinancialRules.ValidatePolicy(plan);
        Assert.Equal(3m, preview.FinancialSources.Sum(source => source.Values.Freight));
    }

    [Fact]
    public async Task SupplierGrouping_IsIndependentOfDocumentReadOrder_AndNeverAddsAnotherSupplier()
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token, new("Synthetic seller", 10, 5),
            new(" synthetic   SELLER ", 20, 5), new("Other seller", 7, 4, Product: "P2"));
        await fixture.SelectProductsAsync(Token, "P1", "P2");
        var ordered = fixture.Input.Analysis with { Proposals = fixture.Input.Analysis.Proposals.OrderBy(proposal => proposal.Template.SupplierName == "Synthetic seller" ? 0 : 1).ToArray() };
        var reversed = ordered with { Proposals = ordered.Proposals.Reverse().ToArray() };
        var first = CreationDocumentRules.Groups(ordered, fixture.Proposal).Single(); var second = CreationDocumentRules.Groups(reversed, fixture.Proposal).Single();
        Assert.Equal(first.SupplierName, second.SupplierName); Assert.Equal("Synthetic seller", first.SupplierName);
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token, [new(first.SupplierKey, DocumentQuantityChoice.Combine)]);
        var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
        Assert.Equal(30m, selection.Lines.Single(line => line.ProductCode == "P1").Quantity);
        Assert.Equal(7m, selection.Lines.Single(line => line.ProductCode == "P2").Quantity);
        Assert.Equal(178m, selection.ItemSubtotal);
    }

    [Fact]
    public async Task MultipleSupplierDecisions_KeepPriceConflictsAndQuantitiesIndependent()
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(Token,
            new("Seller A", 10, 5), new("Seller A", 20, 8), new("Seller B", 3, 4, Product: "P2"),
            new("Seller B", 7, 4, Product: "P2"), new("Seller C", 2, 2, Product: "P3"));
        await fixture.SelectProductsAsync(Token, "P1", "P2", "P3");
        Assert.Equal(["SELLER A"], CreationDocumentRules.PriceConflictSuppliers(fixture.Input.Analysis, fixture.Proposal).Order());
        Assert.Equal(2, CreationDocumentRules.Groups(fixture.Input.Analysis, fixture.Proposal).Count);
        var preview = await fixture.Previews.PreviewAsync(fixture.Proposal, "branch", null, null, Token,
            [new("SELLER A", DocumentQuantityChoice.Combine, DocumentPriceChoice.WeightedAverage), new("SELLER B", DocumentQuantityChoice.Combine)]);
        var selection = PurchaseSynchronization.Select(preview, preview.Differences.Select(change => change.Id).ToArray(), false);
        Assert.Equal(3, selection.Lines.Count); Assert.Equal(254m, selection.ItemSubtotal);
        Assert.Equal(30m, selection.Lines.Single(line => line.ProductCode == "P1").Quantity);
        Assert.Equal(10m, selection.Lines.Single(line => line.ProductCode == "P2").Quantity);
        Assert.Equal(2m, selection.Lines.Single(line => line.ProductCode == "P3").Quantity);
    }
}

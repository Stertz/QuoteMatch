using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Application.Analysis;
using QuoteMatch.Desktop.Controls;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    private static ProductReviewRow ConversionRow(ProductCodeKind kind = ProductCodeKind.Internal, string quotedUnit = "UN", string baseUnit = "UN",
        decimal quantity = 2, decimal? alternativeFactor = null, decimal? savedFactor = null, string color = "")
    {
        var proposal = AnalysisProposal(Guid.NewGuid(), "Synthetic supplier", quantity, 8, quotedUnit);
        proposal = proposal with { Template = proposal.Template with { CodeKind = kind }, Review = [proposal.Review[0] with { ProductCode = "000.01", Color = color }] };
        var item = proposal.Review[0];
        var product = new MappingLookup().Product with
        {
            ProductCode = item.ProductCode,
            BaseUnit = baseUnit,
            LastPurchaseUnit = baseUnit,
            LastPurchaseUnitPrice = 2.5m,
            SupplierProductCode = "ALT",
            AlternativeUnit = quotedUnit,
            AlternativeQuantity = alternativeFactor
        };
        var mapping = savedFactor is decimal factor ? new SupplierProductMapping(Guid.NewGuid(), 1, SupplierProductReference.From(proposal, item),
            product, baseUnit, factor, ProductMappingState.Active, DateTime.UnixEpoch, DateTime.UnixEpoch) : null;
        return new(proposal, item, [product], mapping, new Localizer());
    }

    [Fact]
    public async Task ConversionGrid_UsesConfirmedMappingOrMatchingAlternativeFactor_AndDoesNotAssumeDifferentUnits()
    {
        await session.Session.Dispatch(() =>
        {
            var saved = ConversionRow(ProductCodeKind.Supplier, "RL", "MT", savedFactor: 250, alternativeFactor: 100);
            Assert.Equal(250m, saved.ConversionFactor); Assert.Equal(500m, saved.SystemQuantity); Assert.False(saved.HasValidation);
            var alternative = ConversionRow(ProductCodeKind.Supplier, "RL", "MT", alternativeFactor: 100);
            Assert.Equal(100m, alternative.ConversionFactor); Assert.Equal(200m, alternative.SystemQuantity); Assert.Equal("R$ 0,0800/MT", alternative.SystemPrice);
            var missing = ConversionRow(ProductCodeKind.Internal, "RL", "MT", alternativeFactor: 100);
            Assert.Null(missing.ConversionFactor); Assert.True(missing.HasValidation);
            missing.SystemQuantity = 300; Assert.Equal(150m, missing.ConversionFactor); Assert.False(missing.HasValidation);
            var direct = ConversionRow(alternativeFactor: 100, color: "Synthetic Blue");
            Assert.True(direct.HasVariant); Assert.Equal("Cor: Synthetic Blue", direct.Variant); Assert.False(saved.HasVariant);
            Assert.Equal(1m, direct.ConversionFactor); Assert.Equal(2m, direct.SystemQuantity);
            direct.ConversionFactor = 2.5m; Assert.Equal(5m, direct.SystemQuantity);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("precision")]
    [InlineData("factor")]
    [InlineData("overflow")]
    [InlineData("inactive")]
    [InlineData("base-unit")]
    [InlineData("code")]
    [InlineData("missing")]
    [InlineData("price")]
    [InlineData("quantity")]
    public async Task ConversionGrid_InvalidInputsStayVisibleAndCannotBeConfirmed(string invalid)
    {
        await session.Session.Dispatch(() =>
        {
            var row = ConversionRow(quantity: invalid == "precision" ? 3 : invalid == "quantity" ? 0 : 2);
            if (invalid == "precision") { row.SystemQuantity = 1; Assert.Equal(row.Strings["BatchQuantityPrecision"], row.Validation); row.ConversionFactor = 1; Assert.False(row.HasValidation); row.SystemQuantity = 1; }
            if (invalid == "factor") row.ConversionFactor = 0;
            if (invalid == "overflow") row.ConversionFactor = decimal.MaxValue;
            if (invalid == "inactive") row.SelectProduct(row.SelectedCandidate!.Product with { IsInactive = true });
            if (invalid == "base-unit") row.SelectProduct(row.SelectedCandidate!.Product with { BaseUnit = null });
            if (invalid == "code") row.SelectProduct(row.SelectedCandidate!.Product with { ProductCode = "OTHER" });
            if (invalid == "missing") row.SelectedCandidate = null;
            if (invalid == "price")
            {
                var proposal = row.Proposal with { Review = [row.Item with { UnitPrice = decimal.MaxValue }] };
                row = new(proposal, proposal.Review[0], [row.SelectedCandidate!.Product], null, row.Strings); row.ConversionFactor = 0.1m;
                Assert.Equal("—", row.SystemPrice);
            }
            Assert.True(row.HasValidation); Assert.NotEmpty(row.Validation);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private async Task<(SqliteImportStore Store, QuotationAnalysisViewModel Model, SavedProposal Proposal, PreferenceStore Preferences)> BatchWorkspaceAsync(bool duplicateSource = false, PreferenceStore? preferences = null)
    {
        var store = new SqliteImportStore(_directory); await store.InitializeAsync();
        var now = DateTime.UtcNow;
        var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic grid study", "", now, now, QuotationState.Draft, 0));
        var proposal = AnalysisProposal(quote.Id, "Synthetic supplier", 2, 8, "un");
        proposal = proposal with
        {
            FileName = "synthetic.xlsx",
            Template = proposal.Template with { SourceFormat = ImportSourceFormat.Excel, CodeKind = ProductCodeKind.Internal },
            Review = [proposal.Review[0] with { ProductCode = "00.1-U" }, proposal.Review[0] with { CaptureId = Guid.NewGuid(), ProductCode = duplicateSource ? "00.1-U" : "00.2-U" }]
        };
        await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
        preferences ??= CreateStore();
        var model = new QuotationAnalysisViewModel(new Localizer(), store, new InternalBatchLookup(1), store, preferences);
        await model.LoadAsync(quote.Id); await model.IdentifyPendingCommand.ExecuteAsync(null);
        return (store, model, proposal, preferences);
    }

    [Fact]
    public async Task BatchConflict_KeepsTheWindowOpenAndDoesNotRememberAnUncommittedChoice()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, model, proposal, preferences) = await BatchWorkspaceAsync();
            var batch = model.CreateProductReview(); var window = new ProductReviewWindow { DataContext = batch }; window.Show();
            try
            {
                batch.RememberMappings = true;
                var current = await store.GetAnalysisInputAsync(proposal.QuotationId!.Value);
                await store.SaveAnalysisAsync(current.State);
                window.FindControl<Button>("ConfirmProductReview")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (batch.IsBusy) await Task.Yield(); Dispatcher.UIThread.RunJobs();
                Assert.False(batch.IsSaved); Assert.True(window.IsVisible); Assert.Equal(model.Strings["AnalysisErrorConflict"], batch.Status);
                Assert.False(preferences.Load().RememberProductMappings);
                Assert.Empty((await store.GetAnalysisInputAsync(proposal.QuotationId.Value)).State.Links);
                Assert.Null(await store.FindMappingAsync("synthetic-catalog", SupplierProductReference.From(proposal, proposal.Review[0]), TestContext.Current.CancellationToken));
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BatchMappingConflict_ShowsTheConflictingReferenceWithoutSavingAnyRow()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, model, proposal, preferences) = await BatchWorkspaceAsync(true);
            var batch = model.CreateProductReview(); batch.RememberMappings = true; batch.Rows[1].ConversionFactor = 2;
            await batch.SaveCommand.ExecuteAsync(null);
            Assert.False(batch.IsSaved); Assert.Equal(model.Strings["MappingErrorConflictingConversion"], batch.Status);
            Assert.False(preferences.Load().RememberProductMappings);
            Assert.Empty((await store.GetAnalysisInputAsync(proposal.QuotationId!.Value)).State.Links);
            batch.ClearSelectionCommand.Execute(null); Assert.False(batch.CanSave);
            batch.SelectAllCommand.Execute(null); batch.Rows[1].ConversionFactor = 1; batch.RememberMappings = false;
            await batch.SaveCommand.ExecuteAsync(null); Assert.True(batch.IsSaved);
            Assert.False(preferences.Load().RememberProductMappings);
            Assert.Null(await store.FindMappingAsync("synthetic-catalog", SupplierProductReference.From(proposal, proposal.Review[0]), TestContext.Current.CancellationToken));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SavingAMappingForOneRow_RefreshesTheSuggestionForRemainingIdenticalReferences()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, model, proposal, _) = await BatchWorkspaceAsync(true);
            var original = model.CreateProductReview().Rows[0];
            var source = SupplierProductReference.From(proposal, original.Item);
            await store.SaveMappingAsync(new(Guid.NewGuid(), 0, source, original.SelectedCandidate!.Product, original.BaseUnit, 50,
                ProductMappingState.Active, DateTime.UnixEpoch, DateTime.UnixEpoch), TestContext.Current.CancellationToken);
            await model.IdentifyPendingCommand.ExecuteAsync(null);
            var batch = model.CreateProductReview(); Assert.All(batch.Rows, row => Assert.Equal(50m, row.ConversionFactor));
            batch.Rows[0].ConversionFactor = 100; batch.Rows[1].IsSelected = false; batch.RememberMappings = true;
            await batch.SaveCommand.ExecuteAsync(null); Assert.True(batch.IsSaved, batch.Status);
            Assert.Equal(100m, (await store.FindMappingAsync("synthetic-catalog", source, TestContext.Current.CancellationToken))!.ConversionFactor);
            Assert.Equal(100m, Assert.Single(model.CreateProductReview().VisibleRows).ConversionFactor);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MappingPreference_PreservesAppearanceAndChangesOnlyAfterSaving()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, model, proposal, preferences) = await BatchWorkspaceAsync();
            var batch = model.CreateProductReview(); batch.RememberMappings = true;
            Assert.False(model.CreateProductReview().RememberMappings);
            Assert.True(preferences.TrySave(new("en-US", AppearanceMode.Dark)));
            await batch.SaveCommand.ExecuteAsync(null); Assert.True(batch.IsSaved);
            var saved = preferences.Load(); Assert.True(saved.RememberProductMappings); Assert.Equal("en-US", saved.CultureName); Assert.Equal(AppearanceMode.Dark, saved.Appearance);
            var appearance = new MainWindowViewModel(preferences); appearance.SelectedLanguageIndex = 0; appearance.SelectedAppearanceIndex = (int)AppearanceMode.Light;
            Assert.True(preferences.Load().RememberProductMappings);
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic next review", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            await store.SaveProposalAsync(proposal with { Id = Guid.NewGuid(), QuotationId = quote.Id }, [2], TestContext.Current.CancellationToken);
            await model.LoadAsync(quote.Id); await model.IdentifyPendingCommand.ExecuteAsync(null);
            var next = model.CreateProductReview(); Assert.True(next.RememberMappings); next.RememberMappings = false;
            await next.SaveCommand.ExecuteAsync(null); Assert.True(next.IsSaved); Assert.False(preferences.Load().RememberProductMappings);
            appearance.SelectedAppearanceIndex = (int)AppearanceMode.Dark; Assert.False(preferences.Load().RememberProductMappings);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PreferenceWriteFailure_DoesNotUndoOrRetryAnAlreadySavedBatch()
    {
        await session.Session.Dispatch(async () =>
        {
            string path = Path.Combine(_directory, "unwritable-preferences"); Directory.CreateDirectory(path);
            var (store, model, proposal, _) = await BatchWorkspaceAsync(preferences: new(path));
            var batch = model.CreateProductReview(); batch.RememberMappings = true;
            await batch.SaveCommand.ExecuteAsync(null); Assert.True(batch.IsSaved); Assert.False(batch.CanSave);
            Assert.Contains(model.Strings["PreferenceError"], batch.Status);
            await batch.SaveCommand.ExecuteAsync(null);
            Assert.Equal(1, (await store.GetAnalysisInputAsync(proposal.QuotationId!.Value)).State.Version);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IndividualConfirmation_StaysOpenForInvalidConversionAndClosesAfterSuccessfulSave()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, model, proposal, preferences) = await BatchWorkspaceAsync();
            var window = new QuotationItemEditorWindow { DataContext = model }; window.Show();
            try
            {
                var confirm = window.FindControl<Button>("ConfirmProductConversion")!;
                model.RememberMapping = true; model.ConversionFactor = 0;
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (model.IsBusy) await Task.Yield(); Dispatcher.UIThread.RunJobs();
                Assert.True(window.IsVisible); Assert.False(model.LastProductConfirmationSucceeded);
                Assert.False(preferences.Load().RememberProductMappings);
                model.ConversionFactor = 1; confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (model.IsBusy) await Task.Yield(); Dispatcher.UIThread.RunJobs();
                Assert.False(window.IsVisible); Assert.True(model.LastProductConfirmationSucceeded); Assert.True(preferences.Load().RememberProductMappings);
                Assert.Single((await store.GetAnalysisInputAsync(proposal.QuotationId!.Value)).State.Links);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1280)]
    public async Task ConversionGrid_AlignsHeadersAndEditableColumnsAtBothWindowSizes(int width)
    {
        await session.Session.Dispatch(async () =>
        {
            var (_, model, _, _) = await BatchWorkspaceAsync();
            var batch = model.CreateProductReview();
            var window = new ProductReviewWindow { DataContext = batch, Width = width, Height = 560 }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                double factorX = window.FindControl<HelpLabel>("BatchFactorHeader")!.TranslatePoint(default, window)!.Value.X;
                double quantityX = window.FindControl<TextBlock>("BatchQuantityHeader")!.TranslatePoint(default, window)!.Value.X;
                var fields = window.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
                foreach (var field in fields.Where(field => field.Name == "ReviewConversionFactor")) Assert.InRange(Math.Abs(field.TranslatePoint(default, window)!.Value.X - factorX), 0, 1);
                foreach (var field in fields.Where(field => field.Name == "ReviewSystemQuantity")) Assert.InRange(Math.Abs(field.TranslatePoint(default, window)!.Value.X - quantityX), 0, 1);
                Assert.Equal(4, fields.Count(field => field.IsEffectivelyVisible));
                Assert.True(window.FindControl<Button>("ConfirmProductReview")!.IsEffectivelyVisible);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConversionGrid_ProductSearchReturnsToTheSameRowWithoutSaving()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, model, proposal, _) = await BatchWorkspaceAsync();
            var batch = model.CreateProductReview(); var window = new ProductReviewWindow { DataContext = batch }; window.Show();
            try
            {
                var row = batch.Rows[1];
                var searchButton = window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.DataContext, row) && Equals(button.Content, "…"));
                searchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                var search = Assert.IsType<ProductSearchWindow>(Assert.Single(window.OwnedWindows));
                var lookup = Assert.IsType<ProductSearchViewModel>(search.DataContext);
                Assert.Equal(0, lookup.ModeIndex); Assert.Equal(row.Item.ProductCode, lookup.SearchText);
                await lookup.SearchCommand.ExecuteAsync(null); lookup.SelectedProduct = Assert.Single(lookup.Results);
                search.FindControl<Button>("SelectProductButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Yield(); Dispatcher.UIThread.RunJobs();
                Assert.Empty(window.OwnedWindows); Assert.Equal(row.Item.ProductCode, row.SelectedCandidate!.Code);
                Assert.Equal(2, row.Candidates.Count); Assert.False(row.HasValidation);
                Assert.Empty((await store.GetAnalysisInputAsync(proposal.QuotationId!.Value)).State.Links);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}

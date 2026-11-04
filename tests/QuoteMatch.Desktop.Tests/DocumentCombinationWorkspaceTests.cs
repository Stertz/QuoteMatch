using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Testing;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Theory]
    [InlineData(1, "Synthetic seller", false)]
    [InlineData(2, "Other seller", false)]
    [InlineData(2, "Synthetic seller", true)]
    public async Task CreateOrder_AsksAboutQuantitiesOnlyForMultipleDocumentsFromTheSameSupplier(int count, string secondSupplier, bool asks)
    {
        await session.Session.Dispatch(async () =>
        {
            CombinationDocument[] documents = count == 1 ? [new("Synthetic seller", 10, 5)] : [new("Synthetic seller", 10, 5), new(secondSupplier, 20, 5)];
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, documents);
            var model = new PurchaseSynchronizationViewModel(new Localizer(), fixture.Store, fixture, fixture, fixture, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId); model.Branch = "branch";
            var window = new PurchaseSynchronizationWindow { DataContext = model }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(); Assert.Equal(asks, model.ShowDocumentChoices);
                Assert.Equal(asks, window.FindControl<Border>("CreateDocumentChoices")!.IsEffectivelyVisible);
                Assert.Equal(!asks, model.CanPreview);
                if (asks)
                {
                    var choice = Assert.Single(model.DocumentChoices); Assert.Equal(-1, choice.QuantityChoiceIndex); Assert.False(choice.NeedsPriceChoice);
                    window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "DocumentQuantityChoice").SelectedIndex = 1;
                    Dispatcher.UIThread.RunJobs(); Assert.True(model.CanPreview); Assert.False(choice.NeedsPriceChoice);
                }
                await model.PreviewCommand.ExecuteAsync(null); model.Differences.Single().IsSelected = true;
                await model.ReviewSelectionCommand.ExecuteAsync(null);
                Assert.Equal(asks ? 30m : 10m, Assert.Single(model.Selection!.Lines).Quantity);
                Assert.Equal(0, fixture.WriteCount);
                model.KindIndex = 1; Dispatcher.UIThread.RunJobs();
                Assert.False(model.ShowDocumentChoices); Assert.Empty(model.DocumentChoices);
                Assert.False(window.FindControl<Border>("CreateDocumentChoices")!.IsEffectivelyVisible);
                Assert.False(model.HasPreview); Assert.False(model.CanConfirm);
                model.SelectedTarget = new((await fixture.ReadAsync("existing-order", TestContext.Current.CancellationToken)).Header, "Synthetic target");
                await model.PreviewCommand.ExecuteAsync(null); Assert.True(model.HasPreview);
                Assert.Empty(model.DocumentChoices);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(0, 240)]
    [InlineData(1, 210)]
    [InlineData(2, 210)]
    public async Task CreateOrder_AsksAboutPricesOnlyWhenNeeded_AndChangingADecisionClearsApproval(int priceChoice, int total)
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken,
                new("Synthetic seller", 10, 5), new("Synthetic seller", 20, 8));
            var model = new PurchaseSynchronizationViewModel(new Localizer(), fixture.Store, fixture, fixture, fixture, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId); model.Branch = "branch";
            var window = new PurchaseSynchronizationWindow { DataContext = model }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(); var choice = Assert.Single(model.DocumentChoices); Assert.False(choice.NeedsPriceChoice);
                window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "DocumentQuantityChoice").SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs(); Assert.True(choice.NeedsPriceChoice); Assert.False(model.CanPreview);
                window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "DocumentPriceChoice").SelectedIndex = priceChoice;
                Dispatcher.UIThread.RunJobs(); Assert.True(model.CanPreview);
                await model.PreviewCommand.ExecuteAsync(null); model.Differences.Single().IsSelected = true;
                await model.ReviewSelectionCommand.ExecuteAsync(null);
                Assert.Equal(total, model.Selection!.ItemSubtotal); Assert.Equal(priceChoice == 2 ? 2 : 1, model.Selection.Lines.Count);
                Assert.Equal(30m, model.Selection.Lines.Sum(line => line.Quantity));
                Assert.True(model.CanConfirm);
                choice.QuantityChoiceIndex = 0; Dispatcher.UIThread.RunJobs();
                Assert.False(choice.NeedsPriceChoice); Assert.False(model.HasPreview); Assert.False(model.HasSelection); Assert.False(model.CanConfirm);
                Assert.Equal(-1, choice.PriceChoiceIndex);
                await model.PreviewCommand.ExecuteAsync(null); model.Differences.Single().IsSelected = true;
                await model.ReviewSelectionCommand.ExecuteAsync(null); Assert.Equal(10m, model.Selection!.Lines.Single().Quantity);
                choice.QuantityChoiceIndex = 1; choice.PriceChoiceIndex = priceChoice;
                await model.PreviewCommand.ExecuteAsync(null); model.Differences.Single().IsSelected = true;
                await model.ReviewSelectionCommand.ExecuteAsync(null);
                await model.ConfirmWriteCommand.ExecuteAsync(null);
                Assert.Equal(1, fixture.WriteCount); Assert.Equal(1, fixture.ReserveCount);
                var operation = Assert.Single(await fixture.Store.GetSynchronizationOperationsAsync(fixture.Proposal.QuotationId, TestContext.Current.CancellationToken));
                Assert.Equal(OrderOperationState.Committed, operation.State);
                Assert.Equal((DocumentPriceChoice)(priceChoice + 1), operation.Plan.Selection.Preview.CreationDocuments!.Choices.Single().Prices);
                Assert.Equal(total, operation.Plan.Selection.ItemSubtotal);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}

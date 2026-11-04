using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    private async Task<(SqliteImportStore Store, Guid QuotationId)> PurchaseStoreAsync(int count = 1)
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync();
        Quotation quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic purchase", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
        var proposals = new[] { AnalysisProposal(quote.Id, "A", 2, 200, "BOB", count), AnalysisProposal(quote.Id, "B", 250, 1.5m, count: count), AnalysisProposal(quote.Id, "C", 100, 2, count: count) };
        foreach (var proposal in proposals) await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
        var input = await store.GetAnalysisInputAsync(quote.Id);
        await store.SaveAnalysisAsync(input.State with
        {
            Links = proposals.SelectMany((proposal, index) => proposal.Review.Select((item, row) => new ItemLink(proposal.Id, item.CaptureId,
                $"manual:{row}", $"Synthetic wire {row + 1:000}", "MT", index == 0 ? 100 : 1, null, DateTime.UtcNow))).ToArray(),
            Demands = Enumerable.Range(0, count).Select(row => new GroupDemand($"manual:{row}", "MT", 200)).ToArray()
        });
        return (store, quote.Id);
    }

    [Fact]
    public async Task PurchaseWorkspace_ChoosesWholeLotsNavigatesFiftyItemsAndPersistsOffline()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotationId) = await PurchaseStoreAsync(50);
            using WorkspaceViewModel workspace = new(CreateStore(), store); await workspace.InitializeAsync();
            workspace.SelectedQuotation = workspace.Quotations.Single(); await workspace.OpenQuotationCommand.ExecuteAsync(null);
            WorkspaceWindow window = new() { DataContext = workspace, InitializeOnOpen = false, Width = 1100, Height = 740 }; window.Show();
            try
            {
                window.FindControl<TabControl>("QuotationTabs")!.SelectedIndex = 2;
                await workspace.RefreshPurchaseAsync(); Dispatcher.UIThread.RunJobs();
                var model = workspace.Purchase;
                var view = window.GetLogicalDescendants().OfType<PurchasePreparationView>().First(view => view.IsEffectivelyVisible);
                Assert.Equal(50, model.Groups.Count); Assert.Equal("R$ 0,00", model.ItemSubtotal);
                var list = view.FindControl<ListBox>("PurchaseGroupList")!;
                list.SelectedItem = model.Groups[^1]; list.ScrollIntoView(list.SelectedItem); Dispatcher.UIThread.RunJobs();
                Assert.Equal("Synthetic wire 050", model.SelectedGroup!.Name);
                Assert.True(list.Bounds.Height >= 200);
                model.SelectedOffer = model.Offers.Single(offer => offer.Supplier == "C"); Assert.False(model.CanChoose);
                await model.ChooseOfferCommand.ExecuteAsync(null); Assert.Null((await store.GetPurchaseInputAsync(quotationId)).Preparation);
                model.SelectedOffer = model.Offers.Single(offer => offer.Supplier == "B"); Assert.True(model.CanChoose);
                Assert.Contains("50 MT", model.SelectedOffer.Detail);
                await model.ChooseOfferCommand.ExecuteAsync(null); Assert.Equal("R$ 375,00", model.ItemSubtotal);
                Assert.Equal("B", model.Offers[0].Supplier); Assert.True(model.Offers[0].IsChosen); Assert.True(model.SelectedOffer!.IsChosen);
                Assert.Equal("B", model.SelectedGroup!.Choice); Assert.True(model.CanRemove);
                model.ShowSummaryCommand.Execute(null); Assert.True(model.IsShowingSummary); Assert.Single(model.Suppliers);
                Assert.Equal("R$ 375,00", model.Suppliers[0].Subtotal);
                model.ShowOffersCommand.Execute(null); Assert.False(model.IsShowingSummary);
                model.SearchText = "050"; Assert.Single(model.Groups); Assert.Equal("R$ 375,00", model.ItemSubtotal);
                model.SearchText = "A-050"; Assert.Single(model.Groups); Assert.Equal("Synthetic wire 050", model.SelectedGroup!.Name);
                model.SearchText = "";
                await model.UseSuggestedCommand.ExecuteAsync(null);
                Assert.Equal("R$ 18.750,00", model.ItemSubtotal); Assert.All(model.Groups, row => Assert.Equal("B", row.Choice));
                model.SelectedOffer = model.Offers.Single(offer => offer.Supplier == "A");
                await model.ChooseOfferCommand.ExecuteAsync(null); Assert.Equal("R$ 18.775,00", model.ItemSubtotal);
                await model.RemoveChoiceCommand.ExecuteAsync(null); Assert.Equal("R$ 18.375,00", model.ItemSubtotal);
                await model.RefreshCommand.ExecuteAsync(null); Assert.Equal("R$ 18.375,00", model.ItemSubtotal);
                Assert.Equal(49, (await new SqliteImportStore(_directory).GetPurchaseInputAsync(quotationId)).Preparation!.Lines.Count);
                model.Clear(); Assert.Empty(model.Groups); Assert.Empty(model.Suppliers); Assert.False(model.CanEdit);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PurchaseWorkspace_RetainsEligibleChoicesInANewVersionAndProtectsFinalizedQuotation()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotationId) = await PurchaseStoreAsync(2);
            PurchasePreparationViewModel model = new(new(), store); await model.LoadAsync(quotationId);
            await model.UseSuggestedCommand.ExecuteAsync(null); Assert.Equal("R$ 750,00", model.ItemSubtotal);
            var input = await store.GetAnalysisInputAsync(quotationId);
            await store.SaveAnalysisAsync(input.State with { Demands = [new("manual:0", "MT", 250), new("manual:1", "MT", 200)] });
            await model.LoadAsync(quotationId); Assert.False(model.IsOutdated);
            Assert.Equal("R$ 750,00", model.ItemSubtotal); Assert.All(model.Groups, row => Assert.Equal("B", row.Choice));
            Assert.Equal(model.Strings["PurchaseChooseOffer"], model.ChooseLabel); Assert.True(model.CanRemove);
            model.SelectedOffer = model.Offers.Single(offer => offer.Supplier == "A"); Assert.False(model.CanChoose);
            await model.ChooseOfferCommand.ExecuteAsync(null); Assert.Equal(2, (await store.GetPurchaseInputAsync(quotationId)).Preparation!.Version);
            await model.RemoveChoiceCommand.ExecuteAsync(null); Assert.False(model.IsOutdated); Assert.Equal("R$ 375,00", model.ItemSubtotal);
            var saved = (await store.GetPurchaseInputAsync(quotationId)).Preparation!;
            Assert.Equal(3, saved.Version); Assert.Equal(2, saved.AnalysisVersion); Assert.Equal(1, saved.PendingGroupCount);
            input = await store.GetAnalysisInputAsync(quotationId);
            await store.SaveQuotationAsync(input.Quotation with { State = QuotationState.Finalized });
            await model.LoadAsync(quotationId); Assert.False(model.CanEdit); Assert.False(model.CanChoose); Assert.False(model.CanSuggest);
            await model.UseSuggestedCommand.ExecuteAsync(null); Assert.Equal(model.Strings["PurchaseErrorReadOnly"], model.Status);
            Assert.Equal(3, (await store.GetPurchaseInputAsync(quotationId)).Preparation!.Version);
            model.Strings.SetCulture("en-US"); model.RefreshLabels(); Assert.Contains("Saved version 3", model.SavedReference);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SavedConnectionButton_UsesPersistedProfileAndReportsSafeDiagnosticStage()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); using WorkspaceViewModel workspace = new(CreateStore(), store); await workspace.InitializeAsync();
            var model = workspace.Settings;
            await model.TestConnectionCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["ConnectionFailed"], model.Status); Assert.Contains("Configuração", model.DiagnosticDetail);
            await model.TestSavedConnectionCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["ErpErrorNotConfigured"], model.Status); Assert.Empty(model.DiagnosticDetail);
            await store.SaveErpConfigurationAsync(new() { Server = "synthetic-host", Database = "synthetic-db", Authentication = Domain.Integration.ErpAuthentication.SqlLogin });
            await model.TestSavedConnectionCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["ErpErrorCredentialsUnavailable"], model.Status);
            Assert.Empty(model.Server); Assert.Empty(model.Database);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task MissingDesiredQuantity_AllowsExplicitChoicesAndExplainsTheOptionalCheck()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotation) = await PurchaseStoreAsync();
            var input = await store.GetAnalysisInputAsync(quotation);
            await store.SaveAnalysisAsync(input.State with { Demands = [] });
            PurchasePreparationViewModel model = new(new(), store); await model.LoadAsync(quotation);
            PurchasePreparationView view = new() { DataContext = model }; Window window = new() { Content = view, Width = 1100, Height = 800 }; window.Show();
            try
            {
                var choose = view.FindControl<Button>("ChoosePurchaseOffer")!;
                Assert.True(choose.IsEffectivelyEnabled);
                Assert.Empty(model.ChooseUnavailableReason);
                Assert.Contains(model.Strings["PurchaseDemandOptional"], model.SelectedOffer!.Detail);
                Assert.True(ToolTip.GetShowOnDisabled(choose));
                model.ShowSummaryCommand.Execute(null); window.UpdateLayout();
                var packages = view.GetVisualDescendants().OfType<Button>().Where(button => button.DataContext is PurchasePackageRow).ToArray();
                Assert.NotEmpty(packages);
                foreach (var button in packages)
                {
                    Assert.True(button.IsEffectivelyEnabled);
                    Assert.Empty(((PurchasePackageRow)button.DataContext!).UnavailableReason);
                    Assert.True(ToolTip.GetShowOnDisabled(button));
                }
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OrderChecking_ExplainsMissingPurchasesAndLetsTheOperatorSearchErpImmediately()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, _) = await PurchaseStoreAsync();
            var model = Model(store, new Orders());
            await model.RefreshReferencesCommand.ExecuteAsync(null);
            Assert.Empty(model.References);
            Assert.Equal(model.Strings["OrdersNoPreparedPurchases"], model.Status);
            Assert.Single(model.ReferenceCandidates);
            Assert.Equal(model.Strings["OrdersReferenceNeedsPreparation"], model.ReferenceCandidates[0].Detail);
            PurchaseOrdersView view = new() { DataContext = model }; Window window = new() { Content = view, Width = 1100, Height = 800 }; window.Show();
            try
            {
                var search = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, model.Strings["OrdersSearch"]));
                Assert.True(search.IsEffectivelyVisible); Assert.True(search.IsEffectivelyEnabled);
                var reconcile = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, model.Strings["OrdersReconcile"]));
                Assert.False(reconcile.IsEffectivelyVisible);
                await model.SearchCommand.ExecuteAsync(null);
                Assert.Equal(2, model.SearchResults.Count);
                Assert.Equal(model.Strings.Format("OrdersSearchCount", 2), model.Status);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PurchaseBlockReasons_UpdateForTheSelectedOfferAndFinalizedQuotation()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotation) = await PurchaseStoreAsync();
            PurchasePreparationViewModel model = new(new(), store); await model.LoadAsync(quotation);
            model.SelectedOffer = model.Offers.Single(row => row.Supplier == "C");
            Assert.Equal(model.Strings["PurchaseBlockedInsufficientQuantity"], model.ChooseUnavailableReason);
            model.SelectedOffer = model.Offers.Single(row => row.Supplier == "A");
            Assert.True(model.CanChoose); Assert.Empty(model.ChooseUnavailableReason);
            var input = await store.GetPurchaseInputAsync(quotation);
            await store.SaveQuotationAsync(input.Analysis.Quotation with { State = Domain.Quotations.QuotationState.Finalized });
            await model.LoadAsync(quotation);
            Assert.False(model.CanChoose); Assert.Equal(model.Strings["PurchaseBlockedFinalized"], model.ChooseUnavailableReason);
            Assert.All(model.Packages, row => Assert.Equal(model.Strings["PurchaseBlockedFinalized"], row.UnavailableReason));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PurchaseReferenceSearch_ListsUnpreparedQuotationsAndRoutesToPreparation()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, erp) = await SeedAsync();
            var draft = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic cotação pending", "", Now, Now, Domain.Quotations.QuotationState.Draft, 0));
            var orders = Model(store, erp); await orders.RefreshReferencesCommand.ExecuteAsync(null);
            Assert.Single(orders.References); Assert.Equal(2, orders.ReferenceCandidates.Count);
            PurchaseReferenceSearchViewModel search = new(orders.Strings, orders.ReferenceCandidates.ToArray());
            search.SearchText = "COTACAO"; var pending = Assert.Single(search.Results);
            Assert.Equal(draft.Id, pending.QuotationId); Assert.False(search.CanSelect); Assert.True(search.CanPrepare);
            search.SearchText = "supplier"; var ready = Assert.Single(search.Results); Assert.True(ready.CanCompare); Assert.True(search.CanSelect);
            search.SearchText = "COTACAO";
            Window owner = new(); owner.Show();
            try
            {
                PurchaseReferenceSearchWindow window = new() { DataContext = search };
                var response = window.ShowDialog<PurchaseReferenceSearchResult?>(owner);
                Assert.False(window.FindControl<Button>("CompareReferencePurchase")!.IsEnabled);
                window.FindControl<Button>("PrepareReferencePurchase")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var result = await response; Assert.True(result!.PreparePurchase); Assert.Equal(draft.Id, result.Candidate.QuotationId);
                using WorkspaceViewModel workspace = new(CreateStore(), store); await workspace.InitializeAsync();
                await workspace.OpenPurchasePreparationAsync(result.Candidate.QuotationId);
                Assert.True(workspace.IsQuotationOpen); Assert.Equal(draft.Id, workspace.CurrentQuotation!.Id); Assert.Equal(2, workspace.QuotationTabIndex);
                Assert.False(workspace.Purchase.CanChoose);
            }
            finally { owner.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnidentifiedPurchase_IsListedWithAnActionableReasonInsteadOfDisappearing()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotation) = await PurchaseStoreAsync();
            PurchasePreparationViewModel purchase = new(new(), store); await purchase.LoadAsync(quotation);
            await purchase.UseSuggestedCommand.ExecuteAsync(null);
            var model = Model(store, new Orders()); await model.RefreshReferencesCommand.ExecuteAsync(null);
            Assert.Empty(model.References); var candidate = Assert.Single(model.ReferenceCandidates);
            Assert.Equal(quotation, candidate.QuotationId); Assert.False(candidate.CanCompare);
            Assert.Equal(model.Strings["OrdersReferenceNeedsErp"], candidate.Detail);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RefreshingReferenceSearch_PreservesTheOpenComparisonAndUnconfirmedChanges()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, erp) = await SeedAsync(); var model = Model(store, erp); await LoadAsync(model);
            await model.SearchCommand.ExecuteAsync(null); await LinkAsync(model, "order-a");
            model.ChangePrice = true; model.UnitPrice = 190; model.AddChangeCommand.Execute(null);
            await model.RefreshReferencesCommand.ExecuteAsync(null);
            Assert.True(model.HasReference); Assert.Single(model.Orders); Assert.Single(model.Changes);
            Assert.Equal(190, model.Changes[0].Change.UnitPrice); Assert.Equal(400, model.Comparison[0].Comparison.RemainingQuantity);
            Assert.Equal(2, model.SearchResults.Count);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}

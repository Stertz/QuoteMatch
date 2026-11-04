using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Testing;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task CreatedOrder_ShowsItsSavedNumberCodeAndBranchAfterCreationAndOfflineReopening()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic supplier", 10, 5));
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, fixture, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId); model.Branch = "branch";
            var window = new PurchaseSynchronizationWindow { DataContext = model }; window.Show();
            try
            {
                Assert.False(window.FindControl<Border>("CreatedPurchaseOrders")!.IsVisible);
                await model.PrepareOrderPreviewAsync(); Assert.Empty(model.CreatedOrders);
                await model.ConfirmWriteCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
                var created = Assert.Single(model.CreatedOrders);
                Assert.Equal("43", created.Number); Assert.Equal("created-order", created.Code); Assert.Equal("branch", created.Branch);
                Assert.Equal(model.Strings.Format("SyncCreatedOrderDetails", "43", "created-order", "branch"), created.Label);
                Assert.True(window.FindControl<Border>("CreatedPurchaseOrders")!.IsVisible);
                Assert.False(model.HasSelection); Assert.False(model.CanConfirm);
                var reopened = new PurchaseSynchronizationViewModel(new(), new SqliteImportStore(fixture.DirectoryPath), fixture, fixture, operations: fixture.Store);
                await reopened.LoadAsync(fixture.Proposal.QuotationId);
                Assert.Equal(created, Assert.Single(reopened.CreatedOrders)); Assert.Equal("branch", reopened.Branch);
                Assert.Equal(1, fixture.WriteCount); Assert.Equal(1, fixture.ReserveCount);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UncertainCreation_ShowsTheCreatedOrderOnlyAfterSuccessfulReconciliationWithoutReplaying()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic supplier", 10, 5));
            fixture.LoseAcknowledgment = true;
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, fixture, fixture.Store);
            await model.LoadAsync(fixture.Proposal.QuotationId); model.Branch = "branch";
            await model.PrepareOrderPreviewAsync(); await model.ConfirmWriteCommand.ExecuteAsync(null);
            Assert.True(model.HasPendingOperation); Assert.Empty(model.CreatedOrders);
            await model.ReconcileCommand.ExecuteAsync(null);
            Assert.False(model.HasPendingOperation); Assert.Equal("43", Assert.Single(model.CreatedOrders).Number);
            Assert.Equal(1, fixture.WriteCount); Assert.Equal(1, fixture.ReserveCount);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BranchPreference_RemembersTheLastValidatedBranchAcrossQuotationsAndKeepsOtherPreferences()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var first = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic supplier", 10, 5));
            await using var second = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic supplier", 10, 5));
            var preferences = CreateStore();
            var original = new UserPreferences("en-US", AppearanceMode.Dark, true, true, false);
            Assert.True(preferences.TrySave(original));
            var model = new PurchaseSynchronizationViewModel(new(), first.Store, first, first, first, first.Store, preferences);
            await model.LoadAsync(first.Proposal.QuotationId); Assert.Empty(model.Branch);
            model.Branch = " 07 "; await model.PrepareOrderPreviewAsync(); Assert.True(model.CanConfirm, model.Status);
            Assert.Equal(original with { LastPurchaseBranch = "07" }, preferences.Load());
            var reopened = new PurchaseSynchronizationViewModel(new(), second.Store, second, second, second, second.Store, preferences);
            await reopened.LoadAsync(second.Proposal.QuotationId); Assert.Equal("07", reopened.Branch);
            reopened.Branch = "99";
            await reopened.LoadAsync(second.Proposal.QuotationId); Assert.Equal("07", reopened.Branch);
            var appearance = new MainWindowViewModel(preferences);
            appearance.SelectedLanguageIndex = 0; appearance.SelectedAppearanceIndex = (int)AppearanceMode.Light;
            Assert.Equal("07", preferences.Load().LastPurchaseBranch);
            Assert.Equal(0, first.WriteCount); Assert.Equal(0, second.WriteCount);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BranchPreference_SaveFailureKeepsTheValidPreviewAndReportsThePreferenceFailure()
    {
        await session.Session.Dispatch(async () =>
        {
            await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, new CombinationDocument("Synthetic supplier", 10, 5));
            string path = Path.Combine(_directory, "preferences.json"); Directory.CreateDirectory(path);
            var model = new PurchaseSynchronizationViewModel(new(), fixture.Store, fixture, fixture, fixture, fixture.Store, new(path));
            await model.LoadAsync(fixture.Proposal.QuotationId); model.Branch = "branch";
            await model.PrepareOrderPreviewAsync();
            Assert.True(model.CanConfirm); Assert.Contains(model.Strings["PreferenceError"], model.Status);
            Assert.Equal(0, fixture.WriteCount); Assert.Equal(0, fixture.ReserveCount);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SavedSupplierAndItemChoices_SurviveAnAnalysisRevisionAndReopeningWithoutReplacingTheSelectedLots()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, id) = await PurchaseStoreAsync(2);
            var model = new PurchasePreparationViewModel(new(), store); await model.LoadAsync(id);
            await model.ChooseSupplierPackageCommand.ExecuteAsync(model.Packages.Single(package => package.Name == "A"));
            model.SelectedOffer = model.Offers.Single(offer => offer.Supplier == "B"); await model.ChooseOfferCommand.ExecuteAsync(null);
            var saved = (await store.GetPurchaseInputAsync(id)).Preparation!;
            async Task<string> HistoricalSnapshot()
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "imports.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT DataJson FROM PurchasePreparations WHERE QuotationId = $quotation AND Version = $version";
                command.Parameters.AddWithValue("$quotation", id); command.Parameters.AddWithValue("$version", saved.Version);
                return Assert.IsType<string>(await command.ExecuteScalarAsync());
            }
            string historical = await HistoricalSnapshot();
            Assert.Equal(2, saved.Lines.Count); Assert.Contains(saved.Lines, line => line.Offer.SupplierName == "A");
            Assert.Contains(saved.Lines, line => line.Offer.SupplierName == "B");
            var analysis = await store.GetAnalysisInputAsync(id); await store.SaveAnalysisAsync(analysis.State);
            var reopened = new PurchasePreparationViewModel(new(), new SqliteImportStore(_directory)); await reopened.LoadAsync(id);
            Assert.False(reopened.IsOutdated);
            Assert.All(reopened.Groups, group => Assert.NotEqual(reopened.Strings["PurchaseUnselected"], group.Choice));
            var current = (await store.GetPurchaseInputAsync(id)).Preparation!;
            Assert.Equal(saved.Version + 1, current.Version); Assert.Equal(analysis.State.Version + 1, current.AnalysisVersion);
            Assert.Equal(saved.Lines.Select(line => (line.Offer.ProposalId, line.Offer.Item.CaptureId)), current.Lines.Select(line => (line.Offer.ProposalId, line.Offer.Item.CaptureId)));
            Assert.Equal(historical, await HistoricalSnapshot());
            await reopened.LoadAsync(id); Assert.Equal(current.Version, (await store.GetPurchaseInputAsync(id)).Preparation!.Version);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SavedChoices_KeepValidLotsAndLeaveAnInsufficientChosenLotPendingWithoutSelectingAnotherSupplier()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, id) = await PurchaseStoreAsync(2);
            var model = new PurchasePreparationViewModel(new(), store); await model.LoadAsync(id);
            await model.ChooseSupplierPackageCommand.ExecuteAsync(model.Packages.Single(package => package.Name == "A"));
            var analysis = await store.GetAnalysisInputAsync(id);
            await store.SaveAnalysisAsync(analysis.State with { Demands = [new("manual:0", "MT", 225), new("manual:1", "MT", 200)] });
            await model.LoadAsync(id);
            Assert.False(model.IsOutdated);
            var current = (await store.GetPurchaseInputAsync(id)).Preparation!;
            Assert.Equal(2, current.Version); Assert.Equal(1, current.PendingGroupCount);
            Assert.Equal("A", Assert.Single(current.Lines).Offer.SupplierName);
            Assert.Equal("manual:1", current.Lines[0].GroupKey);
            Assert.Equal(model.Strings["PurchaseUnselected"], model.Groups.Single(group => group.Group.Key == "manual:0").Choice);
            Assert.Contains(model.Offers, offer => offer.Supplier == "B" && offer.IsEligible);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SingleSupplier_SelectsAndPersistsEligibleItemsOnceAndPreservesAnExplicitRemoval()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, id) = await UnpreparedPurchaseAsync(new("Synthetic supplier", 10, 5, "P1"), new("Synthetic supplier", 20, 8, "P2"));
            var model = new PurchasePreparationViewModel(new(), store); await model.LoadAsync(id);
            var saved = (await store.GetPurchaseInputAsync(id)).Preparation!;
            Assert.Equal(1, saved.Version); Assert.Equal(2, saved.Lines.Count); Assert.Equal(0, saved.PendingGroupCount);
            Assert.Equal("R$ 210,00", model.ItemSubtotal); Assert.All(model.Groups, group => Assert.Equal("Synthetic supplier", group.Choice));
            await model.LoadAsync(id); Assert.Equal(1, (await store.GetPurchaseInputAsync(id)).Preparation!.Version);
            await model.RemoveChoiceCommand.ExecuteAsync(null);
            var reopened = new PurchasePreparationViewModel(new(), new SqliteImportStore(_directory)); await reopened.LoadAsync(id);
            saved = (await store.GetPurchaseInputAsync(id)).Preparation!;
            Assert.Equal(2, saved.Version); Assert.Single(saved.Lines); Assert.Equal(1, saved.PendingGroupCount);
            Assert.Single(reopened.Groups, group => group.Choice == reopened.Strings["PurchaseUnselected"]);
            reopened.SelectedGroup = reopened.Groups.Single(group => group.Choice != reopened.Strings["PurchaseUnselected"]);
            await reopened.RemoveChoiceCommand.ExecuteAsync(null);
            Assert.Empty((await store.GetPurchaseInputAsync(id)).Preparation!.Lines);
            var analysis = await store.GetAnalysisInputAsync(id); await store.SaveAnalysisAsync(analysis.State);
            await reopened.LoadAsync(id);
            Assert.Empty((await store.GetPurchaseInputAsync(id)).Preparation!.Lines);
            Assert.Equal(4, (await store.GetPurchaseInputAsync(id)).Preparation!.Version);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleSupplier_DoesNotGuessBetweenDuplicateOffersOrDifferentSuppliers(bool differentSuppliers)
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, id) = await UnpreparedPurchaseAsync(new("Synthetic A", 10, 5), new(differentSuppliers ? "Synthetic B" : "Synthetic A", 20, 8));
            var model = new PurchasePreparationViewModel(new(), store); await model.LoadAsync(id);
            Assert.Null((await store.GetPurchaseInputAsync(id)).Preparation);
            Assert.All(model.Groups, group => Assert.Equal(model.Strings["PurchaseUnselected"], group.Choice));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SingleSupplier_KeepsUnidentifiedItemsPendingAndDoesNotChangeFinalizedQuotations()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, id) = await UnpreparedPurchaseAsync(new("Synthetic supplier", 10, 5, "P1"), new("Synthetic supplier", 20, 8, "P2", Link: false));
            var model = new PurchasePreparationViewModel(new(), store); await model.LoadAsync(id);
            var saved = (await store.GetPurchaseInputAsync(id)).Preparation!;
            Assert.Equal("P1", Assert.Single(saved.Lines).Offer.Item.ProductCode); Assert.Equal(1, saved.PendingGroupCount);
            var input = await store.GetAnalysisInputAsync(id);
            await store.SaveQuotationAsync(input.Quotation with { State = QuotationState.Finalized });
            await model.LoadAsync(id); Assert.Equal(1, (await store.GetPurchaseInputAsync(id)).Preparation!.Version);
            var (otherStore, otherId) = await UnpreparedPurchaseAsync(new CombinationDocument("Synthetic supplier", 10, 5));
            var other = await otherStore.GetAnalysisInputAsync(otherId);
            await otherStore.SaveQuotationAsync(other.Quotation with { State = QuotationState.Finalized });
            await model.LoadAsync(otherId); Assert.Null((await otherStore.GetPurchaseInputAsync(otherId)).Preparation);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private async Task<(SqliteImportStore Store, Guid Id)> UnpreparedPurchaseAsync(params CombinationDocument[] documents)
    {
        await using var fixture = await DocumentCombinationFixture.CreateAsync(TestContext.Current.CancellationToken, documents);
        var source = fixture.Input.Analysis; var store = new SqliteImportStore(_directory); await store.InitializeAsync();
        await store.SaveQuotationAsync(source.Quotation with { Revision = 0 });
        foreach (var proposal in source.Proposals) await store.SaveProposalAsync(proposal, [1]);
        var input = await store.GetAnalysisInputAsync(source.Quotation.Id);
        await store.SaveAnalysisAsync(input.State with { Links = source.State.Links, Demands = source.State.Demands });
        return (store, source.Quotation.Id);
    }
}

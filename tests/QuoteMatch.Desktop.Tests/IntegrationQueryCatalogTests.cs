using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Desktop.Controls;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Infrastructure.Erp;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task SavingSettings_ClearsDiagnosticsFromAnEarlierConnectionTest()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var model = new IntegrationSettingsViewModel(new Localizer(), store, new ErpGateway(store, new ProtectedErpCredentials(_directory)));
            await model.LoadAsync(); await model.TestConnectionCommand.ExecuteAsync(null);
            Assert.False(string.IsNullOrWhiteSpace(model.DiagnosticDetail));
            await model.SaveCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["SettingsSaved"], model.Status); Assert.Empty(model.DiagnosticDetail);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WriteConnectionSettings_PersistExplicitConnectionReuseAndOperatorWithoutRequiringAnotherLogin()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            await store.SaveErpConfigurationAsync(new() { Authentication = ErpAuthentication.Integrated });
            IntegrationSettingsViewModel model = new(new Localizer(), store, new ErpGateway(store, new ProtectedErpCredentials(_directory)));
            await model.LoadAsync();
            Assert.False(model.UseReadConnectionForWrites); Assert.Null(model.WriteOperatorCode);
            ConnectionSettingsView view = new() { DataContext = model }; Window window = new() { Content = view }; window.Show();
            try
            {
                view.FindControl<CheckBox>("UseReadConnectionForWritesInput")!.IsChecked = true;
                view.FindControl<NumericUpDown>("WriteOperatorCodeInput")!.Value = 112;
                var writeOptions = view.FindControl<Expander>("OrderWriteConnectionOptions")!;
                Assert.False(writeOptions.IsExpanded); writeOptions.IsExpanded = true;
                var advanced = view.FindControl<Expander>("ConnectionAdvancedOptions")!;
                Assert.False(advanced.IsExpanded); advanced.IsExpanded = true;
                view.FindControl<CheckBox>("EncryptConnectionInput")!.IsChecked = false;
                view.FindControl<NumericUpDown>("ConnectionTimeoutInput")!.Value = 45;
                Dispatcher.UIThread.RunJobs();
                await model.SaveCommand.ExecuteAsync(null);
                SqliteImportStore restarted = new(_directory);
                var saved = await restarted.GetErpConfigurationAsync();
                Assert.True(saved.UseReadConnectionForWrites); Assert.Equal(112, saved.WriteOperatorCode);
                Assert.False(saved.Encrypt); Assert.Equal(45, saved.ConnectionTimeoutSeconds);
                Assert.Empty(saved.WriteUserName); Assert.Equal(ErpAuthentication.Integrated, saved.Authentication);
                IntegrationSettingsViewModel loaded = new(new Localizer(), restarted, new ErpGateway(restarted, new ProtectedErpCredentials(_directory)));
                await loaded.LoadAsync();
                Assert.True(loaded.UseReadConnectionForWrites); Assert.Equal(112, loaded.WriteOperatorCode);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QueryCatalog_ListsOnlyImplementedOperationsAndSavesEachUnderItsOriginalIdentity()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            await store.SaveErpConfigurationAsync(new()
            {
                Queries = new() { [ErpOperation.GetLastPurchase] = "SELECT @ProductCode AS LegacyProduct", [ErpOperation.GetPurchaseOrderItems] = "SELECT @OrderNumber AS LegacyOrder" }
            });
            IntegrationSettingsViewModel model = new(new Localizer(), store, new ErpGateway(store, new ProtectedErpCredentials(_directory)));
            await model.LoadAsync();
            Assert.Equal(["FindProduct", "GetEquivalentProducts", "FindProductById", "SearchProducts", "SearchPurchaseOrders", "ReadPurchaseOrder", "FindBuyingSupplier", "PreviewPurchaseFinancials", "ReservePurchaseSynchronization", "CreatePurchaseSynchronization", "UpdatePurchaseSynchronization", "ReconcilePurchaseSynchronization", "CreatePurchaseOrder", "UpdatePurchaseOrder", "ReconcilePurchaseOperation"], model.OperationOptions.Select(option => option.Operation.ToString()));
            QuerySettingsView view = new() { DataContext = model }; Window window = new() { Content = view }; window.Show();
            try
            {
                var selector = view.FindControl<ComboBox>("QueryOperationSelector")!;
                var expected = new[] { ErpOperation.FindProduct, ErpOperation.GetEquivalentProducts, ErpOperation.FindProductById, ErpOperation.SearchProducts,
                    ErpOperation.SearchPurchaseOrders, ErpOperation.ReadPurchaseOrder, ErpOperation.FindBuyingSupplier, ErpOperation.PreviewPurchaseFinancials,
                    ErpOperation.ReservePurchaseSynchronization, ErpOperation.CreatePurchaseSynchronization, ErpOperation.UpdatePurchaseSynchronization, ErpOperation.ReconcilePurchaseSynchronization,
                    ErpOperation.CreatePurchaseOrder, ErpOperation.UpdatePurchaseOrder, ErpOperation.ReconcilePurchaseOperation };
                for (int index = 0; index < expected.Length; index++)
                {
                    selector.SelectedIndex = index; Dispatcher.UIThread.RunJobs();
                    Assert.Equal(model.Strings["QueryContract" + expected[index]], model.QueryContract);
                    var option = model.OperationOptions[index];
                    Assert.Equal(expected[index], option.Operation);
                    Assert.Equal(model.Strings["QueryName" + expected[index]], option.Label);
                    Assert.EndsWith(expected[index].ToString(), option.Detail);
                    Assert.Equal(index < 4, view.FindControl<Expander>("ProductReadOptions")!.IsVisible);
                    model.QueryText = "SELECT " + index;
                }
                var help = Assert.Single(view.GetVisualDescendants().OfType<HelpLabel>());
                var helpButton = Assert.Single(help.GetVisualDescendants().OfType<Button>());
                Assert.True(helpButton.Focusable); helpButton.Flyout!.ShowAt(helpButton);
                Assert.Equal(model.QueryContract, Assert.IsType<TextBlock>(Assert.IsType<Flyout>(helpButton.Flyout).Content).Text);
                helpButton.Flyout.Hide();
                model.Strings.SetCulture("en-US"); model.RefreshLabels(); Dispatcher.UIThread.RunJobs();
                Assert.Equal("Check previous operation", model.OperationOptions[^1].Label);
                Assert.Equal("SELECT 14", model.QueryText);
                model.Strings.SetCulture("pt-BR"); model.RefreshLabels();
                model.AllowProductReadScripts = true;
                await model.SaveCommand.ExecuteAsync(null);
                var saved = await store.GetErpConfigurationAsync();
                Assert.Equal(ProductReadMode.Script, saved.ProductReadMode);
                for (int index = 0; index < expected.Length; index++) Assert.Equal("SELECT " + index, saved.Queries[expected[index]]);
                Assert.Equal("SELECT @ProductCode AS LegacyProduct", saved.Queries[ErpOperation.GetLastPurchase]);
                Assert.Equal("SELECT @OrderNumber AS LegacyOrder", saved.Queries[ErpOperation.GetPurchaseOrderItems]);
                await model.LoadAsync(); Assert.True(model.AllowProductReadScripts);
                for (int index = 0; index < expected.Length; index++) { model.OperationIndex = index; Assert.Equal("SELECT " + index, model.QueryText); }
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}

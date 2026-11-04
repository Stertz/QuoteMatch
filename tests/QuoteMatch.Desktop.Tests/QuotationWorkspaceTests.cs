using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using QuoteMatch.Desktop.Controls;
using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task Workspace_HomeToQuotationImportFinalizeReopenAndConfirmedDelete()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory);
            using WorkspaceViewModel model = new(CreateStore(), store);
            await model.InitializeAsync();
            WorkspaceWindow window = new() { DataContext = model, InitializeOnOpen = false };
            window.Show();
            try
            {
                Assert.True(model.IsHome); Assert.True(model.HasNoQuotations);
                window.FindControl<Button>("HomeNewQuotation")!.Command!.Execute(null);
                Assert.True(model.IsEditingDetails); Assert.False(model.CanImport);
                TextBox name = window.FindControl<TextBox>("QuotationNameEditor")!;
                name.Text = "November stock";
                Dispatcher.UIThread.RunJobs();
                await model.SaveQuotationCommand.ExecuteAsync(null);
                Assert.True(model.CanImport); Assert.Single(model.Quotations); Assert.Equal("November stock", model.CurrentQuotation!.Name);
                await model.EditQuotationCommand.ExecuteAsync(null); model.QuotationNotes = "Only blue variants";
                await model.SaveQuotationCommand.ExecuteAsync(null);
                Assert.Equal("Only blue variants", model.CurrentQuotation.Notes);
                model.Templates.NewTemplateCommand.Execute(null);
                model.Templates.Editor.Name = "Synthetic layout"; model.Templates.Editor.SupplierName = "Example supplier";
                await model.Templates.SaveTemplateCommand.ExecuteAsync(null);
                string path = Path.Combine(_directory, "synthetic.pdf");
                PdfDocumentBuilder builder = new(); var font = builder.AddStandard14Font(Standard14Font.Helvetica); var page = builder.AddPage(PageSize.A4);
                page.AddText("001 Blue wire 10 2,50 25,00", 12, new PdfPoint(40, 780), font);
                await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
                using ImportWorkspaceViewModel import = model.CreateImport(); await import.InitializeAsync(); await import.PreviewFileAsync(path);
                Assert.True(import.CanSave); await import.ConfirmCommand.ExecuteAsync(null);
                await model.RefreshAsync();
                Assert.Single(model.Proposals); Assert.Equal("R$ 25,00", model.Proposals[0].Total);
                model.RequestFinalizeCommand.Execute(null); Assert.True(model.HasConfirmation);
                Dispatcher.UIThread.RunJobs(); Assert.False(window.FindControl<Button>("ImportIntoQuotation")!.IsEffectivelyEnabled);
                await model.ConfirmActionCommand.ExecuteAsync(null);
                Assert.True(model.IsFinalized); Assert.False(model.CanImport);
                Assert.Throws<QuoteMatch.Domain.Quotations.QuotationException>(() => model.CreateImport());
                Dispatcher.UIThread.RunJobs();
                Assert.False(window.FindControl<Button>("ImportIntoQuotation")!.IsVisible);
                using ImportWorkspaceViewModel reader = model.CreateProposalReader(); await reader.InitializeAsync();
                reader.SelectedProposal = model.Proposals[0].Summary; await reader.OpenProposalCommand.ExecuteAsync(null);
                Assert.True(reader.IsConsulting); Assert.False(reader.ShowConfirmation); Assert.Single(reader.Items);
                await model.ReopenQuotationCommand.ExecuteAsync(null); Assert.True(model.CanImport);
                model.RequestDeleteCommand.Execute(null); model.CancelConfirmationCommand.Execute(null);
                Assert.Single(await store.GetQuotationsAsync(TestContext.Current.CancellationToken));
                model.RequestDeleteCommand.Execute(null); await model.ConfirmActionCommand.ExecuteAsync(null);
                Assert.True(model.IsQuotationList); Assert.True(model.HasNoQuotations);
                Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
                model.PageIndex = 2; Assert.True(model.IsTemplates);
                model.PageIndex = 3; Assert.True(model.IsSettings);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TemplatePage_HidesAdvancedControlsAndExplainsNumberPolicies()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory);
            using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            model.PageIndex = 2;
            WorkspaceWindow window = new() { DataContext = model, InitializeOnOpen = false }; window.Show();
            try
            {
                TemplatesView view = window.GetLogicalDescendants().OfType<TemplatesView>().Single();
                Expander advanced = view.FindControl<Expander>("AdvancedTemplateOptions")!;
                Assert.False(advanced.IsExpanded);
                advanced.IsExpanded = true; Dispatcher.UIThread.RunJobs();
                var labels = view.GetLogicalDescendants().OfType<HelpLabel>().ToArray();
                Assert.Contains(labels, label => label.Help == model.Strings["AbsoluteToleranceHelp"]);
                Assert.Contains(labels, label => label.Help == model.Strings["RelativeToleranceHelp"]);
                Assert.Contains(labels, label => label.Help == model.Strings["DocumentNumbersHelp"]);
                model.Templates.Editor.Load(new PdfTemplate { RemoveGroupingSpaces = false, RelativeTolerance = .001m });
                Assert.Equal(.1m, model.Templates.Editor.RelativeTolerancePercent);
                NumericUpDown relative = view.FindControl<NumericUpDown>("RelativeToleranceInput")!;
                Assert.Equal(",", relative.NumberFormat!.NumberDecimalSeparator);
                relative.Text = "0,25";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(.25m, relative.Value);
                HelpLabel absoluteHelp = labels.Single(label => label.Help == model.Strings["AbsoluteToleranceHelp"]);
                Button helpButton = absoluteHelp.GetLogicalDescendants().OfType<Button>().Single();
                helpButton.Flyout!.ShowAt(helpButton);
                Dispatcher.UIThread.RunJobs();
                Assert.True(helpButton.Flyout.IsOpen);
                Assert.Equal(model.Strings["AbsoluteToleranceHelp"], ((TextBlock)((Flyout)helpButton.Flyout).Content!).Text);
                helpButton.Flyout.Hide();
                model.Templates.Editor.RelativeTolerancePercent = .25m;
                Assert.Equal(.0025m, model.Templates.Editor.Build().RelativeTolerance);
                Assert.True(model.Templates.Editor.Build().RemoveGroupingSpaces);
                model.Templates.Editor.CultureIndex = 1; Assert.Equal("en-US", model.Templates.Editor.Build().CultureName);
                model.Appearance.SelectedLanguageIndex = 1;
                Assert.Equal("en-US", model.Templates.Strings.Culture.Name);
                Assert.Equal(".", relative.NumberFormat!.NumberDecimalSeparator);
                Assert.DoesNotContain(view.GetLogicalDescendants().OfType<CheckBox>(), check => Equals(check.Content, model.Strings["RemoveGroupingSpaces"]));
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Workspace_FilterDoesNotReplaceHomeRecentQuotationsWithAnEmptyState()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            model.NewQuotationCommand.Execute(null); model.QuotationName = "November stock"; await model.SaveQuotationCommand.ExecuteAsync(null);
            WorkspaceWindow window = new() { DataContext = model, InitializeOnOpen = false }; window.Show();
            try
            {
                model.BackToQuotationsCommand.Execute(null); model.SearchText = "does not exist"; Assert.Empty(model.Quotations);
                model.GoHomeCommand.Execute(null); Dispatcher.UIThread.RunJobs();
                Assert.Single(model.RecentQuotations);
                Assert.False(window.FindControl<TextBlock>("HomeEmptyNotice")!.IsVisible);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Workspace_OpensAnOlderQuotationOutsideTheThreeRecentRows()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            for (int index = 1; index <= 5; index++) { model.NewQuotationCommand.Execute(null); model.QuotationName = $"Study {index}"; await model.SaveQuotationCommand.ExecuteAsync(null); }
            model.BackToQuotationsCommand.Execute(null);
            WorkspaceWindow window = new() { DataContext = model, InitializeOnOpen = false }; window.Show();
            try
            {
                QuotationListRow oldest = model.Quotations[^1];
                window.FindControl<ListBox>("QuotationList")!.SelectedItem = oldest;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(oldest, model.SelectedQuotation);
                Assert.True(model.CanOpenQuotation);
                await model.OpenQuotationCommand.ExecuteAsync(null);
                Assert.Equal(oldest.Summary.Quotation.Id, model.CurrentQuotation!.Id);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IntegrationSettings_TestDoesNotDiscardPasswordBeforeSaving()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            SessionCredentials credentials = new();
            IntegrationSettingsViewModel model = new(new QuoteMatch.Desktop.Localization.Localizer(), store, new QuoteMatch.Infrastructure.Erp.ErpGateway(store, credentials));
            model.AuthenticationIndex = 1; model.SessionPassword = "synthetic-password";
            await model.TestConnectionCommand.ExecuteAsync(null);
            await model.SaveCommand.ExecuteAsync(null);
            Assert.Equal("synthetic-password", credentials.SavedPassword);
            Assert.Empty(model.SessionPassword);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class SessionCredentials : QuoteMatch.Application.Integration.IErpCredentials
    {
        public string? SavedPassword { get; private set; }
        public bool CanPersist => false;
        public Task SaveAsync(ErpConfiguration configuration, string password, CancellationToken token) { SavedPassword = password; return Task.CompletedTask; }
        public Task<string?> GetAsync(ErpConfiguration configuration, CancellationToken token) => Task.FromResult(SavedPassword);
    }

    [Theory]
    [InlineData(false, true, 45)]
    [InlineData(true, false, 30)]
    public async Task IntegrationSettings_SaveAndReloadTransportOptionsFromConnectionControls(bool encrypt, bool trust, int timeout)
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var strings = new QuoteMatch.Desktop.Localization.Localizer();
            var gateway = new QuoteMatch.Infrastructure.Erp.ErpGateway(store, new SessionCredentials());
            IntegrationSettingsViewModel model = new(strings, store, gateway);
            model.TrustServerCertificate = trust;
            ConnectionSettingsView view = new() { DataContext = model };
            Window window = new() { Content = view };
            window.Show();
            try
            {
                view.FindControl<CheckBox>("EncryptConnectionInput")!.IsChecked = encrypt;
                view.FindControl<NumericUpDown>("ConnectionTimeoutInput")!.Value = timeout;
                Dispatcher.UIThread.RunJobs();
                await model.SaveCommand.ExecuteAsync(null);

                SqliteImportStore reopenedStore = new(_directory);
                ErpConfiguration saved = await reopenedStore.GetErpConfigurationAsync(TestContext.Current.CancellationToken);
                Assert.Equal(encrypt, saved.Encrypt);
                Assert.Equal(trust, saved.TrustServerCertificate);
                Assert.Equal(timeout, saved.ConnectionTimeoutSeconds);

                IntegrationSettingsViewModel reloaded = new(strings, reopenedStore, gateway);
                await reloaded.LoadAsync(TestContext.Current.CancellationToken);
                Assert.Equal(encrypt, reloaded.Encrypt);
                Assert.Equal(trust, reloaded.TrustServerCertificate);
                Assert.Equal(timeout, reloaded.ConnectionTimeoutSeconds);
                var options = QuoteMatch.Infrastructure.Erp.ErpConnectionOptions.Create(saved, string.Empty);
                Assert.Equal(encrypt ? Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory : Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional, options.Encrypt);
                Assert.Equal(timeout, options.ConnectTimeout);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IntegrationSettings_PreserveEachQueryAndNeverPersistSessionPassword()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            model.Settings.Server = "synthetic-server"; model.Settings.Database = "Example";
            model.Settings.SessionPassword = "session-only-secret"; model.Settings.QueryText = "SELECT @SupplierProductCode";
            model.Settings.OperationIndex = 2; model.Settings.QueryText = "SELECT @ProductCode";
            await model.Settings.SaveCommand.ExecuteAsync(null);
            ErpConfiguration config = await store.GetErpConfigurationAsync(TestContext.Current.CancellationToken);
            Assert.Equal("SELECT @SupplierProductCode", config.Queries[ErpOperation.FindProduct]);
            Assert.Equal("SELECT @ProductCode", config.Queries[ErpOperation.FindProductById]);
            model.Settings.OperationIndex = 0; Assert.Equal("SELECT @SupplierProductCode", model.Settings.QueryText);
            Assert.DoesNotContain("session-only-secret", System.Text.Json.JsonSerializer.Serialize(config), StringComparison.Ordinal);
            model.Settings.Server = ""; await model.Settings.TestConnectionCommand.ExecuteAsync(null);
            Assert.Equal(model.Strings["ConnectionFailed"], model.Settings.Status);
            Assert.Empty(model.Settings.SessionPassword);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}








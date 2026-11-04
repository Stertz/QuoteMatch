using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Application.Importing;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task SupplierPackages_LabelPartialComparisonsWithTheComparedSubtotal()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotationId) = await PurchaseStoreAsync(2);
            var input = await store.GetAnalysisInputAsync(quotationId);
            await store.SaveAnalysisAsync(input.State with { Demands = [new("manual:0", "MT", 100), new("manual:1", "MT", 200)] });
            PurchasePreparationViewModel model = new(new(), store); await model.LoadAsync(quotationId);
            var package = model.Packages.Single(row => row.Name == "A");
            Assert.Equal("R$ 800,00", package.Total);
            Assert.Contains("Produtos comparados: 1", package.Comparison);
            Assert.Contains("R$ 400,00", package.Comparison);
            Assert.Contains("+100,00%", package.Comparison);
            Assert.True(package.CanSelect); Assert.False(model.Packages.Single(row => row.Name == "C").CanSelect);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SupplierPackage_SelectsTheFullQuotationAndAllowsIndividualOverrides()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, quotationId) = await PurchaseStoreAsync(2);
            PurchasePreparationViewModel model = new(new(), store); await model.LoadAsync(quotationId);
            Assert.Equal(3, model.Packages.Count); Assert.False(model.Packages.Single(row => row.Name == "C").CanSelect);
            model.SearchText = "001";
            await model.ChooseSupplierPackageCommand.ExecuteAsync(model.Packages.Single(row => row.Name == "A"));
            Assert.Equal("R$ 800,00", model.ItemSubtotal); Assert.True(model.IsShowingSummary);
            Assert.Equal(2, (await store.GetPurchaseInputAsync(quotationId)).Preparation!.Lines.Count);
            Assert.Equal(2, Assert.Single(model.Suppliers).Lines.Count);
            model.SelectedOffer = model.Offers.Single(row => row.Supplier == "B");
            await model.ChooseOfferCommand.ExecuteAsync(null);
            Assert.Equal("R$ 775,00", model.ItemSubtotal); Assert.Equal(2, model.Suppliers.Count);
            await model.ChooseSupplierPackageCommand.ExecuteAsync(model.Packages.Single(row => row.Name == "B"));
            var saved = (await store.GetPurchaseInputAsync(quotationId)).Preparation!;
            Assert.Equal(3, saved.Version); Assert.Equal(0, saved.PendingGroupCount); Assert.Equal(750m, saved.ItemSubtotal);
            Assert.All(saved.Lines, line => Assert.Equal("B", line.Offer.SupplierName));
            var input = await store.GetAnalysisInputAsync(quotationId);
            await store.SaveQuotationAsync(input.Quotation with { State = QuotationState.Finalized });
            await model.LoadAsync(quotationId); Assert.All(model.Packages, row => Assert.False(row.CanSelect));
            Assert.Equal(3, (await store.GetPurchaseInputAsync(quotationId)).Preparation!.Version);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TemplateCatalog_SearchesFiftyTemplatesByNameAndSupplierWithoutLosingTheEditor()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            PdfTemplate? latest = null;
            for (int index = 0; index < 50; index++) latest = await store.SaveTemplateAsync(new() { Id = Guid.Empty, Version = 0, Name = $"Template {index:000}", SupplierName = $"Fábrica Cabos {index:000}" });
            latest = await store.SaveTemplateAsync(latest!);
            using ImportWorkspaceViewModel model = new(new Localizer(), store, new PdfImportService(new PdfPigTextExtractor(), store));
            await model.InitializeAsync(); model.SelectedTemplate = model.Templates[3];
            string editedName = model.Editor.Name = "Unsaved synthetic editor name";
            TemplatesView view = new() { DataContext = model }; Window window = new() { Content = view, Width = 1100, Height = 740 }; window.Show();
            try
            {
                var text = view.FindControl<TextBox>("TemplateSearch")!;
                text.Text = "cabo 049"; Dispatcher.UIThread.RunJobs();
                Assert.Equal(50, model.Templates.Count); Assert.Equal(latest.Id, Assert.Single(model.VisibleTemplates).Id);
                Assert.Equal(editedName, model.Editor.Name);
                var list = view.FindControl<ListBox>("TemplateList")!; list.SelectedItem = model.VisibleTemplates[0]; Dispatcher.UIThread.RunJobs();
                Assert.Equal("Template 049", model.Editor.Name); Assert.Equal(2, model.SelectedTemplate!.Version);
                model.Editor.Name = "Updated template"; await model.SaveTemplateCommand.ExecuteAsync(null);
                Assert.Equal(latest.Id, model.SelectedTemplate!.Id); Assert.Equal(3, model.SelectedTemplate.Version);
                Assert.Equal(50, model.Templates.Count);
                text.Text = "no match"; Dispatcher.UIThread.RunJobs(); Assert.True(model.HasNoMatchingTemplates); Assert.Empty(model.VisibleTemplates);
                text.Text = ""; Dispatcher.UIThread.RunJobs(); Assert.Equal(50, model.VisibleTemplates.Count);
                Assert.True(list.Bounds.Height >= 250);
                Assert.InRange(list.ContainerFromIndex(0)!.Bounds.Height, 30, 52);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WorkspaceBrand_UsesTheEmbeddedLogoForEachTheme()
    {
        await session.Session.Dispatch(async () =>
        {
            var (store, _) = await PurchaseStoreAsync();
            using WorkspaceViewModel model = new(CreateStore(), store); await model.InitializeAsync();
            WorkspaceWindow window = new() { DataContext = model, InitializeOnOpen = false, RequestedThemeVariant = ThemeVariant.Light }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(); var logo = window.FindControl<Image>("DeveloperLogo")!;
                var light = logo.Source; Assert.NotNull(light);
                Assert.InRange(logo.Width, 158, 162); Assert.InRange(logo.Height, 49, 51);
                window.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs(); Assert.NotNull(logo.Source); Assert.NotSame(light, logo.Source);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == model.Strings["LocalWorkspace"]);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == model.Strings["DevelopedBy"]);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}

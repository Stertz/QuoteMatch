using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Application.Importing;
using QuoteMatch.Application.Integration;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using QuoteMatch.Infrastructure.Spreadsheets;
using QuoteMatch.Testing;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task TemplateTest_ReusesTheMappedSampleWithoutImportingAProposal()
    {
        await session.Session.Dispatch(async () =>
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "synthetic.xlsx");
            await File.WriteAllBytesAsync(path, SyntheticWorkbook.Xlsx(SpreadsheetRows), TestContext.Current.CancellationToken);
            var store = new SqliteImportStore(_directory); await store.InitializeAsync();
            using ImportWorkspaceViewModel model = new(new Localizer(), store, new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader()));
            model.Editor.Load(SpreadsheetTemplate);
            TemplatesView view = new() { DataContext = model }; Window owner = new() { Content = view, Width = 1100, Height = 740 }; owner.Show();
            try
            {
                var test = view.FindControl<Button>("TestTemplateButton")!;
                Assert.False(test.IsEffectivelyEnabled);
                await model.InspectSpreadsheetAsync(path); Dispatcher.UIThread.RunJobs();
                Assert.True(test.IsEffectivelyEnabled);
                test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (model.IsBusy) await Task.Yield(); Dispatcher.UIThread.RunJobs();
                var review = Assert.IsType<ImportWorkspaceWindow>(Assert.Single(owner.OwnedWindows));
                Assert.Equal(path, model.FilePath); Assert.Equal("00.1-U", Assert.Single(model.Items).ProductCode);
                Assert.Equal(6.25m, model.Items[0].ToReview().ReportedTotal); Assert.False(model.CanSave);
                review.Close(); Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
            }
            finally { owner.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(".csv")]
    [InlineData(".ods")]
    public async Task SpreadsheetFormats_ShareTheMappingReviewAndOriginalExport_WithSavedTextOptions(string extension)
    {
        await session.Session.Dispatch(async () =>
        {
            Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "synthetic" + extension);
            byte[] bytes = extension == ".csv"
                ? new System.Text.UnicodeEncoding(false, false, true).GetBytes("Code|Description|Quantity|Price\r\n000.01|Synthetic café|2|3,125\r\n")
                : SyntheticWorkbook.OpenDocument("""
                    <table:table table:name="  Data  ">
                      <table:table-row><table:table-cell office:value-type="string"><text:p>Code</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Description</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Quantity</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Price</text:p></table:table-cell></table:table-row>
                      <table:table-row><table:table-cell office:value-type="string"><text:p>000.01</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Synthetic café</text:p></table:table-cell><table:table-cell office:value-type="float" office:value="2"><text:p>2</text:p></table:table-cell><table:table-cell office:value-type="float" office:value="3.125"><text:p>3,125</text:p></table:table-cell></table:table-row>
                    </table:table>
                    """);
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var store = new SqliteImportStore(_directory); await store.InitializeAsync();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic format review", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            using ImportWorkspaceViewModel model = new(new Localizer(), store, new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader()), quotation.Id);
            await model.InitializeAsync(); model.NewTemplateCommand.Execute(null);
            var view = new TemplatesView { DataContext = model }; Window editor = new() { Content = view, Width = 1200, Height = 820 }; editor.Show();
            try
            {
                view.FindControl<ComboBox>("TemplateFormatSelector")!.SelectedIndex = 1;
                view.FindControl<ComboBox>("ProductCodeKindSelector")!.SelectedIndex = 1;
                view.FindControl<ComboBox>("DelimitedSeparatorSelector")!.SelectedIndex = 4;
                view.FindControl<ComboBox>("DelimitedEncodingSelector")!.SelectedIndex = 3;
                Dispatcher.UIThread.RunJobs();
                model.Editor.Name = "Synthetic format template"; model.Editor.SupplierName = "Synthetic supplier"; model.Editor.DefaultUnit = "un";
                await model.InspectSpreadsheetAsync(path);
                string sheetName = extension == ".csv" ? "Data" : "  Data  ";
                Assert.Equal(sheetName, model.Editor.SheetName); Assert.Equal("Synthetic café", model.Editor.Columns[1].Sample);
                await model.SaveTemplateCommand.ExecuteAsync(null);
                Assert.Equal(sheetName, model.SelectedTemplate!.Spreadsheet.SheetName);
                Assert.Equal(DelimitedSeparator.Pipe, model.SelectedTemplate!.Spreadsheet.TextOptions.Separator);
                Assert.Equal(DelimitedEncoding.Utf16LittleEndian, model.SelectedTemplate.Spreadsheet.TextOptions.Encoding);
                await model.PreviewFileAsync(path); Assert.True(model.CanSave);
            }
            finally { editor.Close(); }
            var window = new ImportWorkspaceWindow { DataContext = model, InitializeOnOpen = false, DisposeOnClose = false }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(); Assert.Equal("000.01", window.FindControl<TextBox>("ReviewedCode")!.Text);
                Assert.Equal(3.125m, model.Items.Single().ToReview().UnitPrice); Assert.Equal(6.25m, model.Items.Single().ToReview().ReportedTotal);
                await model.ConfirmCommand.ExecuteAsync(null); Assert.Single(model.Proposals);
                string exported = Path.Combine(_directory, "exported" + extension); await model.ExportOriginalAsync(exported);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(exported, TestContext.Current.CancellationToken));
                await model.OpenProposalCommand.ExecuteAsync(null); Assert.Equal("Synthetic café", model.Items.Single().Description);
                Assert.Equal(DelimitedEncoding.Utf16LittleEndian, (await store.GetProposalAsync(model.SelectedProposal!.Id))!.Template.Spreadsheet.TextOptions.Encoding);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static object?[][] SpreadsheetRows => [["Code", "Description", "Quantity", "Price"], ["00.1-U", "Synthetic product", 2m, 3.125m]];
    private static PdfTemplate SpreadsheetTemplate => new()
    {
        Name = "Synthetic workbook",
        SupplierName = "Synthetic supplier",
        DefaultUnit = "un",
        SourceFormat = ImportSourceFormat.Excel,
        Spreadsheet = new() { SheetName = "Synthetic" }
    };

    [Fact]
    public async Task ExcelMapping_UsesTheExistingTemplateEditorAndImportReview_AndPreservesOriginalOnRestart()
    {
        await session.Session.Dispatch(async () =>
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "synthetic.xlsx");
            byte[] bytes = SyntheticWorkbook.Xlsx(SpreadsheetRows);
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic Excel study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            using ImportWorkspaceViewModel model = new(new Localizer(), store, new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader()), quotation.Id, quotation.Name);
            await model.InitializeAsync(); model.NewTemplateCommand.Execute(null);
            var view = new TemplatesView { DataContext = model };
            Window editor = new() { Content = view, Width = 1200, Height = 820 }; editor.Show();
            try
            {
                view.FindControl<ComboBox>("TemplateFormatSelector")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
                view.FindControl<ComboBox>("ProductCodeKindSelector")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
                Assert.True(model.Editor.IsExcel); Assert.False(model.Editor.IsPdf);
                Assert.True(view.FindControl<Button>("InspectSpreadsheetButton")!.IsVisible);
                model.Editor.Name = "Synthetic Excel"; model.Editor.SupplierName = "Synthetic supplier"; model.Editor.DefaultUnit = "un";
                await model.InspectSpreadsheetAsync(path);
                Assert.Equal("Synthetic", model.Editor.SheetName); Assert.Equal(4, model.Editor.Columns.Count);
                Assert.Equal("00.1-U", model.Editor.Columns[0].Sample);
                view.FindControl<TextBox>("SpreadsheetCodeColumnInput")!.Text = "A";
                view.FindControl<TextBox>("SpreadsheetQuantityColumnInput")!.Text = "C";
                view.FindControl<TextBox>("SpreadsheetPriceColumnInput")!.Text = "D";
                Dispatcher.UIThread.RunJobs();
                await model.SaveTemplateCommand.ExecuteAsync(null);
                Assert.Single(model.Templates); Assert.Equal(ImportSourceFormat.Excel, model.SelectedTemplate!.SourceFormat);
                Assert.Equal(ProductCodeKind.Internal, model.SelectedTemplate.CodeKind);
                Assert.Equal("Quantity", model.SelectedTemplate.Spreadsheet.ExpectedHeaders["C"]);
                string mapping = Path.Combine(_directory, "synthetic-template.json"); await model.ExportTemplateAsync(mapping);
                await model.LoadTemplateAsync(mapping); Assert.True(model.Editor.IsExcel); Assert.Equal("C", model.Editor.QuantityColumn);
                Assert.Equal(1, model.Editor.CodeKindIndex);
                Assert.Equal("Code", model.Editor.Build().Spreadsheet.ExpectedHeaders["A"]);
                model.SelectedTemplate = model.Templates.Single();
            }
            finally { editor.Close(); }
            await model.PreviewFileAsync(path);
            Assert.True(model.CanSave); Assert.Equal(6.25m, model.Items.Single().ToReview().ReportedTotal);
            var window = new ImportWorkspaceWindow { DataContext = model, InitializeOnOpen = false, DisposeOnClose = false }; window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(); Assert.Equal("00.1-U", window.FindControl<TextBox>("ReviewedCode")!.Text);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == model.Strings["InternalCodeLabel"]);
                Assert.Contains("Synthetic", model.SelectedItem!.Position); Assert.Contains("2", model.SelectedItem.Position);
                Assert.Contains(model.Strings["SpreadsheetCalculatedTotal"], model.SelectedItem.Issues);
                await model.ConfirmCommand.ExecuteAsync(null);
                Assert.True(model.IsConsulting); Assert.False(model.CanSave); Assert.Single(model.Proposals);
                await model.OpenProposalCommand.ExecuteAsync(null); Assert.Equal("00.1-U", model.Items.Single().ProductCode);
                string exported = Path.Combine(_directory, "exported.xlsx"); await model.ExportOriginalAsync(exported);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(exported, TestContext.Current.CancellationToken));
            }
            finally { window.Close(); }
            Assert.Equal(ImportSourceFormat.Excel, (await new SqliteImportStore(_directory).GetProposalAsync(model.Proposals.Single().Id))!.Template.SourceFormat);
            var lookup = new InternalSpreadsheetLookup();
            var analysis = new QuotationAnalysisViewModel(new Localizer(), store, lookup, store);
            await analysis.LoadAsync(quotation.Id); await analysis.IdentifyPendingCommand.ExecuteAsync(null);
            Assert.Equal(1, lookup.InternalRequests); Assert.False(analysis.CanSearchSimilarity);
            Assert.Equal("00.1-U", analysis.SelectedCandidate!.Product.ProductCode);
            await analysis.LinkErpCommand.ExecuteAsync(null);
            Assert.Equal(ProductGroupingVersion.ExactProductCode, (await store.GetAnalysisInputAsync(quotation.Id)).State.Links.Single().GroupingVersion);
            Assert.Equal("+25,00%", analysis.Offers.Single().PurchaseVariation);
            Assert.Contains("2,5000", analysis.Offers.Single().LastPurchase);
            analysis.RequestedQuantity = 2m; await analysis.ApplyDemandCommand.ExecuteAsync(null);
            var purchase = new PurchasePreparationViewModel(new Localizer(), store); await purchase.LoadAsync(quotation.Id);
            await purchase.UseSuggestedCommand.ExecuteAsync(null);
            Assert.Equal(6.25m, (await store.GetPurchaseInputAsync(quotation.Id)).Preparation!.ItemSubtotal);
            Assert.Empty(await store.GetSynchronizationOperationsAsync(quotation.Id, TestContext.Current.CancellationToken));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportReview_ChangingTheFileOrTemplateInvalidatesThePreviousPreview(bool changeTemplate)
    {
        await session.Session.Dispatch(async () =>
        {
            Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "synthetic.xlsx");
            await File.WriteAllBytesAsync(path, SyntheticWorkbook.Xlsx(SpreadsheetRows), TestContext.Current.CancellationToken);
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            await store.SaveTemplateAsync(SpreadsheetTemplate);
            var second = await store.SaveTemplateAsync(SpreadsheetTemplate with { Name = "Other template" });
            using ImportWorkspaceViewModel model = new(new Localizer(), store, new PdfImportService(new PdfPigTextExtractor(), store, new ExcelWorkbookReader()), quotation.Id);
            await model.InitializeAsync(); model.SelectedTemplate = model.Templates.Single(item => item.Id != second.Id);
            await model.PreviewFileAsync(path); Assert.True(model.CanSave);
            if (changeTemplate) model.SelectedTemplate = model.Templates.Single(item => item.Id == second.Id);
            else model.FilePath = Path.Combine(_directory, "other.xlsx");
            Assert.False(model.CanSave); Assert.False(model.IsEditable);
            await model.ConfirmCommand.ExecuteAsync(null); Assert.Empty(model.Proposals);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class InternalSpreadsheetLookup : IErpProductLookup, IErpProductSearch
    {
        public int InternalRequests { get; private set; }
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => Task.FromResult("synthetic-catalog");
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token)
        {
            InternalRequests++;
            return Task.FromResult<IReadOnlyList<ErpProductCandidate>>([new(code, "Synthetic product", "Shared similarity", false,
                "brand", "Synthetic brand", "UNRELATED", null, "", new(2026, 1, 1), 2.5m, "un", "un", "synthetic-catalog", DateTime.UtcNow)
            { Brand = new("synthetic-catalog", "brand", "Synthetic brand") }]);
        }
        public Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string code, CancellationToken token) => throw new InvalidOperationException("Internal-code import must not query supplier codes.");
        public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string code, CancellationToken token) => throw new InvalidOperationException("Internal-code import must not query similarities.");
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => throw new InvalidOperationException("Internal-code import must not identify by description.");
    }
}

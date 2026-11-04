using Avalonia.Controls;
using Avalonia.Threading;
using QuoteMatch.Application.Importing;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task ImportWorkspace_ReviewsLastItemAndReopensPersistedProposal()
    {
        await session.Session.Dispatch(async () =>
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "synthetic.pdf");
            PdfDocumentBuilder builder = new();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            for (int pageIndex = 0; pageIndex < 2; pageIndex++)
            {
                var page = builder.AddPage(PageSize.A4);
                for (int index = 0; index < 25; index++)
                {
                    int number = pageIndex * 25 + index + 1;
                    page.AddText($"{number:000} Synthetic wire {number} 2 3,00 6,00", 12, new PdfPoint(40, 800 - index * 25), font);
                }
            }
            await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
            SqliteImportStore store = new(_directory);
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            var quotation = await store.SaveQuotationAsync(new QuoteMatch.Domain.Quotations.Quotation(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuoteMatch.Domain.Quotations.QuotationState.Draft, 0), TestContext.Current.CancellationToken);
            using ImportWorkspaceViewModel model = new(new Localizer(), store, new PdfImportService(new PdfPigTextExtractor(), store), quotation.Id, quotation.Name);
            await model.InitializeAsync();
            model.NewTemplateCommand.Execute(null);
            model.Editor.Name = "Synthetic template";
            model.Editor.SupplierName = "Example supplier";
            await model.SaveTemplateCommand.ExecuteAsync(null);
            Assert.Single(model.Templates);
            await model.PreviewFileAsync(string.Empty);
            Assert.Equal(model.Strings["ErrorInvalidPdf"], model.Status);
            await model.PreviewFileAsync(path);
            Assert.Equal(50, model.Items.Count);
            Assert.True(model.CanSave);
            ImportWorkspaceWindow window = new() { DataContext = model };
            window.Show();
            try
            {
                // Opened initialization refreshes catalogs without clearing the in-memory review.
                while (model.IsBusy) await Task.Delay(10, TestContext.Current.CancellationToken);
                ListBox list = window.FindControl<ListBox>("ImportedItemList")!;
                list.SelectedItem = model.Items[^1];
                list.ScrollIntoView(list.SelectedItem);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(model.Items[^1], model.SelectedItem);
                TextBox code = window.FindControl<TextBox>("ReviewedCode")!;
                Assert.Equal("050", code.Text);
                code.Text = "REVIEWED-050";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("REVIEWED-050", model.SelectedItem!.ProductCode);
                await model.ConfirmCommand.ExecuteAsync(null);

                Assert.Single(model.Proposals);
                Assert.False(model.CanSave);
                Assert.False(model.IsEditable);
                model.SelectedProposal = model.Proposals[0];
                await model.OpenProposalCommand.ExecuteAsync(null);
                Assert.Empty(model.Status);
                model.SelectedItem = model.Items[^1];
                Assert.Equal("REVIEWED-050", model.SelectedItem.ProductCode);
                Assert.Contains("050", model.SelectedItem.Evidence);
                Assert.False(window.FindControl<TextBox>("ReviewedCode")!.IsEffectivelyEnabled);
                Assert.Equal(300m, model.Proposals[0].ReportedItemTotal);
            }
            finally { window.Close(); }
            using ImportWorkspaceViewModel restarted = new(new Localizer(), new SqliteImportStore(_directory), new PdfImportService(new PdfPigTextExtractor(), store));
            await restarted.InitializeAsync();
            Assert.Single(restarted.Proposals);
            Assert.Single(restarted.Templates);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TemplateEditor_ImportsExportsAndVersionsWithoutRewritingSavedDefinition()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory);
            Localizer strings = new(); strings.SetCulture("en-US");
            using ImportWorkspaceViewModel model = new(strings, store, new PdfImportService(new PdfPigTextExtractor(), store));
            await model.InitializeAsync();
            model.NewTemplateCommand.Execute(null);
            model.Editor.Name = "Synthetic"; model.Editor.SupplierName = "Example";
            model.Editor.RecordPattern = "(";
            model.ValidateTemplateCommand.Execute(null);
            Assert.Equal(strings["ErrorInvalidTemplate"], model.Status);
            model.Editor.RecordPattern = new PdfTemplate().RecordPattern;
            await model.SaveTemplateCommand.ExecuteAsync(null);
            Assert.Equal(1, model.SelectedTemplate!.Version);
            model.Editor.Name = "Revised";
            await model.SaveTemplateCommand.ExecuteAsync(null);
            Assert.Equal(2, model.SelectedTemplate!.Version);
            string path = Path.Combine(_directory, "template.json");
            await model.ExportTemplateAsync(path);
            await model.LoadTemplateAsync(path);
            Assert.Null(model.SelectedTemplate);
            Assert.Equal("Revised", model.Editor.Name);
            await model.SaveTemplateCommand.ExecuteAsync(null);
            Assert.Equal(2, model.Templates.Count);
            Assert.Equal(1, model.SelectedTemplate!.Version);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}




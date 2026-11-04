using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using QuoteMatch.Application.Importing;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    private static PdfTemplate AutomaticTemplate(string name) => new()
    {
        Name = name,
        SupplierName = "Example supplier",
        IdentificationPattern = "Example supplier",
        TableEndPattern = "^Total"
    };
    private async Task<(ImportWorkspaceViewModel Model, SqliteImportStore Store, string Path)> AutomaticWorkspaceAsync(IPdfTextExtractor extractor, params PdfTemplate[] templates)
    {
        Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "synthetic-auto.pdf");
        PdfDocumentBuilder builder = new(); var font = builder.AddStandard14Font(Standard14Font.Helvetica); var page = builder.AddPage(PageSize.A4);
        page.AddText("Example supplier", 12, new PdfPoint(40, 800), font);
        page.AddText("001 Synthetic wire 2 3,00 6,00", 12, new PdfPoint(40, 760), font);
        page.AddText("002 Synthetic cable 3 2,00 6,00", 12, new PdfPoint(40, 730), font);
        await File.WriteAllBytesAsync(path, builder.Build());
        var store = new SqliteImportStore(_directory); await store.InitializeAsync();
        var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic automatic import", "", Now, Now, QuotationState.Draft, 0));
        foreach (var template in templates) await store.SaveTemplateAsync(template);
        var model = new ImportWorkspaceViewModel(new(), store, new PdfImportService(extractor, store), quotation.Id);
        await model.InitializeAsync(); return (model, store, path);
    }

    [Fact]
    public async Task SelectingPdf_AutomaticallyReviewsTheFirstMatchingLayoutWithoutSavingAndAllowsACachedManualOverride()
    {
        await session.Session.Dispatch(async () =>
        {
            var extractor = new AutomaticWorkspaceExtractor();
            var (model, store, path) = await AutomaticWorkspaceAsync(extractor, AutomaticTemplate("A company only") with { RecordStartPattern = "^ITEM" },
                AutomaticTemplate("B matching layout"), AutomaticTemplate("C manual layout") with { DefaultUnit = "CX" });
            using (model)
            {
                await model.SelectFilesAsync([path]);
                Assert.Equal("B matching layout", model.SelectedTemplate!.Name); Assert.Equal(2, model.Items.Count);
                Assert.True(model.CanSave, model.Status); Assert.Contains("B matching layout", model.TemplateSuggestionStatus);
                Assert.Equal(1, extractor.Calls); Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
                model.SelectedTemplate = model.Templates.Single(template => template.Name == "C manual layout");
                Assert.False(model.CanSave); Assert.Empty(model.TemplateSuggestionStatus);
                await model.PreviewCommand.ExecuteAsync(null);
                Assert.True(model.CanSave, model.Status); Assert.Equal(1, extractor.Calls);
                Assert.All(model.Items, item => Assert.Equal("CX", item.Unit));
                Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
                await model.ConfirmCommand.ExecuteAsync(null); Assert.Single(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AutomaticDetection_PrefersTheCurrentTemplateAndPreservesAManualSelectionMadeWhileReading()
    {
        await session.Session.Dispatch(async () =>
        {
            var extractor = new BlockingAutomaticExtractor();
            var (model, store, path) = await AutomaticWorkspaceAsync(extractor, AutomaticTemplate("A layout"), AutomaticTemplate("B preferred layout"));
            using (model)
            {
                model.SelectedTemplate = model.Templates[1];
                var work = model.SelectFilesAsync([path]); await extractor.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
                model.SelectedTemplate = model.Templates[0]; extractor.Release.SetResult(); await work;
                Assert.Equal("A layout", model.SelectedTemplate!.Name); Assert.False(model.CanSave); Assert.Empty(model.Items);
                Assert.Equal(model.Strings["ImportPreviewChanged"], model.Status);
                model.SelectedTemplate = model.Templates[1]; await model.SelectFilesAsync([path]);
                Assert.Equal("B preferred layout", model.SelectedTemplate!.Name); Assert.Equal(2, model.Items.Count); Assert.True(model.CanSave);
                Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DropArea_AcceptsALocalPdfAndUsesTheSameAutomaticReviewFlow()
    {
        await session.Session.Dispatch(async () =>
        {
            var extractor = new AutomaticWorkspaceExtractor();
            var (model, store, path) = await AutomaticWorkspaceAsync(extractor, AutomaticTemplate("Synthetic matching layout"));
            var window = new ImportWorkspaceWindow { DataContext = model, InitializeOnOpen = false }; window.Show();
            try
            {
                var area = window.FindControl<Border>("ImportDropZone")!; Assert.True(DragDrop.GetAllowDrop(area));
                var file = await window.StorageProvider.TryGetFileFromPathAsync(path); Assert.NotNull(file);
                var data = new DataTransfer(); data.Add(DataTransferItem.CreateFile(file));
                var over = new DragEventArgs(DragDrop.DragOverEvent, data, area, default, KeyModifiers.None);
                area.RaiseEvent(over); Assert.Equal(DragDropEffects.Copy, over.DragEffects); Assert.Contains("drag-over", area.Classes);
                area.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, area, default, KeyModifiers.None));
                while (model.IsBusy) { await Task.Yield(); Dispatcher.UIThread.RunJobs(); }
                Assert.Equal(path, model.FilePath); Assert.Equal(2, model.Items.Count); Assert.True(model.CanSave, model.Status);
                Assert.DoesNotContain("drag-over", area.Classes); Assert.Equal(1, extractor.Calls);
                Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
                var multiple = new DataTransfer(); multiple.Add(DataTransferItem.CreateFile(file)); multiple.Add(DataTransferItem.CreateFile(file));
                area.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, multiple, area, default, KeyModifiers.None));
                Assert.Equal(model.Strings["ImportDropSingleFile"], model.Status); Assert.Equal(2, model.Items.Count);
                var folder = await window.StorageProvider.TryGetFolderFromPathAsync(_directory); Assert.NotNull(folder);
                var directory = new DataTransfer(); directory.Add(DataTransferItem.CreateFile(folder));
                area.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, directory, area, default, KeyModifiers.None));
                Assert.Equal(model.Strings["ImportDropUnsupported"], model.Status); Assert.True(model.CanSave);
                var text = new DataTransfer(); text.Add(DataTransferItem.CreateText("synthetic text"));
                var unsupported = new DragEventArgs(DragDrop.DragOverEvent, text, area, default, KeyModifiers.None);
                area.RaiseEvent(unsupported); Assert.Equal(DragDropEffects.None, unsupported.DragEffects);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FileSelection_RejectsMultipleOrUnsupportedFilesAndKeepsTheCurrentReview()
    {
        await session.Session.Dispatch(async () =>
        {
            var (model, store, path) = await AutomaticWorkspaceAsync(new AutomaticWorkspaceExtractor(), AutomaticTemplate("Synthetic layout"));
            using (model)
            {
                await model.SelectFilesAsync([path]);
                await model.SelectFilesAsync([path, path]); Assert.Equal(model.Strings["ImportDropSingleFile"], model.Status);
                await model.SelectFilesAsync(["synthetic.exe"]); Assert.Equal(model.Strings["ImportDropUnsupported"], model.Status);
                Assert.Equal(path, model.FilePath); Assert.Equal(2, model.Items.Count); Assert.True(model.CanSave);
                Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detection_WithoutAnIdentifierOrMatchingItemLeavesManualExtractionAvailable(bool noIdentifier)
    {
        await session.Session.Dispatch(async () =>
        {
            var extractor = new AutomaticWorkspaceExtractor();
            var template = AutomaticTemplate("Synthetic manual layout");
            template = noIdentifier ? template with { IdentificationPattern = "" } : template with { RecordStartPattern = "^ITEM" };
            var (model, store, path) = await AutomaticWorkspaceAsync(extractor, template);
            using (model)
            {
                await model.SelectFilesAsync([path]);
                Assert.Empty(model.Items); Assert.False(model.CanSave); Assert.Equal(model.Strings["ImportTemplateNotDetected"], model.Status);
                Assert.Equal(noIdentifier ? 0 : 1, extractor.Calls);
                if (noIdentifier) { await model.PreviewCommand.ExecuteAsync(null); Assert.Equal(2, model.Items.Count); Assert.True(model.CanSave); }
                Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SpreadsheetSelection_RemainsManualAndDoesNotReadPdfText()
    {
        await session.Session.Dispatch(async () =>
        {
            var extractor = new AutomaticWorkspaceExtractor();
            var (model, _, _) = await AutomaticWorkspaceAsync(extractor, AutomaticTemplate("Synthetic PDF layout"));
            using (model)
            {
                string path = Path.Combine(_directory, "synthetic.xlsx");
                await model.SelectFilesAsync([path]);
                Assert.Equal(path, model.FilePath); Assert.Equal(0, extractor.Calls);
                Assert.Equal(model.Strings["ImportSpreadsheetSelectTemplate"], model.Status);
                Assert.Empty(model.Items); Assert.False(model.CanSave);
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detection_RejectsLateResultsAfterCancellationOrFileReplacement(bool replaceFile)
    {
        await session.Session.Dispatch(async () =>
        {
            var extractor = new BlockingAutomaticExtractor();
            var (model, store, path) = await AutomaticWorkspaceAsync(extractor, AutomaticTemplate("Synthetic layout"));
            using (model)
            {
                var work = model.SelectFilesAsync([path]); await extractor.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
                if (replaceFile) model.FilePath = "other-synthetic.pdf"; else model.CancelCommand.Execute(null);
                extractor.Release.SetResult(); await work;
                Assert.False(model.IsBusy); Assert.False(model.CanSave); Assert.Empty(model.Items); Assert.Empty(model.RawText);
                Assert.NotEqual(model.Strings["ImportWorking"], model.Status);
                Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class AutomaticWorkspaceExtractor : IPdfTextExtractor
    {
        public int Calls { get; private set; }
        public ExtractedPdf Extract(byte[] bytes, PdfTextStrategy strategy, CancellationToken token)
        { Calls++; return new PdfPigTextExtractor().Extract(bytes, strategy, token); }
    }
    private sealed class BlockingAutomaticExtractor : IPdfTextExtractor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExtractedPdf Extract(byte[] bytes, PdfTextStrategy strategy, CancellationToken token)
        {
            Started.TrySetResult(); Release.Task.GetAwaiter().GetResult();
            return new PdfPigTextExtractor().Extract(bytes, strategy, CancellationToken.None);
        }
    }
}

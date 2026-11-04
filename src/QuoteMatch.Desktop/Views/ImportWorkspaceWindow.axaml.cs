using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public sealed partial class ImportWorkspaceWindow : Window
{
    public bool InitializeOnOpen { get; init; } = true;
    public bool DisposeOnClose { get; init; } = true;
    public ImportWorkspaceWindow()
    {
        InitializeComponent();
        var dropZone = this.FindControl<Border>("ImportDropZone")!;
        dropZone.AddHandler(DragDrop.DragOverEvent, DragOver);
        dropZone.AddHandler(DragDrop.DragLeaveEvent, DragLeave);
        dropZone.AddHandler(DragDrop.DropEvent, DropFile);
        Opened += async (_, _) => { if (InitializeOnOpen && DataContext is ImportWorkspaceViewModel model) await model.InitializeAsync(); };
        Closed += (_, _) => { if (DisposeOnClose) (DataContext as ImportWorkspaceViewModel)?.Dispose(); };
    }
    private void CloseWindow(object? sender, RoutedEventArgs args) => Close();
    private ImportWorkspaceViewModel Model => (ImportWorkspaceViewModel)DataContext!;
    private async void ChoosePdf(object? sender, RoutedEventArgs args)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Model.Strings["ChooseDocument"],
            AllowMultiple = false,
            FileTypeFilter = [ImportFileTypes.Documents, FilePickerFileTypes.Pdf, ImportFileTypes.Excel]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is string path) await Model.SelectFilesAsync([path]);
    }
    private void DragOver(object? sender, DragEventArgs args)
    {
        bool allowed = Model.ShowFileSelection && !Model.IsBusy && args.DataTransfer.Contains(DataFormat.File);
        args.DragEffects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over"); if (allowed) border.Classes.Add("drag-over");
        }
        args.Handled = true;
    }
    private void DragLeave(object? sender, DragEventArgs args) { if (sender is Border border) border.Classes.Remove("drag-over"); }
    private async void DropFile(object? sender, DragEventArgs args)
    {
        DragLeave(sender, args); args.Handled = true;
        if (!Model.ShowFileSelection || Model.IsBusy) return;
        var files = args.DataTransfer.TryGetFiles()?.ToArray() ?? [];
        var paths = files.OfType<IStorageFile>().Select(file => file.TryGetLocalPath()).ToArray();
        if (paths.Length != files.Length || paths.Any(path => path is null)) { Model.Status = Model.Strings["ImportDropUnsupported"]; return; }
        await Model.SelectFilesAsync(paths.Select(path => path!).ToArray());
    }
    private async void ExportOriginal(object? sender, RoutedEventArgs args)
    {
        if (Model.SelectedProposal is null) return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Model.Strings["ExportOriginal"],
            SuggestedFileName = Model.SelectedProposal.FileName,
            DefaultExtension = Path.GetExtension(Model.SelectedProposal.FileName).TrimStart('.'),
            FileTypeChoices = [ImportFileTypes.Documents]
        });
        if (file?.TryGetLocalPath() is string path) await Model.ExportOriginalAsync(path);
    }
}



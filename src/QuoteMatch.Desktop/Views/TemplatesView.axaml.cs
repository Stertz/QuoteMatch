using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public sealed partial class TemplatesView : UserControl
{
    public TemplatesView() => InitializeComponent();
    private ImportWorkspaceViewModel Model => (ImportWorkspaceViewModel)DataContext!;
    private async void ChooseSample(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = Model.Strings["ChooseDocument"], FileTypeFilter = [Model.Editor.IsExcel ? ImportFileTypes.Excel : FilePickerFileTypes.Pdf] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not string path) return;
        if (Model.Editor.IsExcel) await Model.InspectSpreadsheetAsync(path);
        else Model.FilePath = path;
    }
    private async void TestSample(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!Model.CanTestTemplate) return;
        await Model.PreviewFileAsync(Model.FilePath, true);
        if (Model.Items.Count == 0) return;
        ImportWorkspaceWindow window = new() { DataContext = Model, InitializeOnOpen = false, DisposeOnClose = false };
        await window.ShowDialog(owner);
    }
    private async void ImportTemplate(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = Model.Strings["ImportTemplate"], FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is string path) await Model.LoadTemplateAsync(path);
    }
    private async void ExportTemplate(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new() { Title = Model.Strings["ExportTemplate"], SuggestedFileName = "template.json", DefaultExtension = "json", FileTypeChoices = [new("JSON") { Patterns = ["*.json"] }] });
        if (file?.TryGetLocalPath() is string path) await Model.ExportTemplateAsync(path);
    }
}

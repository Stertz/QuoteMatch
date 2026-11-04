using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QuoteMatch.Desktop.ViewModels;
namespace QuoteMatch.Desktop.Views;

public sealed partial class QuerySettingsView : UserControl
{
    public QuerySettingsView() => InitializeComponent();
    private async void ImportSql(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || DataContext is not IntegrationSettingsViewModel model) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = model.Strings["ImportSqlFile"], FileTypeFilter = [new("SQL") { Patterns = ["*.sql"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is string path) await model.LoadQueryFileAsync(path);
    }
}

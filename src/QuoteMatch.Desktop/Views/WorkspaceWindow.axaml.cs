using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using QuoteMatch.Desktop.ViewModels;

namespace QuoteMatch.Desktop.Views;

public sealed partial class WorkspaceWindow : Window
{
    public bool InitializeOnOpen { get; init; } = true;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    public WorkspaceWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is not WorkspaceViewModel model) return;
            model.CanRestartPresentation = () => OwnedWindows.All(window => window is ApplicationUpdatesWindow);
            model.Updates.RestartRequested += RestartForUpdate;
            if (InitializeOnOpen) { await model.InitializeAsync(); _ = model.Updates.InitializeAsync(); _updateTimer.Start(); }
        };
        _updateTimer.Tick += async (_, _) => { if (DataContext is WorkspaceViewModel model) await model.Updates.InitializeAsync(); };
        Closed += (_, _) =>
        {
            _updateTimer.Stop();
            if (DataContext is WorkspaceViewModel model) { model.Updates.RestartRequested -= RestartForUpdate; model.CanRestartPresentation = null; model.Dispose(); }
        };
    }
    private WorkspaceViewModel Model => (WorkspaceViewModel)DataContext!;
    private async void OpenApplicationUpdates(object? sender, RoutedEventArgs args) =>
        await new ApplicationUpdatesWindow { DataContext = Model.Updates }.ShowDialog(this);
    private void RestartForUpdate()
    {
        foreach (Window window in OwnedWindows.ToArray()) window.Close();
        Close();
    }
    private async void QuotationTabChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (args.Source == sender && DataContext is WorkspaceViewModel model && model.QuotationTabIndex == 2) await model.RefreshPurchaseAsync();
    }
    private async void OpenSelectedQuotation(object? sender, TappedEventArgs args) => await Model.OpenQuotationCommand.ExecuteAsync(null);
    private async void ImportProposal(object? sender, RoutedEventArgs args)
    {
        if (!Model.CanImport) return;
        using ImportWorkspaceViewModel import = Model.CreateImport();
        await new ImportWorkspaceWindow { DataContext = import }.ShowDialog(this);
        await Model.RefreshAsync();
    }
    private async void OpenSelectedProposal(object? sender, TappedEventArgs args) => await ShowProposalAsync();
    private async void OpenProposal(object? sender, RoutedEventArgs args) => await ShowProposalAsync();
    private async Task ShowProposalAsync()
    {
        if (Model.SelectedProposal is null) return;
        using ImportWorkspaceViewModel reader = Model.CreateProposalReader();
        await reader.InitializeAsync(); reader.SelectedProposal = Model.SelectedProposal.Summary;
        await reader.OpenProposalCommand.ExecuteAsync(null);
        await new ImportWorkspaceWindow { DataContext = reader, InitializeOnOpen = false }.ShowDialog(this);
    }
}

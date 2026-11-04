using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Desktop.ViewModels;

public partial class WorkspaceViewModel
{
    public ApplicationUpdatesViewModel Updates { get; }
    public bool IsInteractionEnabled => !IsBusy && !Updates.IsRestarting;
    public Func<bool>? CanRestartPresentation { get; set; }

    private async Task<bool> CanRestartForUpdateAsync(CancellationToken token)
    {
        if (CanRestartPresentation?.Invoke() != true || IsEditingDetails || IsBusy || OrderInputs.IsBusy || Analysis.IsBusy
            || Purchase.IsBusy || Orders.IsBusy || Orders.IsPreviewing || Synchronization.IsBusy || Settings.IsBusy || Templates.IsBusy
            || Settings.HasUnsavedChanges || Templates.Editor.HasUnsavedChanges) return false;
        try
        {
            foreach (var quotation in await _store.GetQuotationsAsync(token))
            {
                if ((await _store.GetOrderOperationsAsync(quotation.Quotation.Id, token)).Any(operation => operation.State is OrderOperationState.Pending or OrderOperationState.Unknown)
                    || (await _store.GetSynchronizationOperationsAsync(quotation.Quotation.Id, token)).Any(operation => operation.State is OrderOperationState.Pending or OrderOperationState.Unknown)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { return false; }
    }
}

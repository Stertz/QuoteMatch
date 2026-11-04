using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using QuoteMatch.Application.Integration;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Infrastructure.Erp;

public sealed class ErpPurchaseSynchronizationWriter(IErpConfigurationRepository configuration, IErpCredentials credentials) : IPurchaseSynchronizationWriter
{
    public async Task<SynchronizationWriteProfile> GetProfileAsync(OrderWriteKind kind, CancellationToken token) =>
        PurchaseOrderScripts.SynchronizationProfile(await configuration.GetErpConfigurationAsync(token), kind);

    public async Task<SynchronizationFinancialPreview> PreviewFinancialsAsync(SynchronizationWritePlan plan, CancellationToken token)
    {
        SynchronizationWriteRules.Validate(plan);
        var config = await configuration.GetErpConfigurationAsync(token);
        if (PurchaseOrderScripts.SynchronizationProfile(config, plan.Kind) != plan.Profile
            || ProtectedErpCredentials.CatalogIdentity(config) != plan.Selection.Preview.Proposal.Brand!.CatalogIdentity) throw new OrderException(OrderError.Conflict);
        string password = config.Authentication == ErpAuthentication.SqlLogin
            ? await credentials.GetAsync(config, token) ?? throw new ErpException(ErpError.CredentialsUnavailable) : "";
        try
        {
            await using SqlConnection connection = new(ErpConnectionOptions.Create(config, password).ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var result = await ReadFinancialsAsync(connection, transaction, config, plan, token);
            await transaction.CommitAsync(token); return result;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        { throw new ErpException(ErpError.QueryFailed); }
    }

    private static async Task<SynchronizationFinancialPreview> ReadFinancialsAsync(SqlConnection connection, SqlTransaction transaction,
        ErpConfiguration config, SynchronizationWritePlan plan, CancellationToken token)
    {
        var result = await ErpPurchaseOrderReader.RunAsync<SynchronizationFinancialPreview>(connection, transaction,
            PurchaseOrderScripts.Get(config, ErpOperation.PreviewPurchaseFinancials), plan with { Allocation = null, FinancialPreview = null }, token);
        SynchronizationFinancialRules.ValidatePreview(plan, result);
        return result;
    }

    public static string FinancialFingerprint(SynchronizationFinancialPreview preview) => JsonSerializer.Serialize(preview with
    {
        Lines = preview.Lines.OrderBy(line => line.Position).ToArray(),
        Sources = preview.Sources.OrderBy(source => source.CatalogIdentity, StringComparer.Ordinal).ThenBy(source => source.OrderId, StringComparer.Ordinal).ToArray()
    }, Persistence.SqliteImportStore.JsonOptions);

    private static async Task ValidateFinancialsAsync(SqlConnection connection, SqlTransaction transaction, ErpConfiguration config, SynchronizationWritePlan plan, CancellationToken token)
    {
        if (!plan.RequiresFinancialPreview) return;
        var current = await ReadFinancialsAsync(connection, transaction, config, plan, token);
        if (FinancialFingerprint(current) != FinancialFingerprint(plan.FinancialPreview!)) throw new OrderException(OrderError.Conflict);
    }

    public async Task<SynchronizationAllocation> ReserveAsync(SynchronizationWritePlan plan, CancellationToken token)
    {
        SynchronizationWriteRules.Validate(plan, true);
        if (plan.Kind != OrderWriteKind.Create || plan.Allocation is not null) throw new OrderException(OrderError.InvalidChange);
        var config = await configuration.GetErpConfigurationAsync(token);
        if (PurchaseOrderScripts.SynchronizationProfile(config, plan.Kind) != plan.Profile
            || ProtectedErpCredentials.CatalogIdentity(config) != plan.Selection.Preview.Proposal.Brand!.CatalogIdentity) throw new OrderException(OrderError.Conflict);
        var write = PurchaseOrderScripts.WriteConfiguration(config);
        string password = write.Authentication == ErpAuthentication.SqlLogin
            ? await credentials.GetAsync(write, token) ?? throw new ErpException(ErpError.CredentialsUnavailable) : "";
        try
        {
            var options = ErpConnectionOptions.Create(write, password); options.ApplicationIntent = ApplicationIntent.ReadWrite;
            await using SqlConnection connection = new(options.ConnectionString); await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await ValidateFinancialsAsync(connection, transaction, config, plan, token);
            var allocation = await ErpPurchaseOrderReader.RunAsync<SynchronizationAllocation>(connection, transaction,
                PurchaseOrderScripts.Get(config, ErpOperation.ReservePurchaseSynchronization), plan, token);
            SynchronizationWriteRules.ValidateAllocation(plan, allocation);
            await transaction.CommitAsync(token); return allocation;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        { throw new ErpException(ErpError.QueryFailed); }
    }

    public async Task<OrderWriteReceipt> ExecuteAsync(SynchronizationWritePlan plan, CancellationToken token)
    {
        SynchronizationWriteRules.Validate(plan, true);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(120)); token = budget.Token;
        var config = await configuration.GetErpConfigurationAsync(token);
        if (PurchaseOrderScripts.SynchronizationProfile(config, plan.Kind) != plan.Profile
            || ProtectedErpCredentials.CatalogIdentity(config) != plan.Selection.Preview.Proposal.Brand!.CatalogIdentity)
            throw new OrderException(OrderError.Conflict);
        var write = PurchaseOrderScripts.WriteConfiguration(config);
        string password = write.Authentication == ErpAuthentication.SqlLogin
            ? await credentials.GetAsync(write, token) ?? throw new ErpException(ErpError.CredentialsUnavailable) : "";
        try
        {
            var options = ErpConnectionOptions.Create(write, password); options.ApplicationIntent = ApplicationIntent.ReadWrite;
            await using SqlConnection connection = new(options.ConnectionString); await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var expected = plan.Selection.Preview.Target;
            if (expected is not null)
            {
                var raw = await ErpPurchaseOrderReader.RunAsync<PurchaseOrderScriptResult>(connection, transaction,
                PurchaseOrderScripts.Get(config, ErpOperation.ReadPurchaseOrder), new PurchaseOrderReadRequest(expected.Header.Id), token);
                var current = ErpPurchaseOrderReader.Snapshot(raw, expected.Header.Id, config);
                if (ErpPurchaseOrderWriter.Fingerprint(current) != ErpPurchaseOrderWriter.Fingerprint(expected)) throw new OrderException(OrderError.Conflict);
            }
            else if (plan.Allocation is null) throw new OrderException(OrderError.InvalidChange);
            var supplier = plan.Selection.Preview.Supplier;
            var suppliers = await ErpPurchaseOrderReader.RunAsync<BuyingSupplierSnapshot[]>(connection, transaction,
                PurchaseOrderScripts.Get(config, ErpOperation.FindBuyingSupplier), new BuyingSupplierReadRequest(supplier.Brand.Id, supplier.Branch), token);
            if (suppliers.Length != 1 || suppliers[0] is null
                || suppliers[0] with { FetchedAtUtc = DateTime.UnixEpoch } != supplier with { FetchedAtUtc = DateTime.UnixEpoch })
                throw new OrderException(OrderError.Conflict);
            await ValidateFinancialsAsync(connection, transaction, config, plan, token);
            var receipt = await ErpPurchaseOrderReader.RunAsync<OrderWriteReceipt>(connection, transaction,
                PurchaseOrderScripts.Get(config, plan.Kind == OrderWriteKind.Create ? ErpOperation.CreatePurchaseSynchronization : ErpOperation.UpdatePurchaseSynchronization), plan, token);
            if (receipt.OperationId != plan.OperationId || receipt.State != OrderOperationState.Committed || receipt.OrderId != plan.TargetId)
                throw new ErpException(ErpError.InvalidResult);
            if (plan.RequiresFinancialPreview)
            {
                if (receipt.FinancialRevision != plan.FinancialPreview!.Revision) throw new ErpException(ErpError.InvalidResult);
                var after = await ErpPurchaseOrderReader.RunAsync<PurchaseOrderScriptResult>(connection, transaction,
                    PurchaseOrderScripts.Get(config, ErpOperation.ReadPurchaseOrder), new PurchaseOrderReadRequest(plan.TargetId!), token);
                SynchronizationFinancialRules.ValidateWrittenOrder(plan, ErpPurchaseOrderReader.Snapshot(after, plan.TargetId!, config));
            }
            await transaction.CommitAsync(token); return receipt;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        { throw new ErpException(ErpError.QueryFailed); }
    }

    public async Task<OrderWriteReceipt> ReconcileAsync(SynchronizationOperation operation, CancellationToken token)
    {
        var plan = operation.Plan;
        if (plan.TargetId is null) return new(plan.OperationId, OrderOperationState.Unknown, null);
        var config = await configuration.GetErpConfigurationAsync(token);
        if (ProtectedErpCredentials.CatalogIdentity(config) != plan.Selection.Preview.Proposal.Brand!.CatalogIdentity)
            throw new OrderException(OrderError.Conflict);
        string password = config.Authentication == ErpAuthentication.SqlLogin
            ? await credentials.GetAsync(config, token) ?? throw new ErpException(ErpError.CredentialsUnavailable) : "";
        try
        {
            await using SqlConnection connection = new(ErpConnectionOptions.Create(config, password).ConnectionString);
            await connection.OpenAsync(token);
            return await ErpPurchaseOrderReader.RunAsync<OrderWriteReceipt>(connection, null,
                PurchaseOrderScripts.Get(config, ErpOperation.ReconcilePurchaseSynchronization), plan, token);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        { throw new ErpException(ErpError.QueryFailed); }
    }
}

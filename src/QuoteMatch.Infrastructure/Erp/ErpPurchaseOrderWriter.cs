using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using QuoteMatch.Application.Integration;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Infrastructure.Erp;

public sealed class ErpPurchaseOrderWriter(IErpConfigurationRepository configuration, IErpCredentials credentials) : IPurchaseOrderWriter
{
    public async Task<string> GetScriptIdentityAsync(OrderWriteKind kind, CancellationToken token) =>
        PurchaseOrderScripts.Identity(await configuration.GetErpConfigurationAsync(token), kind);

    public async Task<OrderWriteReceipt> ExecuteAsync(OrderWritePlan plan, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(120)); token = budget.Token;
        OrderChangeRules.Validate(plan);
        var config = await configuration.GetErpConfigurationAsync(token);
        if (PurchaseOrderScripts.Identity(config, plan.Kind) != plan.ScriptIdentity
            || ProtectedErpCredentials.CatalogIdentity(config) != plan.Reference.CatalogIdentity) throw new OrderException(OrderError.Conflict);
        var write = PurchaseOrderScripts.WriteConfiguration(config);
        string password = write.Authentication == ErpAuthentication.SqlLogin
            ? await credentials.GetAsync(write, token) ?? throw new ErpException(ErpError.CredentialsUnavailable) : "";
        try
        {
            var options = ErpConnectionOptions.Create(write, password);
            options.ApplicationIntent = ApplicationIntent.ReadWrite;
            await using SqlConnection connection = new(options.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var snapshots = plan.ExpectedOrder is { } targetOrder ? plan.CoverageOrders.Append(targetOrder) : plan.CoverageOrders;
            foreach (var expected in snapshots)
            {
                var raw = await ErpPurchaseOrderReader.RunAsync<PurchaseOrderScriptResult>(connection, transaction,
                    PurchaseOrderScripts.Get(config, ErpOperation.ReadPurchaseOrder), new PurchaseOrderReadRequest(expected.Header.Id), token);
                var current = ErpPurchaseOrderReader.Snapshot(raw, expected.Header.Id, config);
                if (Fingerprint(expected) != Fingerprint(current)) throw new OrderException(OrderError.Conflict);
            }
            var receipt = await ErpPurchaseOrderReader.RunAsync<OrderWriteReceipt>(connection, transaction,
                PurchaseOrderScripts.Get(config, plan.Kind == OrderWriteKind.Create ? ErpOperation.CreatePurchaseOrder : ErpOperation.UpdatePurchaseOrder), plan, token, config.WriteOperatorCode);
            if (receipt.OperationId != plan.OperationId || receipt.State != OrderOperationState.Committed || string.IsNullOrWhiteSpace(receipt.OrderId)
                || plan.ExpectedOrder is { } target && receipt.OrderId != target.Header.Id) throw new ErpException(ErpError.InvalidResult);
            await transaction.CommitAsync(token);
            return receipt;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        { throw new ErpException(ErpError.QueryFailed); }
    }
    public async Task<OrderWriteReceipt> ReconcileAsync(OrderOperation operation, CancellationToken token)
    {
        var config = await configuration.GetErpConfigurationAsync(token);
        if (ProtectedErpCredentials.CatalogIdentity(config) != operation.Plan.Reference.CatalogIdentity) throw new OrderException(OrderError.Conflict);
        var receipt = await ReadReceiptAsync(config, operation.Plan, token);
        return receipt;
    }
    private async Task<OrderWriteReceipt> ReadReceiptAsync(ErpConfiguration config, OrderWritePlan plan, CancellationToken token)
    {
        string password = config.Authentication == ErpAuthentication.SqlLogin
            ? await credentials.GetAsync(config, token) ?? throw new ErpException(ErpError.CredentialsUnavailable) : "";
        try
        {
            await using SqlConnection connection = new(ErpConnectionOptions.Create(config, password).ConnectionString);
            await connection.OpenAsync(token);
            return await ErpPurchaseOrderReader.RunAsync<OrderWriteReceipt>(connection, null,
                PurchaseOrderScripts.Get(config, ErpOperation.ReconcilePurchaseOperation), plan, token);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        { throw new ErpException(ErpError.QueryFailed); }
    }
    public static string Fingerprint(PurchaseOrderSnapshot snapshot) => JsonSerializer.Serialize(
        snapshot with { FetchedAtUtc = DateTime.UnixEpoch, Lines = snapshot.Lines.OrderBy(line => line.Id, StringComparer.Ordinal).ToArray() }, Persistence.SqliteImportStore.JsonOptions);
}

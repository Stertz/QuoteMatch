using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using QuoteMatch.Application.Integration;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Infrastructure.Erp;

public sealed class ErpPurchaseOrderReader(IErpConfigurationRepository configuration, IErpCredentials credentials) : IPurchaseOrderReader, IErpBuyingSupplierLookup
{
    public async Task<BuyingSupplierSnapshot?> FindAsync(ErpBrandIdentity brand, string branch, CancellationToken cancellationToken)
    {
        var config = await configuration.GetErpConfigurationAsync(cancellationToken);
        if (brand.CatalogIdentity != ProtectedErpCredentials.CatalogIdentity(config) || string.IsNullOrWhiteSpace(brand.Id)
            || string.IsNullOrWhiteSpace(branch) || branch.Length > 200 || brand.Id.Length > 200) throw new ErpException(ErpError.InvalidQuery);
        var rows = await ExecuteAsync<BuyingSupplierSnapshot[]>(ErpOperation.FindBuyingSupplier, new BuyingSupplierReadRequest(brand.Id, branch), cancellationToken, config);
        if (rows.Length == 0) return null;
        if (rows.Length != 1 || rows[0] is null) throw new ErpException(ErpError.InvalidResult);
        return BuyingSupplierRules.Validate(rows[0], brand, branch);
    }
    public async Task<IReadOnlyList<PurchaseOrderHeader>> SearchAsync(PurchaseOrderSearch search, CancellationToken token)
    {
        if (search.From > search.To || new[] { search.Number, search.Supplier, search.Branch, search.Status }.Any(value => value.Length > 200))
            throw new ErpException(ErpError.InvalidQuery);
        var rows = await ExecuteAsync<PurchaseOrderHeader[]>(ErpOperation.SearchPurchaseOrders, search, token);
        if (rows.Length > 1000) throw new ErpException(ErpError.TooManyResults);
        if (rows.Any(row => row is null || string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.SupplierId))
            || rows.Select(row => row.Id).Distinct().Count() != rows.Length) throw new ErpException(ErpError.InvalidResult);
        return rows;
    }
    public async Task<PurchaseOrderSnapshot> ReadAsync(string orderId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(orderId) || orderId.Length > 200) throw new ErpException(ErpError.InvalidQuery);
        var config = await configuration.GetErpConfigurationAsync(token);
        var result = await ExecuteAsync<PurchaseOrderScriptResult>(ErpOperation.ReadPurchaseOrder, new PurchaseOrderReadRequest(orderId), token, config);
        return Snapshot(result, orderId, config);
    }
    internal static PurchaseOrderSnapshot Snapshot(PurchaseOrderScriptResult result, string id, ErpConfiguration config)
    {
        if (result.Header is null || result.Lines is null || result.Lines.Count > 10000 || result.Lines.Any(line => line is null)
            || result.Header.Id != id) throw new ErpException(ErpError.InvalidResult);
        var snapshot = new PurchaseOrderSnapshot(ProtectedErpCredentials.CatalogIdentity(config), result.Header, result.Revision, DateTime.UtcNow, result.Lines)
        { Financials = result.Financials, OrderTotal = result.OrderTotal };
        PurchaseOrderRules.Validate(snapshot); return snapshot;
    }
    private async Task<T> ExecuteAsync<T>(ErpOperation operation, object request, CancellationToken token, ErpConfiguration? config = null)
    {
        config ??= await configuration.GetErpConfigurationAsync(token);
        if (string.IsNullOrWhiteSpace(config.Server) || string.IsNullOrWhiteSpace(config.Database)) throw new ErpException(ErpError.NotConfigured);
        string sql = PurchaseOrderScripts.Get(config, operation);
        string password = config.Authentication == ErpAuthentication.SqlLogin
            ? await credentials.GetAsync(config, token) ?? throw new ErpException(ErpError.CredentialsUnavailable) : "";
        try
        {
            await using SqlConnection connection = new(ErpConnectionOptions.Create(config, password).ConnectionString);
            await connection.OpenAsync(token);
            if (operation == ErpOperation.ReadPurchaseOrder)
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
                var result = await RunAsync<T>(connection, transaction, sql, request, token);
                await transaction.CommitAsync(token); return result;
            }
            return await RunAsync<T>(connection, null, sql, request, token);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        { throw new ErpException(ErpError.QueryFailed); }
    }
    internal static async Task<T> RunAsync<T>(SqlConnection connection, SqlTransaction? transaction, string sql, object request, CancellationToken token, int? operatorCode = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(60)); token = budget.Token;
        await using SqlCommand command = new(sql, connection, transaction) { CommandTimeout = 60 };
        command.Parameters.Add("@RequestJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(request, Persistence.SqliteImportStore.JsonOptions);
        if (request is PurchaseOrderSearch search)
        {
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = search.From is { } from ? from.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = search.To is { } to ? to.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        }
        if (request is PurchaseOrderReadRequest read) command.Parameters.Add("@OrderId", SqlDbType.NVarChar, 200).Value = read.OrderId;
        if (request is BuyingSupplierReadRequest supplier)
        {
            command.Parameters.Add("@BrandId", SqlDbType.NVarChar, 200).Value = supplier.BrandId;
            command.Parameters.Add("@Branch", SqlDbType.NVarChar, 200).Value = supplier.Branch;
        }
        if (request is OrderWritePlan plan)
        {
            command.Parameters.Add("@OperationId", SqlDbType.UniqueIdentifier).Value = plan.OperationId;
            command.Parameters.Add("@OrderDate", SqlDbType.Date).Value = plan.Date.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@OperatorCode", SqlDbType.Int).Value = operatorCode is { } code ? code : DBNull.Value;
        }
        if (request is SynchronizationWritePlan synchronization)
        {
            command.Parameters.Add("@OperationId", SqlDbType.UniqueIdentifier).Value = synchronization.OperationId;
            command.Parameters.Add("@OperatorCode", SqlDbType.Int).Value = synchronization.Profile.OperatorCode is { } code ? code : DBNull.Value;
        }
        await using var reader = await command.ExecuteReaderAsync(token);
        return await PurchaseOrderScripts.ReadResultAsync<T>(reader, token);
    }
}

internal sealed record PurchaseOrderReadRequest(string OrderId);
internal sealed record BuyingSupplierReadRequest(string BrandId, string Branch);

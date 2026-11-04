using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Infrastructure.Erp;

public static class PurchaseOrderScripts
{
    private static readonly JsonSerializerOptions Options = new(Persistence.SqliteImportStore.JsonOptions)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    public static string Get(ErpConfiguration config, ErpOperation operation)
    {
        string sql = config.Queries.GetValueOrDefault(operation, "");
        if (string.IsNullOrWhiteSpace(sql)) throw new ErpException(ErpError.QueryMissing) { Operation = operation };
        if (sql.Length > 1_000_000) throw new ErpException(ErpError.InvalidQuery);
        return sql;
    }
    public static string Identity(ErpConfiguration config, OrderWriteKind kind)
    {
        if (!config.UseReadConnectionForWrites && string.IsNullOrWhiteSpace(config.WriteUserName)) throw new OrderException(OrderError.WritesNotConfigured);
        if (config.WriteOperatorCode is <= 0) throw new OrderException(OrderError.WritesNotConfigured);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            ProtectedErpCredentials.Identity(WriteConfiguration(config)),
            config.WriteOperatorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Get(config, kind == OrderWriteKind.Create ? ErpOperation.CreatePurchaseOrder : ErpOperation.UpdatePurchaseOrder),
            Get(config, ErpOperation.ReadPurchaseOrder), Get(config, ErpOperation.ReconcilePurchaseOperation)))));
    }
    public static ErpConfiguration WriteConfiguration(ErpConfiguration config) => config.UseReadConnectionForWrites
        ? config : config with { Authentication = ErpAuthentication.SqlLogin, UserName = config.WriteUserName };

    public static SynchronizationWriteProfile SynchronizationProfile(ErpConfiguration config, OrderWriteKind kind = OrderWriteKind.Update)
    {
        if (!config.UseReadConnectionForWrites && string.IsNullOrWhiteSpace(config.WriteUserName) || config.WriteOperatorCode is <= 0)
            throw new OrderException(OrderError.WritesNotConfigured);
        string financialSql = config.Queries.GetValueOrDefault(ErpOperation.PreviewPurchaseFinancials, "");
        if (!string.IsNullOrWhiteSpace(financialSql)) financialSql = Get(config, ErpOperation.PreviewPurchaseFinancials);
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            ProtectedErpCredentials.Identity(WriteConfiguration(config)), config.WriteOperatorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Get(config, kind == OrderWriteKind.Create ? ErpOperation.CreatePurchaseSynchronization : ErpOperation.UpdatePurchaseSynchronization),
            kind == OrderWriteKind.Create ? Get(config, ErpOperation.ReservePurchaseSynchronization) : "", Get(config, ErpOperation.ReconcilePurchaseSynchronization),
            Get(config, ErpOperation.ReadPurchaseOrder), Get(config, ErpOperation.FindBuyingSupplier), financialSql))));
        return new(identity, config.WriteOperatorCode) { Kind = kind, SupportsFinancialPolicies = !string.IsNullOrWhiteSpace(financialSql) };
    }

    public static async Task<T> ReadResultAsync<T>(DbDataReader reader, CancellationToken token)
    {
        string? json = null;
        do
        {
            if (reader.FieldCount == 0) continue;
            if (reader.FieldCount != 1 || reader.GetName(0) != "ResultJson" || json is not null
                || !await reader.ReadAsync(token) || reader.IsDBNull(0) || reader.GetValue(0) is not string value
                || value.Length > 4_000_000) throw new ErpException(ErpError.InvalidResult);
            json = value;
            if (await reader.ReadAsync(token)) throw new ErpException(ErpError.InvalidResult);
        } while (await reader.NextResultAsync(token));
        try { return JsonSerializer.Deserialize<T>(json ?? throw new ErpException(ErpError.InvalidResult), Options) ?? throw new ErpException(ErpError.InvalidResult); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException) { throw new ErpException(ErpError.InvalidResult); }
    }
}

public sealed record PurchaseOrderScriptResult(PurchaseOrderHeader Header, string Revision, IReadOnlyList<PurchaseOrderLine> Lines)
{
    public QuoteMatch.Domain.Importing.FinancialAmounts? Financials { get; init; }
    public decimal? OrderTotal { get; init; }
}

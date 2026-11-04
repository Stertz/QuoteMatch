namespace QuoteMatch.Domain.Integration;

public sealed record ErpProductCandidate(string ProductCode, string Name, string SimilarityCode, bool IsInactive,
    string SupplierId, string SupplierName, string SupplierProductCode, decimal? AlternativeQuantity, string AlternativeUnit,
    DateOnly? LastPurchaseDate, decimal? LastPurchaseUnitPrice, string? BaseUnit, string? LastPurchaseUnit,
    string CatalogIdentity, DateTime FetchedAtUtc)
{
    public ErpBrandIdentity? Brand { get; init; }
}

public enum ErpError { NotConfigured, CredentialsUnavailable, ConnectionFailed, QueryMissing, InvalidQuery, InvalidResult, TooManyResults, QueryFailed }
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ErpFailureStage { Configuration, Connection, Query, Result }
public sealed record ErpDiagnosticFailure(ErpFailureStage Stage, int? SqlErrorNumber = null, int? NativeErrorCode = null);
public sealed record ErpConnectionTestResult(bool Connected, ErpDiagnosticFailure? Failure = null);
public sealed class ErpException(ErpError code, ErpDiagnosticFailure? failure = null) : Exception($"ERP operation failed: {code}.")
{
    public ErpError Code { get; } = code;
    public ErpOperation? Operation { get; init; }
    public ErpDiagnosticFailure? Failure { get; } = failure;
}

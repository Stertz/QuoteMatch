namespace QuoteMatch.Domain.Integration;

public enum ErpAuthentication { Integrated, SqlLogin }
// Retain legacy operation names and numeric values so existing profiles remain readable.
public enum ErpOperation { FindProduct = 0, GetEquivalentProducts = 1, GetLastPurchase = 2, GetPurchaseOrderItems = 3, FindProductById = 4, SearchProducts = 5, SearchPurchaseOrders = 6, ReadPurchaseOrder = 7, CreatePurchaseOrder = 8, UpdatePurchaseOrder = 9, ReconcilePurchaseOperation = 10, FindBuyingSupplier = 11, UpdatePurchaseSynchronization = 12, ReconcilePurchaseSynchronization = 13, ReservePurchaseSynchronization = 14, CreatePurchaseSynchronization = 15, PreviewPurchaseFinancials = 16 }
public enum ProductReadMode { SingleQuery = 0, Script = 1 }
public sealed record ErpConfiguration
{
    public string Server { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
    public bool TrustServerCertificate { get; init; }
    public bool Encrypt { get; init; } = true;
    public int ConnectionTimeoutSeconds { get; init; } = 30;
    public ErpAuthentication Authentication { get; init; }
    public string UserName { get; init; } = string.Empty;
    public string WriteUserName { get; init; } = string.Empty;
    public bool UseReadConnectionForWrites { get; init; }
    public int? WriteOperatorCode { get; init; }
    public ProductReadMode ProductReadMode { get; init; }
    public Dictionary<ErpOperation, string> Queries { get; init; } = [];
}


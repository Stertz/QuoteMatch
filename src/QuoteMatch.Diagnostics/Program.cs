using System.Text.Json;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Infrastructure.Erp;
using QuoteMatch.Infrastructure.Persistence;

string? configured = Environment.GetEnvironmentVariable("QUOTEMATCH_DATA_DIRECTORY");
string directory = !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) ? configured
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuoteMatch");
using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
try
{
    SqliteImportStore store = new(directory);
    ErpGateway gateway = new(store, new ProtectedErpCredentials(directory));
    if (args is ["test-connection"])
    {
        var result = await gateway.TestSavedDetailedAsync(timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { Status = result.Connected ? "Connected" : "ConnectionFailed", result.Failure }));
        return result.Connected ? 0 : 1;
    }
    if (args is ["test-products", string similarity])
    {
        var rows = await gateway.FindBySimilarityAsync(similarity, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { Status = "ValidResult", RowCount = rows.Count })); return 0;
    }
    if (args is ["test-product-code", string code])
    {
        var rows = await gateway.FindByCodeAsync(code, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { Status = "ValidResult", RowCount = rows.Count })); return 0;
    }
    Console.WriteLine("Usage: QuoteMatch.Diagnostics test-connection | test-product-code <supplier-code> | test-products <similarity>"); return 2;
}
catch (ErpException exception) { Console.WriteLine(JsonSerializer.Serialize(new { Status = exception.Code.ToString(), exception.Failure })); return 1; }
catch (OperationCanceledException) { Console.WriteLine("{\"Status\":\"Canceled\"}"); return 1; }
catch (Exception) { Console.WriteLine("{\"Status\":\"Unavailable\"}"); return 1; }

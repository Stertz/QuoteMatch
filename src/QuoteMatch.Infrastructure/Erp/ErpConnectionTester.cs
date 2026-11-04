using Microsoft.Data.SqlClient;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Infrastructure.Erp;

public sealed class ErpConnectionTester
{
    public async Task<bool> TestAsync(ErpConfiguration configuration, string sessionPassword, CancellationToken cancellationToken) =>
        (await TestDetailedAsync(configuration, sessionPassword, cancellationToken)).Connected;

    public async Task<ErpConnectionTestResult> TestDetailedAsync(ErpConfiguration configuration, string sessionPassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.Server) || string.IsNullOrWhiteSpace(configuration.Database)) return new(false, new(ErpFailureStage.Configuration));
        SqlConnectionStringBuilder builder = ErpConnectionOptions.Create(configuration, sessionPassword);
        try
        {
            await using SqlConnection connection = new(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return new(true);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        { return new(false, ErpConnectionOptions.Failure(ErpFailureStage.Connection, exception)); }
    }
}


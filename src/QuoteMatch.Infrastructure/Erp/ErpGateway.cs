using System.Data;
using Microsoft.Data.SqlClient;
using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Integration;
namespace QuoteMatch.Infrastructure.Erp;

public sealed class ErpGateway(IErpConfigurationRepository configuration, IErpCredentials credentials) : IErpProductLookup, IErpProductSearch
{
    public async Task<string> GetCatalogIdentityAsync(CancellationToken cancellationToken) =>
        ProtectedErpCredentials.CatalogIdentity(await configuration.GetErpConfigurationAsync(cancellationToken));
    public async Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string productCode, CancellationToken cancellationToken)
    {
        var rows = await ExecuteReadAsync(ErpOperation.FindProductById, "@ProductCode", productCode, cancellationToken);
        if (rows.Any(row => !string.Equals(row.ProductCode, productCode.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ErpException(ErpError.InvalidResult, new(ErpFailureStage.Result));
        return rows;
    }
    public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken cancellationToken)
    {
        return ExecuteReadAsync(ErpOperation.SearchProducts, "@SearchText", ProductNameSearch.BuildPattern(name), cancellationToken);
    }
    public async Task SavePasswordAsync(ErpConfiguration config, string password, CancellationToken token) => await credentials.SaveAsync(config, password, token);
    public async Task<ErpConnectionTestResult> TestSavedDetailedAsync(CancellationToken token)
    {
        ErpConfiguration config = await configuration.GetErpConfigurationAsync(token);
        if (string.IsNullOrWhiteSpace(config.Server) || string.IsNullOrWhiteSpace(config.Database)) throw new ErpException(ErpError.NotConfigured);
        string? password = config.Authentication == ErpAuthentication.SqlLogin ? await credentials.GetAsync(config, token) : string.Empty;
        if (config.Authentication == ErpAuthentication.SqlLogin && password is null) throw new ErpException(ErpError.CredentialsUnavailable);
        return await new ErpConnectionTester().TestDetailedAsync(config, password ?? string.Empty, token);
    }
    public async Task<bool> TestAsync(ErpConfiguration config, string? sessionPassword, CancellationToken token) =>
        (await TestDetailedAsync(config, sessionPassword, token)).Connected;
    public async Task<ErpConnectionTestResult> TestDetailedAsync(ErpConfiguration config, string? sessionPassword, CancellationToken token)
    {
        string? password = config.Authentication == ErpAuthentication.Integrated ? string.Empty
            : !string.IsNullOrEmpty(sessionPassword) ? sessionPassword : await credentials.GetAsync(config, token);
        if (config.Authentication == ErpAuthentication.SqlLogin && password is null) throw new ErpException(ErpError.CredentialsUnavailable);
        return await new ErpConnectionTester().TestDetailedAsync(config, password ?? string.Empty, token);
    }
    public async Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string supplierProductCode, CancellationToken cancellationToken)
    {
        var products = await ExecuteReadAsync(ErpOperation.FindProduct, "@SupplierProductCode", supplierProductCode, cancellationToken);
        if (products.Any(product => !ErpProductIdentity.MatchesSupplierCode(product, supplierProductCode))) throw new ErpException(ErpError.InvalidResult, new(ErpFailureStage.Result));
        return products;
    }
    public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string similarityCode, CancellationToken cancellationToken) =>
        ExecuteReadAsync(ErpOperation.GetEquivalentProducts, "@SimilarityCode", similarityCode, cancellationToken);

    private async Task<IReadOnlyList<ErpProductCandidate>> ExecuteReadAsync(ErpOperation operation, string parameterName, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200) throw new ErpException(ErpError.InvalidQuery);
        ErpConfiguration config = await configuration.GetErpConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.Server) || string.IsNullOrWhiteSpace(config.Database)) throw new ErpException(ErpError.NotConfigured);
        string query = config.Queries.GetValueOrDefault(operation, string.Empty);
        if (string.IsNullOrWhiteSpace(query)) throw new ErpException(ErpError.QueryMissing);
        ProductReadQuery.Validate(query, parameterName, config.ProductReadMode);
        string password = string.Empty;
        if (config.Authentication == ErpAuthentication.SqlLogin)
        {
            password = await credentials.GetAsync(config, cancellationToken) ?? throw new ErpException(ErpError.CredentialsUnavailable);
        }
        SqlConnectionStringBuilder builder = ErpConnectionOptions.Create(config, password);
        bool connected = false;
        try
        {
            await using SqlConnection connection = new(builder.ConnectionString); await connection.OpenAsync(cancellationToken);
            connected = true;
            await using SqlCommand command = new(query, connection) { CommandTimeout = 15 };
            command.Parameters.Add(parameterName, SqlDbType.NVarChar, 200).Value = value.Trim();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(60));
            await using var reader = await command.ExecuteReaderAsync(budget.Token);
            return await ErpProductReader.ReadAsync(reader, ProtectedErpCredentials.CatalogIdentity(config), DateTime.UtcNow, budget.Token);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new ErpException(connected ? ErpError.QueryFailed : ErpError.ConnectionFailed,
            ErpConnectionOptions.Failure(connected ? ErpFailureStage.Query : ErpFailureStage.Connection, exception));
        }
        catch (Exception exception) when (exception is OverflowException or InvalidCastException)
        { throw new ErpException(ErpError.InvalidResult, new(ErpFailureStage.Result)); }
        catch (ErpException exception) when (exception.Code is ErpError.InvalidResult or ErpError.TooManyResults)
        { throw new ErpException(exception.Code, new(ErpFailureStage.Result)); }
    }
}


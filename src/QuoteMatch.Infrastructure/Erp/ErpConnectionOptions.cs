using Microsoft.Data.SqlClient;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Infrastructure.Erp;

public static class ErpConnectionOptions
{
    public static SqlConnectionStringBuilder Create(ErpConfiguration config, string password)
    {
        SqlConnectionStringBuilder builder = new()
        {
            DataSource = config.Server,
            InitialCatalog = config.Database,
            IntegratedSecurity = config.Authentication == ErpAuthentication.Integrated,
            Encrypt = config.Encrypt,
            TrustServerCertificate = config.TrustServerCertificate,
            ConnectTimeout = config.ConnectionTimeoutSeconds > 0 ? config.ConnectionTimeoutSeconds : 30,
            ApplicationName = "QuoteMatch",
            ApplicationIntent = ApplicationIntent.ReadOnly
        };
        if (!builder.IntegratedSecurity) { builder.UserID = config.UserName; builder.Password = password; }
        return builder;
    }

    internal static ErpDiagnosticFailure Failure(ErpFailureStage stage, Exception exception) => new(stage,
        (exception as SqlException)?.Number,
        exception.InnerException is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : null);
}

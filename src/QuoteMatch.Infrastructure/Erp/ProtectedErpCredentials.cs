using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Integration;
namespace QuoteMatch.Infrastructure.Erp;

public sealed class ProtectedErpCredentials(string directory, IUserSecretProtection? protection = null) : IErpCredentials
{
    private readonly IUserSecretProtection _protection = protection ?? new DpapiSecretProtection();
    private readonly ConcurrentDictionary<string, string> _session = new();
    public bool CanPersist => OperatingSystem.IsWindows();
    public static string Identity(ErpConfiguration configuration) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{configuration.Server.Trim().ToUpperInvariant()}\n{configuration.Database.Trim().ToUpperInvariant()}\n{configuration.Authentication}\n{configuration.UserName.Trim().ToUpperInvariant()}")));
    public static string CatalogIdentity(ErpConfiguration configuration) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{configuration.Server.Trim().ToUpperInvariant()}\n{configuration.Database.Trim().ToUpperInvariant()}")));
    public async Task SaveAsync(ErpConfiguration configuration, string password, CancellationToken cancellationToken)
    {
        string identity = Identity(configuration);
        if (!OperatingSystem.IsWindows()) { _session[identity] = password; return; }
        byte[] clear = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] encrypted = _protection.Protect(clear);
            string folder = Path.Combine(directory, "credentials"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, identity + ".bin");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException) { throw new ErpException(ErpError.CredentialsUnavailable); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public async Task<string?> GetAsync(ErpConfiguration configuration, CancellationToken cancellationToken)
    {
        string identity = Identity(configuration);
        if (!OperatingSystem.IsWindows()) return _session.GetValueOrDefault(identity);
        string path = Path.Combine(directory, "credentials", identity + ".bin"); if (!File.Exists(path)) return null;
        try
        {
            byte[] encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
            byte[] clear = _protection.Unprotect(encrypted);
            try { return Encoding.UTF8.GetString(clear); } finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException) { throw new ErpException(ErpError.CredentialsUnavailable); }
    }
}



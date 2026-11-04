using System.Security.Cryptography;
namespace QuoteMatch.Infrastructure.Erp;

public interface IUserSecretProtection
{
    byte[] Protect(byte[] clear);
    byte[] Unprotect(byte[] encrypted);
}
public sealed class DpapiSecretProtection : IUserSecretProtection
{
    public byte[] Protect(byte[] clear)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
    }
    public byte[] Unprotect(byte[] encrypted)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
    }
}

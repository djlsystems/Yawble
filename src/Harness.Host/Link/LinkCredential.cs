using Microsoft.AspNetCore.DataProtection;

namespace Harness.Host.Link;

/// <summary>
/// The link credential sealed with the Host's Data Protection keys (<c>&lt;dataRoot&gt;/keys</c>), under a
/// purpose of its own, so no other sealed value of the instance opens as it or it as one.
/// </summary>
public static class LinkCredential
{
    public const string Purpose = "Harness.Link.Credential";

    public static string Protect(IDataProtectionProvider protection, string credential) =>
        protection.CreateProtector(Purpose).Protect(credential);

    public static string Unprotect(IDataProtectionProvider protection, string cipher) =>
        protection.CreateProtector(Purpose).Unprotect(cipher);
}

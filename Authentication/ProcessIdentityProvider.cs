using System.Security.Principal;

namespace ExchangeIntegration.Authentication;

/// <summary>Reports the identity the process authenticates to Exchange with. Diagnostics only; no credentials are read.</summary>
public interface IProcessIdentityProvider
{
    string GetCurrentIdentityName();
}

public sealed class WindowsProcessIdentityProvider : IProcessIdentityProvider
{
    public string GetCurrentIdentityName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }

        return $"{Environment.UserDomainName}\\{Environment.UserName} (not a Windows host)";
    }
}

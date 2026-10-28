using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Configuration;

namespace ExchangeIntegration.Tests.TestSupport;

/// <summary>Serializes the tests that read or change process-wide environment variables.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>
/// Opt-in settings for the live Exchange tests. Nothing here runs unless EXCHANGE_LIVE_TESTS is set, so a normal
/// test run never contacts an Exchange server.
/// </summary>
public static class LiveExchangeSettings
{
    public const string EnableVariable = "EXCHANGE_LIVE_TESTS";
    public const string UnauthorizedMailboxVariable = "EXCHANGE_LIVE_TESTS_UNAUTHORIZED_MAILBOX";

    public static string? UnauthorizedMailbox => Environment.GetEnvironmentVariable(UnauthorizedMailboxVariable);

    public static string? GetSkipReason(bool requiresUnauthorizedMailbox)
    {
        var enabled = Environment.GetEnvironmentVariable(EnableVariable);
        if (!string.Equals(enabled, "1", StringComparison.Ordinal) && !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return $"Live Exchange tests are off. Set {EnableVariable}=1 together with Exchange__EwsUrl and Exchange__Mailbox for a customer-approved test mailbox.";
        }

        if (requiresUnauthorizedMailbox && string.IsNullOrWhiteSpace(UnauthorizedMailbox))
        {
            return $"Set {UnauthorizedMailboxVariable} to a mailbox that is approved for security testing and that the service identity must NOT be able to access.";
        }

        return null;
    }

    /// <summary>The Exchange settings exactly as the application reads them from the environment.</summary>
    public static ExchangeOptions LoadOptions()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        return configuration.GetSection(ExchangeOptions.SectionName).Get<ExchangeOptions>() ?? new ExchangeOptions();
    }
}

/// <summary>A fact that is skipped unless the live Exchange tests have been explicitly enabled.</summary>
public sealed class LiveExchangeFactAttribute : FactAttribute
{
    public bool RequiresUnauthorizedMailbox { get; set; }

    public override string? Skip
    {
        get => LiveExchangeSettings.GetSkipReason(RequiresUnauthorizedMailbox) ?? base.Skip;
        set => base.Skip = value;
    }
}

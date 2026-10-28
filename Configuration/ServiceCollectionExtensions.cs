using ExchangeIntegration.Authentication;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Exchange settings (validated at startup), the Windows-authenticated EWS client and the
    /// connectivity probe. Later milestones add their workers on top of this registration.
    /// </summary>
    public static IServiceCollection AddExchangeIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<ExchangeOptions>, ExchangeOptionsValidator>();
        services.AddOptions<ExchangeOptions>()
            .Bind(configuration.GetSection(ExchangeOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IProcessIdentityProvider, WindowsProcessIdentityProvider>();

        // One long-lived client: NTLM authenticates the connection, so connection reuse avoids repeated handshakes.
        services.AddSingleton(provider => ExchangeHttpClientFactory.Create(provider.GetRequiredService<IOptions<ExchangeOptions>>().Value));
        services.AddSingleton<IEwsClient, EwsClient>();
        services.AddSingleton<ExchangeConnectivityProbe>();

        return services;
    }
}

using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Authentication;
using ExchangeIntegration.Cloud;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Exchange settings (validated at startup), the Windows-authenticated EWS client and the
    /// connectivity probe. <see cref="AddExchangeAdapter"/> adds the service-mode workers on top of this registration.
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

    /// <summary>
    /// Service mode (--run): everything from <see cref="AddExchangeIntegration"/> plus the Azure settings, the
    /// Blob Storage and Service Bus clients (signed in with DefaultAzureCredential), the EWS mailbox and the two
    /// workers (Inbox polling and Engine commands).
    /// </summary>
    public static IServiceCollection AddExchangeAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddExchangeIntegration(configuration);
        services.AddSingleton<IValidateOptions<ExchangeOptions>, ServiceModeExchangeOptionsValidator>();

        services.AddSingleton<IValidateOptions<AzureOptions>, AzureOptionsValidator>();
        services.AddOptions<AzureOptions>()
            .Bind(configuration.GetSection(AzureOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.AddSingleton(provider =>
        {
            var azure = provider.GetRequiredService<IOptions<AzureOptions>>().Value;
            return new BlobServiceClient(new Uri(azure.BlobServiceUri), provider.GetRequiredService<TokenCredential>())
                .GetBlobContainerClient(azure.IntakeContainer);
        });

        services.AddSingleton(provider =>
        {
            var azure = provider.GetRequiredService<IOptions<AzureOptions>>().Value;
            return new ServiceBusClient(
                azure.ServiceBusNamespace,
                provider.GetRequiredService<TokenCredential>(),
                new ServiceBusClientOptions
                {
                    TransportType = azure.UseWebSockets ? ServiceBusTransportType.AmqpWebSockets : ServiceBusTransportType.AmqpTcp,
                });
        });

        services.AddKeyedSingleton(
            EwsMailbox.HttpClientKey,
            (provider, _) => ExchangeHttpClientFactory.CreateForAdapter(provider.GetRequiredService<IOptions<ExchangeOptions>>().Value));

        services.AddSingleton<IExchangeMailbox, EwsMailbox>();
        services.AddSingleton<IInvoiceStore, BlobInvoiceStore>();
        services.AddSingleton<IEngineQueues, ServiceBusEngineQueues>();
        services.AddSingleton<InvoiceIngestion>();
        services.AddSingleton<ExchangeCommandHandler>();
        services.AddHostedService<IngestionWorker>();
        services.AddHostedService<CommandWorker>();

        return services;
    }
}

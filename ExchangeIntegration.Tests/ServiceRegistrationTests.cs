using ExchangeIntegration.Authentication;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Tests;

/// <summary>Sets environment variables for the lifetime of a test and restores the originals afterwards.</summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _originals = [];

    public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            _originals[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, original) in _originals)
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public class ServiceRegistrationTests
{
    private static readonly Dictionary<string, string?> NoExchangeVariables = new()
    {
        ["Exchange__EwsUrl"] = null,
        ["Exchange__Mailbox"] = null,
        ["Exchange__AccessMode"] = null,
        ["Exchange__TimeoutSeconds"] = null,
    };

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExchangeIntegration(configuration);
        return services.BuildServiceProvider();
    }

    // The same call Program.cs uses, so these tests cover appsettings.json, environment variables and arguments.
    private static IHost BuildHost(params string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddExchangeIntegration(builder.Configuration);
        return builder.Build();
    }

    [Fact]
    public void Valid_settings_resolve_the_probe_with_the_windows_authenticated_client()
    {
        using var provider = BuildProvider(new()
        {
            ["Exchange:EwsUrl"] = "https://mail.contoso.com/EWS/Exchange.asmx",
            ["Exchange:Mailbox"] = "ap-invoices@contoso.com",
        });

        Assert.NotNull(provider.GetRequiredService<ExchangeConnectivityProbe>());
        Assert.IsType<EwsClient>(provider.GetRequiredService<IEwsClient>());
        Assert.IsType<WindowsProcessIdentityProvider>(provider.GetRequiredService<IProcessIdentityProvider>());
    }

    [Fact]
    public void Invalid_settings_fail_validation_as_soon_as_the_options_are_read()
    {
        using var provider = BuildProvider(new()
        {
            ["Exchange:EwsUrl"] = "http://mail.contoso.com/EWS/Exchange.asmx",
            ["Exchange:Mailbox"] = "not an address",
        });

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeOptions>>().Value);

        Assert.True(exception.Failures.Count() >= 2);
    }

    [Fact]
    public void Missing_settings_fail_validation()
    {
        using var provider = BuildProvider([]);

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeOptions>>().Value);
    }

    [Fact]
    public void The_shipped_appsettings_json_binds_to_valid_options()
    {
        using var environment = new EnvironmentVariableScope(NoExchangeVariables);
        using var host = BuildHost();

        var options = host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;

        Assert.Empty(ExchangeOptionsValidator.GetFailures(options));
        Assert.Equal(ExchangeAccessMode.Delegated, options.AccessMode);
        Assert.Equal(ExchangeOptions.DefaultTimeoutSeconds, options.TimeoutSeconds);
        Assert.StartsWith("https://", options.EwsUrl);
    }

    [Fact]
    public void Environment_variables_override_appsettings_json_without_code_changes()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__EwsUrl"] = "https://exchange.example.test/EWS/Exchange.asmx",
            ["Exchange__Mailbox"] = "approved-test@example.test",
            ["Exchange__TimeoutSeconds"] = "12",
        });
        using var host = BuildHost();

        var options = host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;

        Assert.Equal("https://exchange.example.test/EWS/Exchange.asmx", options.EwsUrl);
        Assert.Equal("approved-test@example.test", options.Mailbox);
        Assert.Equal(12, options.TimeoutSeconds);
    }

    [Fact]
    public void Command_line_arguments_override_the_other_sources()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__Mailbox"] = "from-environment@example.test",
        });
        using var host = BuildHost("--Exchange:Mailbox=from-arguments@example.test");

        var options = host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;

        Assert.Equal("from-arguments@example.test", options.Mailbox);
    }

    [Fact]
    public void A_value_that_cannot_be_converted_surfaces_as_a_configuration_error()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__AccessMode"] = "NotAMode",
        });
        using var host = BuildHost();

        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value);
    }

    [Fact]
    public void Impersonation_is_rejected_by_startup_validation()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__AccessMode"] = "Impersonation",
        });
        using var host = BuildHost();

        var exception = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("not supported in this milestone"));
    }
}

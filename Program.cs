using ExchangeIntegration.Authentication;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

const string ProbeSwitch = "--probe";
const string RunSwitch = "--run";
const string LocalSwitch = "--local";
const int UsageExitCode = 64;
const int ConfigurationExitCode = (int)FailureCategory.Configuration;

if (args.Any(IsHelpSwitch))
{
    WriteUsage(Console.Out);
    return 0;
}

string[] modeSwitches = [ProbeSwitch, RunSwitch, LocalSwitch];
var modes = modeSwitches.Where(mode => args.Any(arg => string.Equals(arg, mode, StringComparison.OrdinalIgnoreCase))).ToList();
if (modes.Count != 1)
{
    Console.Error.WriteLine(modes.Count > 1 ? "Specify only one of --probe, --run or --local." : "No command was specified.");
    Console.Error.WriteLine();
    WriteUsage(Console.Error);
    return UsageExitCode;
}

// The command switch is not configuration; everything else (for example --Exchange:Mailbox=...) goes to the host.
var configurationArgs = args
    .Where(arg => !modeSwitches.Contains(arg, StringComparer.OrdinalIgnoreCase))
    .ToArray();

// Resolving appsettings.json against the binary directory keeps it found when the working directory differs,
// which is also what a Windows service needs (its working directory is System32).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = configurationArgs,
    ContentRootPath = AppContext.BaseDirectory,
});

// Logs go to stderr so that stdout carries only the probe report and can be captured on its own.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
    options.IncludeScopes = true;
});
builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

if (modes[0] != ProbeSwitch)
{
    return await RunServiceAsync(builder, local: modes[0] == LocalSwitch);
}

builder.Services.AddExchangeIntegration(builder.Configuration);

using var host = builder.Build();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var identity = host.Services.GetRequiredService<IProcessIdentityProvider>().GetCurrentIdentityName();

    // Reading the options runs the startup validation, so invalid settings stop here before any request is made.
    try
    {
        _ = host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;
    }
    catch (OptionsValidationException exception)
    {
        return Write(ConnectivityResult.ForConfigurationFailure(identity, exception.Failures));
    }
    catch (InvalidOperationException exception)
    {
        // The configuration binder could not convert a value, for example a non-numeric Exchange:TimeoutSeconds.
        return Write(ConnectivityResult.ForConfigurationFailure(identity, [exception.Message]));
    }

    var probe = host.Services.GetRequiredService<ExchangeConnectivityProbe>();
    return Write(await probe.RunAsync(cancellation.Token));
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Unexpected error: {exception.GetType().Name}: {Redaction.SanitizeMessage(exception.Message)}");
    return (int)FailureCategory.Unexpected;
}

static bool IsHelpSwitch(string arg) => arg is "--help" or "-h" or "-?" or "/?";

// Service mode: poll the Inbox and process Engine commands until Ctrl+C or a Windows service stop.
// Local mode is the same with folders in place of Exchange and Azure.
static async Task<int> RunServiceAsync(HostApplicationBuilder builder, bool local)
{
    try
    {
        if (local)
        {
            builder.Services.AddLocalExchangeAdapter(builder.Configuration);
        }
        else
        {
            // Under the Service Control Manager this adds the Windows Event Log (source "ExchangeIntegration") and
            // service lifetime; in a console it does nothing.
            builder.Services.AddWindowsService(options => options.ServiceName = "ExchangeIntegration");
            builder.Services.AddExchangeAdapter(builder.Configuration);
        }

        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }
    catch (Exception exception) when (ConfigurationFailures(exception) is { Count: > 0 } failures)
    {
        Console.Error.WriteLine("Configuration errors:");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"  - {Redaction.SanitizeMessage(failure)}");
        }

        return ConfigurationExitCode;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Unexpected error: {exception.GetType().Name}: {Redaction.SanitizeMessage(exception.Message)}");
        return (int)FailureCategory.Unexpected;
    }
}

static List<string> ConfigurationFailures(Exception exception) => exception switch
{
    OptionsValidationException validation => validation.Failures.ToList(),
    AggregateException aggregate => aggregate.Flatten().InnerExceptions.SelectMany(ConfigurationFailures).ToList(),
    _ => [],
};

static int Write(ConnectivityResult result)
{
    Console.Out.Write(ConnectivityReport.Render(result));
    return result.ExitCode;
}

static void WriteUsage(TextWriter writer)
{
    writer.WriteLine("Exchange Integration - EWS adapter between on-premises Exchange mailboxes and Azure");
    writer.WriteLine();
    writer.WriteLine("Usage:");
    writer.WriteLine("  ExchangeIntegration --probe [--Exchange:EwsUrl=<url>] [--Exchange:Mailbox=<address>]");
    writer.WriteLine("  ExchangeIntegration --run   [--<Section>:<Key>=<value> ...]");
    writer.WriteLine("  ExchangeIntegration --local [--Local:RootPath=<folder>] [--<Section>:<Key>=<value> ...]");
    writer.WriteLine();
    writer.WriteLine("Options:");
    writer.WriteLine("  --probe   Test mode: run the one-time read-only connectivity test and exit. The exit code is 0 only on success.");
    writer.WriteLine("  --run     Service mode: capture new Inbox emails to Azure and carry out Engine commands until stopped.");
    writer.WriteLine("            This is the mode to register as a Windows service.");
    writer.WriteLine("  --local   Local mode: service mode against folders instead of Exchange and Azure, for development.");
    writer.WriteLine("            Mailboxes, blobs and queues live under Local:RootPath (default: local-data in the current directory).");
    writer.WriteLine("  --help    Show this help.");
    writer.WriteLine();
    writer.WriteLine("Settings come from appsettings.json and can be overridden with environment variables such as");
    writer.WriteLine("Exchange__EwsUrl, Exchange__Mailbox and Azure__BlobServiceUri, or with --<Section>:<Key>=<value> arguments.");
    writer.WriteLine("Service mode also accepts Exchange:Mailboxes, a list of independent Exchange configurations.");
    writer.WriteLine("For example, Exchange__Mailboxes__0__Mailbox and Exchange__Mailboxes__0__EwsUrl configure the first.");
    writer.WriteLine("Test mode still probes only Exchange:Mailbox using Exchange:EwsUrl; it ignores the mailbox list.");
    writer.WriteLine("See README.md for the exit codes and troubleshooting.");
}

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
const int UsageExitCode = 64;

if (args.Any(IsHelpSwitch))
{
    WriteUsage(Console.Out);
    return 0;
}

if (!args.Any(arg => string.Equals(arg, ProbeSwitch, StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("No command was specified.");
    Console.Error.WriteLine();
    WriteUsage(Console.Error);
    return UsageExitCode;
}

// The command switch is not configuration; everything else (for example --Exchange:Mailbox=...) goes to the host.
var configurationArgs = args
    .Where(arg => !string.Equals(arg, ProbeSwitch, StringComparison.OrdinalIgnoreCase))
    .ToArray();

// Resolving appsettings.json against the binary directory keeps it found when the working directory differs,
// which is also what a Windows service needs later (its working directory is System32).
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
});
builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Later milestones register their BackgroundService workers here and call AddWindowsService() once the
// application runs as a Windows service.
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

static int Write(ConnectivityResult result)
{
    Console.Out.Write(ConnectivityReport.Render(result));
    return result.ExitCode;
}

static void WriteUsage(TextWriter writer)
{
    writer.WriteLine("Exchange Integration - read-only EWS connectivity and Windows authentication probe");
    writer.WriteLine();
    writer.WriteLine("Usage:");
    writer.WriteLine("  ExchangeIntegration --probe [--Exchange:EwsUrl=<url>] [--Exchange:Mailbox=<address>]");
    writer.WriteLine();
    writer.WriteLine("Options:");
    writer.WriteLine("  --probe   Run the one-time connectivity test and exit. The exit code is 0 only on success.");
    writer.WriteLine("  --help    Show this help.");
    writer.WriteLine();
    writer.WriteLine("Settings come from appsettings.json and can be overridden with environment variables such as");
    writer.WriteLine("Exchange__EwsUrl and Exchange__Mailbox, or with the --Exchange:<Key>=<value> arguments above.");
    writer.WriteLine("See README.md for the exit codes and troubleshooting.");
}

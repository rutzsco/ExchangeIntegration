using System.Text;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ExchangeIntegration.Local;

/// <summary>
/// Runs before the pollers in local mode: creates every folder (including each mailbox's move folders, which Exchange
/// requires to exist) and puts a sample invoice email in the Inbox of each mailbox folder it creates.
/// </summary>
public sealed class LocalDataSetup : IHostedService
{
    public const string SampleFileName = "sample-invoice.eml";

    private readonly LocalFolders _folders;
    private readonly IEnumerable<ConfiguredMailbox> _mailboxes;
    private readonly AzureOptions _azure;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalDataSetup> _logger;

    public LocalDataSetup(
        LocalFolders folders,
        IEnumerable<ConfiguredMailbox> mailboxes,
        IOptions<AzureOptions> azure,
        TimeProvider time,
        ILogger<LocalDataSetup> logger)
    {
        _folders = folders;
        _mailboxes = mailboxes;
        _azure = azure.Value;
        _time = time;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Local mode: folders stand in for Exchange, Blob Storage and Service Bus. Data folder: {Root}", _folders.Root);

        foreach (var mailbox in _mailboxes)
        {
            var folder = _folders.Mailbox(mailbox.Options.Mailbox);
            var isNew = !Directory.Exists(folder);
            var inbox = Directory.CreateDirectory(Path.Combine(folder, FolderMailbox.InboxFolder)).FullName;
            foreach (var moveFolder in mailbox.Options.GetMoveFolders())
            {
                Directory.CreateDirectory(Path.Combine(folder, moveFolder.Trim()));
            }

            if (isNew)
            {
                WriteSampleEmail(Path.Combine(inbox, SampleFileName), mailbox.Options.Mailbox, _time.GetUtcNow());
            }

            _logger.LogInformation("Mailbox {Mailbox}: put .eml files in {Inbox}", mailbox.Options.Mailbox, inbox);
        }

        Directory.CreateDirectory(_folders.Container(_azure.IntakeContainer));
        Directory.CreateDirectory(_folders.Queue(_azure.InvoiceReceivedQueue));
        Directory.CreateDirectory(_folders.Queue(_azure.CommandsQueue));
        Directory.CreateDirectory(_folders.Queue(_azure.CommandResultsQueue));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static void WriteSampleEmail(string path, string mailbox, DateTimeOffset date)
    {
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Example Supplier", "billing@example.test"));
        message.To.Add(new MailboxAddress(null, mailbox.Trim()));
        message.Subject = "Invoice 1001";
        message.Date = date;
        message.MessageId = "sample-invoice-1001@example.test";

        var body = new BodyBuilder { TextBody = "Please find invoice 1001 attached." };
        body.Attachments.Add(
            "invoice-1001.csv",
            Encoding.UTF8.GetBytes("Item,Quantity,UnitPrice\r\nWidget,10,4.50\r\n"),
            new ContentType("text", "csv"));
        message.Body = body.ToMessageBody();
        message.WriteTo(path);
    }
}

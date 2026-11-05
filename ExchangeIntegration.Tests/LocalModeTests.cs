using System.Text.Json;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Local;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ExchangeIntegration.Tests;

public sealed class LocalModeTests : IDisposable
{
    private const string Mailbox = "ap-invoices@contoso.com";
    private const string SampleMessageId = "<sample-invoice-1001@example.test>";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ei-local-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FolderMailbox CreateMailbox(params string[] folders)
    {
        var inbox = Directory.CreateDirectory(Path.Combine(_root, FolderMailbox.InboxFolder)).FullName;
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(_root, folder));
        }

        LocalDataSetup.WriteSampleEmail(Path.Combine(inbox, LocalDataSetup.SampleFileName), Mailbox, Now);
        return new FolderMailbox(_root, Mailbox, new FixedTimeProvider(Now.AddHours(1)));
    }

    [Fact]
    public async Task A_folder_mailbox_reads_eml_files_like_an_inbox()
    {
        var mailbox = CreateMailbox();

        var item = Assert.Single(await mailbox.ListInboxAsync(CancellationToken.None));
        var message = await mailbox.GetMessageAsync(item.ItemId, CancellationToken.None);
        var attachment = Assert.Single(message.Attachments);
        var content = await mailbox.GetAttachmentContentAsync(attachment.AttachmentId!, CancellationToken.None);

        Assert.Equal(new InboxItem(LocalDataSetup.SampleFileName, "IPM.Note", SampleMessageId, Now), item);
        Assert.Equal(SampleMessageId, message.InternetMessageId);
        Assert.Equal("Invoice 1001", message.Subject);
        Assert.Equal("billing@example.test", message.From!.Address);
        Assert.Equal(Now, message.ReceivedUtc);
        Assert.Null(message.AutoSubmitted);
        Assert.Equal(new EmailAttachment($"{item.ItemId}|0", "invoice-1001.csv", "text/csv", content.Length, false, true), attachment);
        Assert.StartsWith("Item,Quantity", System.Text.Encoding.UTF8.GetString(content));
        Assert.Equal([item.ItemId], await mailbox.FindInboxItemIdsAsync(SampleMessageId, CancellationToken.None));
        Assert.Empty(await mailbox.FindInboxItemIdsAsync("<other@example.test>", CancellationToken.None));
    }

    [Fact]
    public async Task A_reply_is_written_to_sent_items_as_a_threaded_email()
    {
        var mailbox = CreateMailbox();
        var original = await mailbox.GetMessageAsync(LocalDataSetup.SampleFileName, CancellationToken.None);

        await mailbox.SendReplyAsync(original, "Received, thank you.", CancellationToken.None);

        var sent = Assert.Single(Directory.GetFiles(Path.Combine(_root, FolderMailbox.SentItemsFolder), "*.eml"));
        using var reply = MimeMessage.Load(sent);
        Assert.Equal("RE: Invoice 1001", reply.Subject);
        Assert.Equal(Mailbox, reply.From.Mailboxes.Single().Address);
        Assert.Equal("billing@example.test", reply.To.Mailboxes.Single().Address);
        Assert.Equal("sample-invoice-1001@example.test", reply.InReplyTo);
        Assert.Equal("Received, thank you.", reply.TextBody?.Trim());
    }

    [Fact]
    public async Task A_move_needs_an_existing_folder_and_removes_the_email_from_the_inbox()
    {
        var mailbox = CreateMailbox("Processed");

        var missing = await Assert.ThrowsAsync<EwsOperationException>(
            () => mailbox.MoveAsync([LocalDataSetup.SampleFileName], "Archive", CancellationToken.None));
        await mailbox.MoveAsync([LocalDataSetup.SampleFileName], "Processed", CancellationToken.None);
        var gone = await Assert.ThrowsAsync<EwsOperationException>(
            () => mailbox.GetMessageAsync(LocalDataSetup.SampleFileName, CancellationToken.None));

        Assert.Equal("ErrorFolderNotFound", missing.ResponseCode);
        Assert.True(File.Exists(Path.Combine(_root, "Processed", LocalDataSetup.SampleFileName)));
        Assert.Empty(await mailbox.ListInboxAsync(CancellationToken.None));
        Assert.Equal("ErrorItemNotFound", gone.ResponseCode);
    }

    [Theory]
    [InlineData(@"..\Processed\sample-invoice.eml")]
    [InlineData("../sample-invoice.eml")]
    [InlineData("missing.eml")]
    public async Task Item_ids_outside_the_inbox_are_not_found(string itemId)
    {
        var mailbox = CreateMailbox("Processed");

        var exception = await Assert.ThrowsAsync<EwsOperationException>(() => mailbox.GetMessageAsync(itemId, CancellationToken.None));

        Assert.Equal("ErrorItemNotFound", exception.ResponseCode);
    }

    [Fact]
    public async Task The_file_store_keeps_the_announced_checkpoint_beside_the_manifest()
    {
        var store = new FileInvoiceStore(_root);
        var manifest = BlobPaths.Manifest("key");

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.MarkAnnouncedAsync("key", Now, CancellationToken.None));
        var uri = await store.UploadAsync(manifest, BinaryData.FromString("{}"), "application/json", CancellationToken.None);
        Assert.False(await store.IsAnnouncedAsync("key", CancellationToken.None));
        await store.MarkAnnouncedAsync("key", Now, CancellationToken.None);
        Assert.True(await store.IsAnnouncedAsync("key", CancellationToken.None));
        await store.UploadAsync(manifest, BinaryData.FromString("{ }"), "application/json", CancellationToken.None);

        Assert.False(await store.IsAnnouncedAsync("key", CancellationToken.None));
        Assert.Equal("{ }", (await store.DownloadAsync(manifest, CancellationToken.None))!.ToString());
        Assert.Equal(Path.Combine(_root, "key", "manifest.json"), uri.LocalPath);
        Assert.Null(await store.DownloadAsync("key/missing.json", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UploadAsync("../escape.json", BinaryData.FromString("{}"), "application/json", CancellationToken.None));
    }

    [Fact]
    public async Task Local_mode_needs_no_exchange_endpoint_or_azure_settings()
    {
        await using var provider = LocalServices(new() { ["Exchange:Mailbox"] = Mailbox }).BuildServiceProvider();

        var mailbox = Assert.Single(provider.GetServices<ConfiguredMailbox>());
        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.IsType<FolderMailbox>(mailbox.Client);
        Assert.IsType<FileInvoiceStore>(provider.GetRequiredService<IInvoiceStore>());
        Assert.IsType<FileEngineQueues>(provider.GetRequiredService<IEngineQueues>());
        Assert.IsType<LocalDataSetup>(hosted[0]);
        Assert.Single(hosted.OfType<IngestionWorker>());
        Assert.Single(hosted.OfType<LocalCommandWorker>());
        Assert.Empty(hosted.OfType<CommandWorker>());
        Assert.Equal(_root, provider.GetRequiredService<LocalFolders>().Root);
    }

    [Fact]
    public async Task Local_mode_still_validates_mailboxes_folders_and_names()
    {
        await using var provider = LocalServices(new()
        {
            ["Exchange:Mailbox"] = "not an address",
            ["Exchange:MoveFolders:0"] = "..",
            ["Azure:CommandsQueue"] = "Bad_Name",
        }).BuildServiceProvider();

        var exchange = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value);
        var azure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureOptions>>().Value);

        Assert.Equal(2, exchange.Failures.Count());
        Assert.Contains(exchange.Failures, failure => failure.StartsWith("Exchange:Mailbox must be a plain SMTP address"));
        Assert.Contains(exchange.Failures, failure => failure.StartsWith("Exchange:MoveFolders entries must be valid folder names"));
        Assert.StartsWith("Azure:CommandsQueue", Assert.Single(azure.Failures));
    }

    [Fact]
    public async Task The_sample_email_is_captured_and_a_command_file_moves_it()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Exchange:Mailbox"] = Mailbox,
            [LocalFolders.RootPathSetting] = _root,
        });
        builder.Services.AddLocalExchangeAdapter(builder.Configuration);
        using var host = builder.Build();
        var folders = host.Services.GetRequiredService<LocalFolders>();
        var key = MessageKeys.Compute(Mailbox, SampleMessageId);

        await host.StartAsync();
        try
        {
            var announced = Path.Combine(folders.Queue("invoice-received"), key + ".json");
            await WaitForAsync(() => File.Exists(announced));
            var received = JsonSerializer.Deserialize<InvoiceReceivedMessage>(File.ReadAllText(announced), AdapterJson.Options)!;
            var manifest = JsonSerializer.Deserialize<InvoiceManifest>(
                File.ReadAllText(Path.Combine(folders.Container("invoice-intake"), key, "manifest.json")), AdapterJson.Options)!;

            Assert.Equal(SampleMessageId, received.InternetMessageId);
            Assert.True(File.Exists(received.Manifest.LocalPath));
            Assert.Equal("invoice-1001.csv", Assert.Single(manifest.Files).Name);
            Assert.True(File.Exists(Path.Combine(folders.Container("invoice-intake"), Assert.Single(manifest.Files).Blob)));

            var commands = folders.Queue("exchange-commands");
            File.WriteAllText(Path.Combine(commands, "move.json"), JsonSerializer.Serialize(new ExchangeCommand
            {
                Type = MessageTypes.Move,
                CommandId = key + ".move-1",
                MessageKey = key,
                Mailbox = Mailbox,
                InternetMessageId = SampleMessageId,
                Move = new MoveDetails { Folder = "Processed" },
            }, AdapterJson.Options));
            File.WriteAllText(Path.Combine(commands, "zz-bad.json"), "not json");

            await WaitForAsync(() => File.Exists(Path.Combine(commands, LocalCommandWorker.DeadLetterFolder, "zz-bad.json")));
            var result = JsonSerializer.Deserialize<CommandResultMessage>(
                File.ReadAllText(Assert.Single(Directory.GetFiles(folders.Queue("exchange-command-results"), "*.json"))), AdapterJson.Options)!;

            Assert.True(File.Exists(Path.Combine(commands, LocalCommandWorker.ProcessedFolder, "move.json")));
            Assert.Equal(CommandResultMessage.Succeeded, result.Status);
            Assert.True(File.Exists(Path.Combine(folders.Mailbox(Mailbox), "Processed", LocalDataSetup.SampleFileName)));
            Assert.Empty(Directory.GetFiles(Path.Combine(folders.Mailbox(Mailbox), FolderMailbox.InboxFolder)));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private ServiceCollection LocalServices(Dictionary<string, string?> settings)
    {
        settings[LocalFolders.RootPathSetting] = _root;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalExchangeAdapter(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
}

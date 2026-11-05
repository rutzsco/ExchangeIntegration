namespace ExchangeIntegration.Local;

/// <summary>
/// The folder layout of local mode (--local) under Local:RootPath:
/// mailboxes/&lt;address&gt;/Inbox, Sent Items and the move folders; blobs/&lt;container&gt;; queues/&lt;queue&gt;.
/// </summary>
public sealed class LocalFolders
{
    public const string RootPathSetting = "Local:RootPath";
    public const string DefaultRootPath = "local-data";

    /// <param name="rootPath">Relative paths resolve against the current directory.</param>
    public LocalFolders(string? rootPath)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath) ? DefaultRootPath : rootPath.Trim());
    }

    public string Root { get; }

    public string Mailbox(string address) => Path.Combine(Root, "mailboxes", SafeName(address.Trim()));

    public string Container(string name) => Path.Combine(Root, "blobs", name);

    public string Queue(string name) => Path.Combine(Root, "queues", name);

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

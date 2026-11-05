using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Configuration;

/// <summary>Service-mode mailboxes, or the legacy single Exchange configuration when no list is supplied.</summary>
public sealed class ExchangeMailboxesOptions
{
    public ExchangeOptions[] Mailboxes { get; set; } = [];

    internal string[] ConfigurationPaths { get; set; } = [];
}

/// <param name="localMode">Local mode (--local) needs no EWS settings, and move folders must be valid folder names.</param>
public sealed class ExchangeMailboxesOptionsValidator(bool localMode = false) : IValidateOptions<ExchangeMailboxesOptions>
{
    public ValidateOptionsResult Validate(string? name, ExchangeMailboxesOptions options)
    {
        var failures = new List<string>();
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.Mailboxes.Length == 0)
        {
            failures.Add("Exchange:Mailboxes must contain at least one mailbox configuration.");
        }

        for (var index = 0; index < options.Mailboxes.Length; index++)
        {
            var mailbox = options.Mailboxes[index];
            var path = index < options.ConfigurationPaths.Length ? options.ConfigurationPaths[index] : $"Exchange:Mailboxes:{index}";
            failures.AddRange(
                (localMode ? GetLocalFailures(mailbox) : ExchangeOptionsValidator.GetFailures(mailbox))
                    .Concat(ExchangeOptionsValidator.GetServiceModeFailures(mailbox))
                    .Select(failure => failure.Replace("Exchange:", path + ":", StringComparison.Ordinal)));

            if (!string.IsNullOrWhiteSpace(mailbox.Mailbox) && !addresses.Add(mailbox.Mailbox.Trim()))
            {
                failures.Add($"{path}:Mailbox duplicates another configured mailbox; each SMTP address must be unique (case-insensitive).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static IEnumerable<string> GetLocalFailures(ExchangeOptions mailbox)
    {
        var failures = ExchangeOptionsValidator.GetMailboxFailures(mailbox).ToList();
        if ((mailbox.MoveFolders ?? []).Any(folder => folder is not null && (folder.Trim() is "." or ".." || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)))
        {
            failures.Add("Exchange:MoveFolders entries must be valid folder names in local mode.");
        }

        return failures;
    }
}

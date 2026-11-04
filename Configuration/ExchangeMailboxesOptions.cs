using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Configuration;

/// <summary>Service-mode mailboxes, or the legacy single Exchange configuration when no list is supplied.</summary>
public sealed class ExchangeMailboxesOptions
{
    public ExchangeOptions[] Mailboxes { get; set; } = [];

    internal string[] ConfigurationPaths { get; set; } = [];
}

public sealed class ExchangeMailboxesOptionsValidator : IValidateOptions<ExchangeMailboxesOptions>
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
                ExchangeOptionsValidator.GetFailures(mailbox)
                    .Concat(ExchangeOptionsValidator.GetServiceModeFailures(mailbox))
                    .Select(failure => failure.Replace("Exchange:", path + ":", StringComparison.Ordinal)));

            if (!string.IsNullOrWhiteSpace(mailbox.Mailbox) && !addresses.Add(mailbox.Mailbox.Trim()))
            {
                failures.Add($"{path}:Mailbox duplicates another configured mailbox; each SMTP address must be unique (case-insensitive).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

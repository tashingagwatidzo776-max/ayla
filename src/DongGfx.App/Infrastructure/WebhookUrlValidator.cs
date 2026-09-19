namespace DongGfx.App.Infrastructure;

/// <summary>Outcome of validating a webhook URL: no issue, a blocking
/// error (cannot work), or a non-blocking warning (probably a mismatch).</summary>
public enum WebhookUrlSeverity
{
    None,
    Warning,
    Error,
}

/// <summary>
/// Pure, headless validation for the Settings tab's webhook URL field.
/// Runs as the user types so a bad URL is flagged before any save or test
/// post: scheme must be https (localhost/127.0.0.1 may use http for local
/// test listeners), and the host is cross-checked against the selected
/// platform format (Discord embeds vs Slack attachments).
/// </summary>
public static class WebhookUrlValidator
{
    /// <summary>Validates <paramref name="url"/> against the selected
    /// platform. Empty input is valid (webhook is optional).</summary>
    public static (WebhookUrlSeverity Severity, string Message) Validate(
        string? url, bool isDiscord)
    {
        var trimmed = url?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return (WebhookUrlSeverity.None, "");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            return (WebhookUrlSeverity.Error,
                "Not a valid webhook URL — expected an https:// address");
        }

        var isLocal = uri.Scheme == "http"
            && (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "::1");
        if (uri.Scheme != "https" && !isLocal)
        {
            return (WebhookUrlSeverity.Error,
                "Webhook must use https (http is only allowed for localhost test servers)");
        }

        var host = uri.Host.ToLowerInvariant();
        var isDiscordHost = host is "discord.com" or "discordapp.com"
            && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
        var isSlackHost = host == "hooks.slack.com";

        if (isDiscord && !isDiscordHost)
        {
            return (WebhookUrlSeverity.Warning, isSlackHost
                ? "This looks like a Slack webhook but Discord format is selected"
                : $"Host '{host}' is not a recognized Discord webhook host");
        }

        if (!isDiscord && !isSlackHost)
        {
            return (WebhookUrlSeverity.Warning, isDiscordHost
                ? "This looks like a Discord webhook but Slack format is selected"
                : $"Host '{host}' is not a recognized Slack webhook host");
        }

        return (WebhookUrlSeverity.None, "");
    }
}

using Tf.App.Infrastructure;

namespace Tf.App.Tests;

/// <summary>
/// Tests for the live webhook URL validation: scheme enforcement (https,
/// localhost http exception), platform/host cross-checks, and the empty
/// input pass-through (webhook is optional).
/// </summary>
[Trait("Category", "Unit")]
public class WebhookUrlValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_Empty_IsOptionalAndValid(string? url)
    {
        var (severity, message) = WebhookUrlValidator.Validate(url, isDiscord: true);

        Assert.Equal(WebhookUrlSeverity.None, severity);
        Assert.Equal("", message);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://discord.com/api/webhooks/1/abc")]
    [InlineData("discord.com/api/webhooks/1/abc")] // no scheme
    public void Validate_NotHttpUrl_IsError(string url)
    {
        var (severity, message) = WebhookUrlValidator.Validate(url, isDiscord: true);

        Assert.Equal(WebhookUrlSeverity.Error, severity);
        Assert.Contains("https", message);
    }

    [Fact]
    public void Validate_PlainHttpRemote_IsError()
    {
        var (severity, message) = WebhookUrlValidator.Validate(
            "http://example.com/hook", isDiscord: true);

        Assert.Equal(WebhookUrlSeverity.Error, severity);
        Assert.Contains("https", message);
    }

    [Theory]
    [InlineData("http://localhost:9999/hook")]
    [InlineData("http://127.0.0.1:8080/hook")]
    public void Validate_LocalHttp_IsAllowedWithHostWarning(string url)
    {
        var (severity, message) = WebhookUrlValidator.Validate(url, isDiscord: true);

        // http is permitted for local test listeners, but the host is still
        // not a recognized Discord webhook host — non-blocking warning.
        Assert.Equal(WebhookUrlSeverity.Warning, severity);
        Assert.Contains("not a recognized Discord webhook host", message);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/12345/abcdef")]
    [InlineData("https://discordapp.com/api/webhooks/12345/abcdef")]
    public void Validate_DiscordUrlWithDiscordFormat_IsValid(string url)
    {
        var (severity, message) = WebhookUrlValidator.Validate(url, isDiscord: true);

        Assert.Equal(WebhookUrlSeverity.None, severity);
        Assert.Equal("", message);
    }

    [Fact]
    public void Validate_DiscordUrlWithoutWebhookPath_Warns()
    {
        var (severity, message) = WebhookUrlValidator.Validate(
            "https://discord.com/other/path", isDiscord: true);

        Assert.Equal(WebhookUrlSeverity.Warning, severity);
        Assert.Contains("not a recognized Discord webhook host", message);
    }

    [Fact]
    public void Validate_SlackUrlWithDiscordFormat_WarnsAboutMismatch()
    {
        var (severity, message) = WebhookUrlValidator.Validate(
            "https://hooks.slack.com/services/T00/B00/XYZ", isDiscord: true);

        Assert.Equal(WebhookUrlSeverity.Warning, severity);
        Assert.Contains("Slack", message);
        Assert.Contains("Discord format is selected", message);
    }

    [Fact]
    public void Validate_DiscordUrlWithSlackFormat_WarnsAboutMismatch()
    {
        var (severity, message) = WebhookUrlValidator.Validate(
            "https://discord.com/api/webhooks/1/abc", isDiscord: false);

        Assert.Equal(WebhookUrlSeverity.Warning, severity);
        Assert.Contains("Discord", message);
        Assert.Contains("Slack format is selected", message);
    }

    [Fact]
    public void Validate_UnknownHostWithSlackFormat_WarnsAboutHost()
    {
        var (severity, message) = WebhookUrlValidator.Validate(
            "https://example.com/hook", isDiscord: false);

        Assert.Equal(WebhookUrlSeverity.Warning, severity);
        Assert.Contains("not a recognized Slack webhook host", message);
    }

    [Fact]
    public void Validate_WhitespaceOnlyDifference_TrimmedBeforeChecks()
    {
        var (severity, _) = WebhookUrlValidator.Validate(
            "  https://discord.com/api/webhooks/1/abc  ", isDiscord: true);

        Assert.Equal(WebhookUrlSeverity.None, severity);
    }
}

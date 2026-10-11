using System.Net;
using System.Text.Json;
using KiriChess.Server.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KiriChess.Server.Tests;

/// <summary>
/// Unit coverage for Phase 8a.1: <see cref="EmailOptions"/>'s fail-closed enable condition,
/// <see cref="EmailTemplates"/>'s link construction and encoding, <see cref="QueuedEmailSender"/>'s
/// drop-on-full behaviour, and <see cref="ResendEmailSender"/>'s payload and error classification.
/// Every test here is a pure unit test - no Docker, no Testcontainers, no WebApplicationFactory.
/// </summary>
public sealed class TransactionalEmailTests
{
    private static EmailOptions MakeOptions(
        string apiKey = "re_test_key",
        string fromAddress = "noreply@kirichess.test",
        string fromName = "KiriChess",
        string frontendBaseUrl = "https://kirichess.test",
        int timeoutSeconds = 10,
        int maxAttempts = 4,
        int queueCapacity = 512) =>
        new(apiKey, fromAddress, fromName, frontendBaseUrl, timeoutSeconds, maxAttempts, queueCapacity);

    [Fact]
    public void Email_is_disabled_without_an_api_key()
    {
        var options = MakeOptions(apiKey: "");

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Email_is_disabled_without_a_frontend_base_url()
    {
        var options = MakeOptions(frontendBaseUrl: "");

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Email_is_enabled_when_key_sender_and_base_url_are_all_set()
    {
        var options = MakeOptions();

        Assert.True(options.Enabled);
    }

    [Fact]
    public void From_header_omits_the_display_name_when_it_is_blank()
    {
        var withName = MakeOptions(fromName: "KiriChess", fromAddress: "noreply@kirichess.test");
        var withoutName = MakeOptions(fromName: "", fromAddress: "noreply@kirichess.test");

        Assert.Equal("KiriChess <noreply@kirichess.test>", withName.FromHeader);
        Assert.Equal("noreply@kirichess.test", withoutName.FromHeader);
    }

    [Fact]
    public void Base_url_trailing_slash_is_trimmed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Frontend:BaseUrl"] = "https://x/"
            })
            .Build();

        var options = EmailOptions.From(config);

        Assert.Equal("https://x", options.FrontendBaseUrl);
    }

    [Fact]
    public void Confirmation_link_percent_encodes_the_token()
    {
        var options = MakeOptions();
        var userId = Guid.NewGuid();

        var message = EmailTemplates.Confirmation(options, "player@example.com", userId, "abc+def/ghi==");

        Assert.Contains("token=abc%2Bdef%2Fghi%3D%3D", message.TextBody);
        Assert.DoesNotContain("token=abc+def", message.TextBody);
    }

    [Fact]
    public void Confirmation_link_points_at_the_verify_email_page()
    {
        var options = MakeOptions();
        var userId = Guid.NewGuid();

        var message = EmailTemplates.Confirmation(options, "player@example.com", userId, "sometoken");

        Assert.Contains($"https://kirichess.test/verify-email?userId={userId}&token=", message.TextBody);
    }

    [Fact]
    public void Reset_link_points_at_the_reset_password_page_and_encodes_the_email()
    {
        var options = MakeOptions();

        var message = EmailTemplates.PasswordReset(options, "a+b@example.com", "sometoken");

        Assert.Contains("https://kirichess.test/reset-password?email=a%2Bb%40example.com&token=", message.TextBody);
    }

    [Fact]
    public void Pairing_found_link_points_at_the_game_directly()
    {
        var options = MakeOptions();
        var gameId = Guid.NewGuid();

        var message = EmailTemplates.PairingFound(
            options, "player@example.com", gameId, "Anchor", rated: true, "https://kirichess.test/unsubscribe?x=1");

        Assert.Contains($"https://kirichess.test/game/{gameId}", message.TextBody);
    }

    [Theory]
    [InlineData(true, "rated")]
    [InlineData(false, "casual")]
    public void Pairing_found_wording_matches_the_rated_flag(bool rated, string word)
    {
        var options = MakeOptions();

        var message = EmailTemplates.PairingFound(
            options, "player@example.com", Guid.NewGuid(), "Winder", rated, "https://kirichess.test/unsubscribe?x=1");

        Assert.Contains($"{word} Winder game", message.TextBody);   // matches the display name passed above
    }

    [Fact]
    public void Rematch_offered_link_points_at_the_finished_game()
    {
        var options = MakeOptions();
        var gameId = Guid.NewGuid();

        var message = EmailTemplates.RematchOffered(
            options, "player@example.com", gameId, "https://kirichess.test/unsubscribe?x=1");

        Assert.Contains($"https://kirichess.test/game/{gameId}", message.TextBody);
        Assert.Equal(EmailTemplates.RematchOfferedSubject, message.Subject);
    }

    [Fact]
    public void Unsubscribe_link_appears_in_both_bodies_when_supplied()
    {
        var options = MakeOptions();
        var unsubscribeUrl = "https://kirichess.test/unsubscribe?userId=u&category=PairingFound&token=t";

        var message = EmailTemplates.PairingFound(
            options, "player@example.com", Guid.NewGuid(), "steal1", rated: true, unsubscribeUrl);

        Assert.Contains(unsubscribeUrl, message.TextBody);
        Assert.Contains(WebUtility.HtmlEncode(unsubscribeUrl), message.HtmlBody);
    }

    [Fact]
    public void Confirmation_and_reset_carry_no_unsubscribe_link()
    {
        var options = MakeOptions();

        var confirmation = EmailTemplates.Confirmation(options, "player@example.com", Guid.NewGuid(), "tok");
        var reset = EmailTemplates.PasswordReset(options, "player@example.com", "tok");

        Assert.DoesNotContain("nsubscribe", confirmation.TextBody);
        Assert.DoesNotContain("nsubscribe", confirmation.HtmlBody);
        Assert.DoesNotContain("nsubscribe", reset.TextBody);
        Assert.DoesNotContain("nsubscribe", reset.HtmlBody);
    }

    [Fact]
    public void Html_body_escapes_the_query_separator()
    {
        var options = MakeOptions();

        var message = EmailTemplates.Confirmation(options, "player@example.com", Guid.NewGuid(), "sometoken");

        Assert.Contains("&amp;token=", message.HtmlBody);
        Assert.DoesNotContain("&token=", message.HtmlBody);
    }

    [Fact]
    public void Html_body_does_not_escape_the_copy()
    {
        var options = MakeOptions();

        var message = EmailTemplates.Confirmation(options, "player@example.com", Guid.NewGuid(), "sometoken");

        Assert.Contains("Confirm my email", message.HtmlBody);
    }

    [Fact]
    public void Html_body_uses_the_app_palette()
    {
        var options = MakeOptions();

        var message = EmailTemplates.PasswordReset(options, "player@example.com", "tok");

        // Bounty CTA and muted copy from ui/app/globals.css - not the old cyan / Tailwind gray.
        Assert.Contains("#e5c07b", message.HtmlBody);
        Assert.Contains("#9197a3", message.HtmlBody);
        Assert.DoesNotContain("#56b6c2", message.HtmlBody);
        Assert.DoesNotContain("#6b7280", message.HtmlBody);
        // King wordmark, matching the auth cards - not the old CSS diamond.
        Assert.Contains("♔", message.HtmlBody);
        Assert.DoesNotContain("rotate(45deg)", message.HtmlBody);
    }

    [Fact]
    public void Text_body_contains_the_unescaped_url()
    {
        var options = MakeOptions();

        var message = EmailTemplates.Confirmation(options, "player@example.com", Guid.NewGuid(), "sometoken");

        Assert.Contains("&token=", message.TextBody);
    }

    [Fact]
    public async Task Queued_sender_hands_the_message_to_the_reader()
    {
        var sender = new QueuedEmailSender(MakeOptions(), NullLogger<QueuedEmailSender>.Instance);
        var message = new EmailMessage("player@example.com", "Subject", "text", "html");

        await sender.SendAsync(message);

        Assert.True(sender.Reader.TryRead(out var read));
        Assert.Equal(message, read);
    }

    [Fact]
    public async Task Queued_sender_drops_instead_of_blocking_when_full()
    {
        var sender = new QueuedEmailSender(MakeOptions(queueCapacity: 1), NullLogger<QueuedEmailSender>.Instance);
        var first = new EmailMessage("player@example.com", "First", "text", "html");
        var second = new EmailMessage("player@example.com", "Second", "text", "html");

        // Both sends must complete synchronously (no blocking) even though the second cannot fit.
        await sender.SendAsync(first);
        await sender.SendAsync(second);

        Assert.True(sender.Reader.TryRead(out var read));
        Assert.Equal("First", read.Subject);
        Assert.False(sender.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Resend_sender_posts_the_expected_payload()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var options = MakeOptions(apiKey: "re_secret_key", fromAddress: "noreply@kirichess.test", fromName: "KiriChess");
        var sender = new ResendEmailSender(new StubHttpClientFactory(handler), options);
        var message = new EmailMessage("player@example.com", "Subject", "text body", "<html>body</html>");

        await sender.SendAsync(message);

        Assert.NotNull(handler.LastRequest);
        Assert.EndsWith("/emails", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("re_secret_key", handler.LastRequest.Headers.Authorization?.Parameter);

        Assert.NotNull(handler.LastBody);
        // Parsed rather than substring-matched: JsonContent.Create uses the default JS encoder,
        // which HTML-escapes '<', '>' and '&' in string values (the html body has all three), so a
        // raw substring check on the wire bytes would be asserting an escaping detail, not the payload.
        using var payload = JsonDocument.Parse(handler.LastBody!);
        var root = payload.RootElement;
        Assert.Equal("KiriChess <noreply@kirichess.test>", root.GetProperty("from").GetString());
        Assert.Equal("player@example.com", root.GetProperty("to")[0].GetString());
        Assert.Equal("Subject", root.GetProperty("subject").GetString());
        Assert.Equal("<html>body</html>", root.GetProperty("html").GetString());
        Assert.Equal("text body", root.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Resend_sender_treats_429_and_5xx_as_retryable(HttpStatusCode statusCode)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("error")
        });
        var sender = new ResendEmailSender(new StubHttpClientFactory(handler), MakeOptions());
        var message = new EmailMessage("player@example.com", "Subject", "text", "html");

        var ex = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(message));

        Assert.True(ex.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Resend_sender_treats_401_and_422_as_permanent(HttpStatusCode statusCode)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("error")
        });
        var sender = new ResendEmailSender(new StubHttpClientFactory(handler), MakeOptions());
        var message = new EmailMessage("player@example.com", "Subject", "text", "html");

        var ex = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(message));

        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Resend_sender_treats_a_transport_failure_as_retryable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection reset"));
        var sender = new ResendEmailSender(new StubHttpClientFactory(handler), MakeOptions());
        var message = new EmailMessage("player@example.com", "Subject", "text", "html");

        var ex = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(message));

        Assert.True(ex.Retryable);
    }

    // EmailDispatchService's retry loop (delay/backoff/give-up-at-MaxAttempts) is intentionally not
    // unit-tested here: exercising it would mean either real Task.Delay waits in the suite or a clock
    // abstraction invented for a single test. Its two behaviours that matter - "retry only when
    // Retryable" and "give up at MaxAttempts" - are covered by the classification tests above plus
    // the visible loop in EmailDispatchService.DeliverAsync. This gap is a recorded decision, not an
    // oversight.

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _respond(request);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("https://api.resend.com/") };
    }
}

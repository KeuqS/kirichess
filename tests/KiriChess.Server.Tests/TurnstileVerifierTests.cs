using System.Net;
using KiriChess.Server.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace KiriChess.Server.Tests;

/// <summary>
/// Unit coverage for DH.3's <see cref="TurnstileVerifier"/>: disabled-by-default no-op, a missing
/// token failing closed once enabled, a genuine Cloudflare rejection failing closed, and fail-open
/// behaviour on transport failure and a non-success status. Pure unit tests, no network - same
/// StubHandler/StubHttpClientFactory pattern as BreachedPasswordCheckerTests.
/// </summary>
public sealed class TurnstileVerifierTests
{
    private static TurnstileVerifier MakeVerifier(StubHandler handler, string secretKey = "test-secret") =>
        new(new StubHttpClientFactory(handler),
            new TurnstileOptions(secretKey, TimeoutSeconds: 3),
            NullLogger<TurnstileVerifier>.Instance);

    [Fact]
    public async Task Returns_true_without_calling_out_when_disabled()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called when disabled"));

        Assert.True(await MakeVerifier(handler, secretKey: "").IsValidAsync("some-token"));
    }

    [Fact]
    public async Task Fails_closed_when_enabled_and_no_token_was_supplied()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called without a token"));

        Assert.False(await MakeVerifier(handler).IsValidAsync(null));
        Assert.False(await MakeVerifier(handler).IsValidAsync(""));
    }

    [Fact]
    public async Task Returns_true_on_a_genuine_success_response()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"success":true}""", System.Text.Encoding.UTF8, "application/json")
        });

        Assert.True(await MakeVerifier(handler).IsValidAsync("good-token"));
    }

    [Fact]
    public async Task Fails_closed_on_a_genuine_rejection()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"success":false}""", System.Text.Encoding.UTF8, "application/json")
        });

        Assert.False(await MakeVerifier(handler).IsValidAsync("bad-token"));
    }

    [Fact]
    public async Task Fails_open_on_a_transport_failure()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection reset"));

        Assert.True(await MakeVerifier(handler).IsValidAsync("some-token"));
    }

    [Fact]
    public async Task Fails_open_on_a_non_success_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.True(await MakeVerifier(handler).IsValidAsync("some-token"));
    }

    [Fact]
    public async Task Sends_the_secret_and_token_as_form_fields()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"success":true}""", System.Text.Encoding.UTF8, "application/json")
        });

        await MakeVerifier(handler, secretKey: "the-secret").IsValidAsync("the-token");

        Assert.NotNull(handler.LastRequestUri);
        Assert.EndsWith("turnstile/v0/siteverify", handler.LastRequestUri!);
        Assert.Contains("secret=the-secret", handler.LastRequestBody);
        Assert.Contains("response=the-token", handler.LastRequestBody);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _respond(request);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("https://challenges.cloudflare.com/") };
    }
}

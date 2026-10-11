using System.Net;
using System.Security.Cryptography;
using System.Text;
using KiriChess.Server.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace KiriChess.Server.Tests;

/// <summary>
/// Unit coverage for Phase 9d.1's <see cref="BreachedPasswordChecker"/>: the k-anonymity request
/// shape (only a 5-character prefix leaves the process), match/no-match parsing of HIBP's range
/// response, and fail-open behaviour on disable, transport failure and a non-success status. Pure
/// unit tests, no network - same StubHandler/StubHttpClientFactory pattern as TransactionalEmailTests.
/// </summary>
public sealed class BreachedPasswordCheckerTests
{
    private static (string Prefix, string Suffix) HashOf(string password)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        return (hash[..5], hash[5..]);
    }

    private static BreachedPasswordChecker MakeChecker(StubHandler handler, bool enabled = true) =>
        new(new StubHttpClientFactory(handler),
            new BreachedPasswordCheckOptions(enabled, TimeoutSeconds: 3),
            NullLogger<BreachedPasswordChecker>.Instance);

    [Fact]
    public async Task Returns_true_when_the_suffix_is_in_the_response()
    {
        var (prefix, suffix) = HashOf("correcthorsebatterystaple");
        var handler = new StubHandler(req =>
        {
            Assert.EndsWith($"range/{prefix}", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"AAAA1111AAAA1111AAAA1111AAAA1111AAA1:3\r\n{suffix}:42\r\nBBBB2222BBBB2222BBBB2222BBBB2222BBB2:7\r\n")
            };
        });

        Assert.True(await MakeChecker(handler).IsBreachedAsync("correcthorsebatterystaple"));
    }

    [Fact]
    public async Task Returns_false_when_the_suffix_is_absent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("AAAA1111AAAA1111AAAA1111AAAA1111AAA1:3\r\n")
        });

        Assert.False(await MakeChecker(handler).IsBreachedAsync("a-genuinely-unique-passphrase-9d1"));
    }

    [Fact]
    public async Task Fails_open_when_disabled()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called when disabled"));

        Assert.False(await MakeChecker(handler, enabled: false).IsBreachedAsync("whatever"));
    }

    [Fact]
    public async Task Fails_open_on_a_transport_failure()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection reset"));

        Assert.False(await MakeChecker(handler).IsBreachedAsync("whatever"));
    }

    [Fact]
    public async Task Fails_open_on_a_non_success_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.False(await MakeChecker(handler).IsBreachedAsync("whatever"));
    }

    [Fact]
    public async Task Only_the_five_character_prefix_of_the_hash_leaves_the_process()
    {
        var (prefix, _) = HashOf("never-sent-in-full");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        });

        await MakeChecker(handler).IsBreachedAsync("never-sent-in-full");

        Assert.NotNull(handler.LastRequest);
        var requestUri = handler.LastRequest!.RequestUri!.ToString();
        Assert.EndsWith($"range/{prefix}", requestUri);
        Assert.DoesNotContain("never-sent-in-full", requestUri);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("https://api.pwnedpasswords.com/") };
    }
}

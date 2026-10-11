using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// Thrown when one delivery attempt fails. <see cref="Retryable"/> is the whole point of the type:
/// a 429 or a 502 is worth trying again, a 401 (bad key) or a 422 (unverified sender domain) will
/// fail identically forever and retrying it only delays the log line that tells an operator what is
/// wrong.
/// </summary>
public sealed class EmailDeliveryException : Exception
{
    public EmailDeliveryException(string message, bool retryable, Exception? innerException = null)
        : base(message, innerException) => Retryable = retryable;

    public bool Retryable { get; }
}

/// <summary>
/// Resend (https://resend.com) provider client - one attempt per call, no retry policy of its own.
/// Retrying is <see cref="EmailDispatchService"/>'s job, because only the dispatcher knows the
/// message is sitting in a queue rather than in a request. Registered only when
/// <see cref="EmailOptions.Enabled"/>; see Program.cs.
/// </summary>
public sealed class ResendEmailSender
{
    /// <summary>Named <see cref="HttpClient"/> registered in Program.cs with the base address and timeout.</summary>
    public const string HttpClientName = "resend";

    // WhenWritingNull: reply_to and headers are absent from the payload entirely for confirmation and
    // password-reset mail (M-5-5, M-5-1) rather than sent as literal JSON nulls.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _clients;
    private readonly EmailOptions _options;

    public ResendEmailSender(IHttpClientFactory clients, EmailOptions options)
    {
        _clients = clients;
        _options = options;
    }

    /// <summary>Sends one message. Returns on success; throws <see cref="EmailDeliveryException"/> otherwise.</summary>
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        // M-5-1: List-Unsubscribe is the deliverability/reputation signal mailbox providers read, and
        // it is also what puts a native "Unsubscribe" control next to the sender name. The mailto
        // fallback keeps working even for a recipient whose client ignores the header's http(s) form.
        // List-Unsubscribe-Post only goes out when the URL is the one-click endpoint that can actually
        // answer a bodyless/form POST (M-5-2) - sending it next to the human-facing footer link would
        // promise a one-click flow the server does not implement there.
        Dictionary<string, string>? headers = message.UnsubscribeHeaderUrl is null
            ? null
            : message.UnsubscribeIsOneClick
                ? new Dictionary<string, string>
                {
                    ["List-Unsubscribe"] = $"<{message.UnsubscribeHeaderUrl}>",
                    ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
                }
                : new Dictionary<string, string>
                {
                    ["List-Unsubscribe"] = $"<mailto:contact@kirichess.com?subject=unsubscribe>, <{message.UnsubscribeHeaderUrl}>"
                };

        var payload = new
        {
            from = _options.FromHeader,
            to = new[] { message.ToEmail },
            subject = message.Subject,
            html = message.HtmlBody,
            text = message.TextBody,
            reply_to = message.ReplyTo,
            headers
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(payload, options: Json)
        };
        // Set per-request rather than on the named client so the key never sits on a shared
        // DefaultRequestHeaders instance, and so a test only needs a bare HttpClient with a stub
        // handler to exercise this class.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var client = _clients.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // No response at all: DNS, TLS, connection reset, or our own HttpClient.Timeout firing
            // (which surfaces as TaskCanceledException). All transient by nature.
            throw new EmailDeliveryException("Resend request failed before a response arrived.", retryable: true, ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;

            throw new EmailDeliveryException(
                $"Resend returned {status}: {Truncate(body, 300)}", retryable);
        }
    }

    /// <summary>Provider error bodies can be long; a log line is not a place to paste one whole.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}

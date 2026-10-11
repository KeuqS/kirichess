using KiriChess.Server.Auth;
using KiriChess.Server.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Tests;

/// <summary>
/// Not coverage - a developer tool. <see cref="EmailTemplates"/> is the only place the transactional
/// mail is styled (inline CSS, because no mail client will load <c>ui/app/globals.css</c>), and
/// <see cref="LoggingEmailSender"/> logs only the text/plain part, so in local development the HTML
/// half is never seen by anyone before it reaches a real inbox. This writes every template's
/// <see cref="EmailMessage.HtmlBody"/> and <see cref="EmailMessage.TextBody"/> to disk so both halves
/// can be opened in a browser.
///
/// Opt-in, like the engine soak tests, so a normal <c>dotnet test</c> run writes nothing:
/// <code>
/// KIRICHESS_EMAIL_PREVIEW=1 dotnet test KiriChess.slnx --filter FullyQualifiedName~EmailPreviewDump
/// </code>
/// Output goes to <c>%TEMP%/kirichess-email-preview/</c>, or to <c>KIRICHESS_EMAIL_PREVIEW_DIR</c>
/// when that is set. Nothing is written into the repo.
/// </summary>
public sealed class EmailPreviewDump
{
    private static readonly EmailOptions Mail = new(
        ApiKey: "re_preview",
        FromAddress: "noreply@kirichess.com",
        FromName: "KiriChess",
        FrontendBaseUrl: "https://kirichess.com",
        TimeoutSeconds: 10,
        MaxAttempts: 4,
        QueueCapacity: 512,
        ApiBaseUrl: "https://api.kirichess.com");

    [Fact]
    public void Write_every_template_to_disk()
    {
        if (Environment.GetEnvironmentVariable("KIRICHESS_EMAIL_PREVIEW") is not "1")
            return;

        var dir = Environment.GetEnvironmentVariable("KIRICHESS_EMAIL_PREVIEW_DIR")
                  ?? Path.Combine(Path.GetTempPath(), "kirichess-email-preview");
        Directory.CreateDirectory(dir);

        var userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var gameId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        const string to = "player@example.com";
        // A realistic Identity token: Base64 with the '+' and '=' that make the encoding in
        // EmailTemplates.Link load-bearing, so the preview shows the link a real mail carries.
        const string token = "CfDJ8Abc+def/ghi=jkl";
        var tokens = new UnsubscribeTokenService(
            Options.Create(new JwtOptions { Key = new string('k', 64) }));
        var unsubscribe = UnsubscribeLink.Build(Mail, tokens, userId, NotificationCategory.PairingFound);
        var oneClick = UnsubscribeLink.BuildOneClick(Mail, tokens, userId, NotificationCategory.PairingFound);

        var messages = new (string Name, EmailMessage Message)[]
        {
            ("confirmation", EmailTemplates.Confirmation(Mail, to, userId, token)),
            ("password-reset", EmailTemplates.PasswordReset(Mail, to, token)),
            ("pairing-found", EmailTemplates.PairingFound(Mail, to, gameId, "blitz", rated: true, unsubscribe, oneClick)),
            ("rematch-offered", EmailTemplates.RematchOffered(Mail, to, gameId, unsubscribe, oneClick)),
        };

        var index = new List<string>();
        foreach (var (name, message) in messages)
        {
            File.WriteAllText(Path.Combine(dir, $"{name}.html"), message.HtmlBody);
            File.WriteAllText(Path.Combine(dir, $"{name}.txt"), message.TextBody);
            index.Add(
                $"""<li><a href="{name}.html">{name}</a> - subject: {message.Subject} - <a href="{name}.txt">text/plain</a></li>""");
        }

        File.WriteAllText(
            Path.Combine(dir, "index.html"),
            $"""
            <!doctype html>
            <meta charset="utf-8">
            <title>KiriChess email previews</title>
            <body style="background:#282c34;color:#abb2bf;font-family:Arial,sans-serif;padding:24px;">
              <h1 style="color:#fff;">KiriChess email previews</h1>
              <ul>{string.Join("\n", index)}</ul>
            </body>
            """);
    }
}

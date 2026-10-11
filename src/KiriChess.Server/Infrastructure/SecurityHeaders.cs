namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Phase 9c.1: response headers a browser ever hitting this origin directly should get. The API
/// itself serves no HTML/CSS/JS - it's JSON endpoints and two SignalR hubs - so the CSP here is
/// maximally strict rather than an allowlist: nothing loaded from this origin should ever execute
/// or render as a page. X-Frame-Options/frame-ancestors matter regardless of content type, since an
/// error page or an accidentally-HTML response is still embeddable without them.
/// </summary>
public static class SecurityHeadersSetup
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";

    public static WebApplication UseKiriChessSecurityHeaders(this WebApplication app)
    {
        // Excludes localhost/loopback by default, so local dev over http is unaffected. Delivered
        // unconditionally otherwise - browsers ignore the header on a plain-http response, so this
        // is a no-op until Cloudflare/Fly TLS termination is in front of it (DH.3), and armed the
        // moment it is.
        app.UseHsts();

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            await next();
        });

        return app;
    }
}

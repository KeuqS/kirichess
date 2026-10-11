using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using System.IO;
using System.Threading.Tasks;
using System;

namespace KiriChess.Server.Account;

public static class StripeWebhookEndpoints
{
    public static void MapStripeWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/stripe", HandleStripeWebhook)
           .AllowAnonymous()
           .ExcludeFromDescription(); // Optional: hides it from Swagger if you have it
    }

    /// <summary>
    /// A checkout only grants a badge once Stripe reports the money is in. <c>unpaid</c> is a
    /// delayed method (SEPA) that later fires <c>checkout.session.async_payment_succeeded</c> or
    /// <c>async_payment_failed</c>; granting on the unpaid <c>completed</c> would consume the
    /// single-use reference and leave a successful payment with nothing to redeem ($1-3).
    /// </summary>
    internal static bool ShouldGrant(string? paymentStatus) =>
        paymentStatus is "paid" or "no_payment_required";

    internal static bool IsLibraryApiVersion(string? eventApiVersion) =>
        string.Equals(eventApiVersion, StripeConfiguration.ApiVersion, StringComparison.Ordinal);

    private static async Task<IResult> HandleStripeWebhook(
        HttpContext context,
        [FromServices] PatronService patrons,
        [FromServices] IConfiguration config,
        [FromServices] ILogger<PatronService> logger,
        CancellationToken ct)
    {
        var webhookSecret = config["Stripe:WebhookSecret"];
        if (string.IsNullOrEmpty(webhookSecret))
        {
            logger.LogError("Stripe WebhookSecret is not configured.");
            return Results.StatusCode(500);
        }

        var json = await new StreamReader(context.Request.Body).ReadToEndAsync(ct);
        try
        {
            var signatureHeader = context.Request.Headers["Stripe-Signature"];
            // $1-1: a dashboard endpoint carries the account's default API version, which is almost
            // never Stripe.net's pin (`2026-07-29.dahlia` on 52.3.0). The three-argument overload
            // throws on that mismatch, every delivery 400s, and Stripe disables the endpoint. The
            // objects this handler reads (Session.PaymentStatus, Subscription.Status) are stable
            // across versions, so process the event and log the skew as its own line.
            var stripeEvent = EventUtility.ConstructEvent(
                json, signatureHeader, webhookSecret, throwOnApiVersionMismatch: false);

            if (!IsLibraryApiVersion(stripeEvent.ApiVersion))
            {
                logger.LogWarning(
                    "Stripe webhook API version {EventVersion} differs from Stripe.net {LibraryVersion}; processing anyway.",
                    stripeEvent.ApiVersion, StripeConfiguration.ApiVersion);
            }

            if (stripeEvent.Type is EventTypes.CheckoutSessionCompleted
                or EventTypes.CheckoutSessionAsyncPaymentSucceeded)
            {
                var session = stripeEvent.Data.Object as Session;
                if (session is not null)
                    await GrantIfPaidAsync(session, patrons, logger, ct);
            }
            else if (stripeEvent.Type == EventTypes.CustomerSubscriptionDeleted)
            {
                var subscription = stripeEvent.Data.Object as Subscription;
                if (subscription is not null)
                    await patrons.RevokeBySubscriptionAsync(subscription.Id, ct);
            }
            else if (stripeEvent.Type == EventTypes.CustomerSubscriptionUpdated)
            {
                var subscription = stripeEvent.Data.Object as Subscription;
                // A past_due subscription is still being retried by Stripe and must keep its badge;
                // Stripe sends the deleted event separately once the retries are exhausted.
                if (subscription is not null && subscription.Status is "canceled" or "unpaid" or "incomplete_expired")
                    await patrons.RevokeBySubscriptionAsync(subscription.Id, ct);
            }

            return Results.Ok();
        }
        catch (StripeException e)
        {
            logger.LogError(e, "Stripe Webhook error.");
            return Results.BadRequest();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Internal error processing Stripe Webhook.");
            return Results.StatusCode(500);
        }
    }

    private static async Task GrantIfPaidAsync(
        Session session, PatronService patrons, ILogger logger, CancellationToken ct)
    {
        if (!ShouldGrant(session.PaymentStatus))
        {
            logger.LogInformation(
                "Checkout {SessionId} payment_status is {Status}; not granting.",
                session.Id, session.PaymentStatus);
            return;
        }

        await patrons.GrantAsync(
            session.ClientReferenceId, session.AmountTotal ?? 0,
            session.Mode == "subscription", session.SubscriptionId, session.CustomerId, ct);
    }
}

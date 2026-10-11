using System.Security.Cryptography;
using System.Text;
using KiriChess.Server.Account;
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// The two launch traps on POST /api/webhooks/stripe, tested against Stripe.net itself rather than
/// a full host: an API-version mismatch must not throw ($1-1), and an unpaid checkout must not
/// grant ($1-3). Booting <c>WebApplicationFactory</c> against an unreachable database hangs on
/// dispose (hosted sweeps retry the connection), so these stay host-free.
/// </summary>
public sealed class StripeWebhookTests
{
    private const string WebhookSecret = "whsec_test_secret_for_stripe_webhook_unit_tests";

    [Theory]
    [InlineData("paid", true)]
    [InlineData("no_payment_required", true)]
    [InlineData("unpaid", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ShouldGrant_is_only_paid_or_no_payment_required(string? status, bool expected) =>
        Assert.Equal(expected, StripeWebhookEndpoints.ShouldGrant(status));

    [Fact]
    public void Library_api_version_helper_rejects_a_foreign_pin()
    {
        Assert.False(StripeWebhookEndpoints.IsLibraryApiVersion("2018-01-01"));
        Assert.True(StripeWebhookEndpoints.IsLibraryApiVersion(StripeConfiguration.ApiVersion));
    }

    [Fact]
    public void Default_ConstructEvent_throws_on_an_API_version_mismatch()
    {
        var payload = CheckoutJson("paid", apiVersion: "2018-01-01");
        var header = Sign(payload);

        var ex = Assert.ThrowsAny<StripeException>(() =>
            EventUtility.ConstructEvent(payload, header, WebhookSecret));

        Assert.Contains("API version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConstructEvent_with_throwOnApiVersionMismatch_false_accepts_a_foreign_pin()
    {
        var payload = CheckoutJson("unpaid", apiVersion: "2018-01-01");
        var header = Sign(payload);

        var stripeEvent = EventUtility.ConstructEvent(
            payload, header, WebhookSecret, throwOnApiVersionMismatch: false);

        Assert.Equal(EventTypes.CheckoutSessionCompleted, stripeEvent.Type);
        Assert.False(StripeWebhookEndpoints.IsLibraryApiVersion(stripeEvent.ApiVersion));
        var session = Assert.IsType<Session>(stripeEvent.Data.Object);
        Assert.False(StripeWebhookEndpoints.ShouldGrant(session.PaymentStatus));
    }

    [Fact]
    public void Async_payment_succeeded_deserializes_as_the_grant_event()
    {
        var payload = CheckoutJson("paid", type: EventTypes.CheckoutSessionAsyncPaymentSucceeded);
        var header = Sign(payload);

        var stripeEvent = EventUtility.ConstructEvent(
            payload, header, WebhookSecret, throwOnApiVersionMismatch: false);

        Assert.Equal(EventTypes.CheckoutSessionAsyncPaymentSucceeded, stripeEvent.Type);
        var session = Assert.IsType<Session>(stripeEvent.Data.Object);
        Assert.True(StripeWebhookEndpoints.ShouldGrant(session.PaymentStatus));
    }

    [Fact]
    public void Bad_signature_still_throws_when_version_mismatch_is_ignored()
    {
        var payload = CheckoutJson("paid");

        Assert.ThrowsAny<StripeException>(() =>
            EventUtility.ConstructEvent(
                payload, "t=1,v1=deadbeef", WebhookSecret, throwOnApiVersionMismatch: false));
    }

    private static string Sign(string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"t={timestamp},v1={hex}";
    }

    private static string CheckoutJson(
        string paymentStatus,
        string? apiVersion = null,
        string? type = null)
    {
        var version = apiVersion ?? StripeConfiguration.ApiVersion;
        var eventType = type ?? EventTypes.CheckoutSessionCompleted;
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $$"""
            {
              "id": "evt_test_webhook",
              "object": "event",
              "api_version": "{{version}}",
              "created": {{created}},
              "type": "{{eventType}}",
              "livemode": false,
              "pending_webhooks": 1,
              "data": {
                "object": {
                  "id": "cs_test_webhook",
                  "object": "checkout.session",
                  "amount_total": 500,
                  "client_reference_id": null,
                  "customer": null,
                  "mode": "payment",
                  "payment_status": "{{paymentStatus}}",
                  "subscription": null
                }
              }
            }
            """;
    }
}

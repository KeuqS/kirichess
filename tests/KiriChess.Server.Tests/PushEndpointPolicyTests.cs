using KiriChess.Server.Infrastructure.Push;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// S-1-8: the allowlist that stops a stored push endpoint from being an outbound request to whatever
/// the container can reach. Pure string work, so no Docker and no fixture - the endpoint-level
/// counterpart (a 400 out of POST /api/push/subscribe) lives in <see cref="PushNotificationTests"/>.
/// </summary>
public sealed class PushEndpointPolicyTests
{
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/dQw4w9WgXcQ")]
    [InlineData("https://android.googleapis.com/gcm/send/dQw4w9WgXcQ")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAAA")]
    [InlineData("https://wns2-by3p.notify.windows.com/w/?token=abc")]
    [InlineData("https://web.push.apple.com/QAAAA")]
    public void A_real_push_service_url_is_accepted(string endpoint) =>
        Assert.True(PushEndpointPolicy.IsAllowed(endpoint));

    [Theory]
    // Not a push service at all: the plain SSRF case.
    [InlineData("https://example.com/whatever")]
    [InlineData("https://internal-admin.flycast/reset")]
    // Suffix confusion: the allowlisted name has to be the host's own suffix, not a substring of it.
    [InlineData("https://fcm.googleapis.com.attacker.example/send")]
    [InlineData("https://notfcm.googleapis.com.evil/send")]
    [InlineData("https://evilgoogleapis.com/send")]
    public void A_host_outside_the_allowlist_is_refused(string endpoint) =>
        Assert.False(PushEndpointPolicy.IsAllowed(endpoint));

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("ftp://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("file:///etc/passwd")]
    public void A_non_https_scheme_is_refused(string endpoint) =>
        Assert.False(PushEndpointPolicy.IsAllowed(endpoint));

    [Theory]
    // The private and link-local targets the finding names, spelled the way an attacker would.
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/admin")]
    [InlineData("https://10.0.0.5/internal")]
    [InlineData("https://192.168.1.1/")]
    [InlineData("https://[fdaa:0:1::3]/internal")]
    public void A_private_or_link_local_address_is_refused(string endpoint) =>
        Assert.False(PushEndpointPolicy.IsAllowed(endpoint));

    [Fact]
    public void An_allowlisted_host_on_another_port_is_refused() =>
        Assert.False(PushEndpointPolicy.IsAllowed("https://fcm.googleapis.com:8443/fcm/send/abc"));

    [Fact]
    public void Credentials_in_the_url_are_refused() =>
        Assert.False(PushEndpointPolicy.IsAllowed("https://user:pass@fcm.googleapis.com/fcm/send/abc"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/fcm/send/abc")]
    public void A_missing_or_unparseable_endpoint_is_refused(string? endpoint) =>
        Assert.False(PushEndpointPolicy.IsAllowed(endpoint));

    [Fact]
    public void An_endpoint_longer_than_the_column_is_refused()
    {
        var tooLong = "https://fcm.googleapis.com/fcm/send/" + new string('a', PushEndpointPolicy.MaxLength);
        Assert.True(tooLong.Length > PushEndpointPolicy.MaxLength);
        Assert.False(PushEndpointPolicy.IsAllowed(tooLong));
    }
}

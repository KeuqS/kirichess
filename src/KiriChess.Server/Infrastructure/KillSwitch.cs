namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Phase 10c.4 (plus S-5-4's <see cref="Api"/>). The things that can be turned off without a deploy,
/// each mapped to a threat this roadmap already names. Deliberately a short, named list and no
/// feature-flag vendor behind it - see <see cref="KillSwitchService"/>'s doc comment for why a small
/// table is enough.
/// </summary>
public enum KillSwitch
{
    /// <summary>A signup flood. 9d.1's breach check and DH.3's Turnstile are the preventive controls; neither of them is a switch.</summary>
    Registration,

    /// <summary>10a.2's connection budget is the fine-grained control; this is the coarse lever behind it, for when even the reserved anonymous share is the problem.</summary>
    Spectating,

    /// <summary>9e.4's public open-seek list.</summary>
    SeekList,

    /// <summary>The stop-the-world lever for when the database is unwell: nobody enters a new game while it holds.</summary>
    Pairing,

    /// <summary>
    /// S-5-4: the coarse lever for the whole of `/api/v1`, checked once for every route in that group
    /// (see <c>PublicApiEndpoints</c>). Before this switch existed, the only response to abuse or load
    /// concentrated on the bot API was per-token revocation or pulling the routes at a deploy - and a
    /// deploy drops every live game (10a.3), the exact outcome 10c.4 built this mechanism to avoid.
    /// </summary>
    Api,
}

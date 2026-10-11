using System.Diagnostics.Metrics;
using KiriChess.Server.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 10a.2. The arithmetic of the connection budget, tested directly rather than through a live
/// hub: <see cref="HubConnectionBudget"/> is deliberately a plain object with no SignalR types in
/// its signature precisely so the ceiling, the reserve and the release path can be pinned without
/// standing up a server. The wiring that applies it is covered by
/// <see cref="HubConnectionBudgetEndToEndTests"/>.
/// </summary>
public sealed class HubConnectionBudgetTests
{
    private const string Ip = "ip:203.0.113.7";
    private const string OtherIp = "ip:198.51.100.4";

    private static HubConnectionBudget Build(
        int perIp = 3, int anonymous = 4, bool enabled = true, KiriChessMetrics? metrics = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hub:ConnectionBudget:Enabled"] = enabled ? "true" : "false",
            ["Hub:ConnectionBudget:MaxConnectionsPerIp"] = perIp.ToString(),
            ["Hub:ConnectionBudget:MaxAnonymousConnections"] = anonymous.ToString()
        }).Build(), metrics ?? new KiriChessMetrics(new TestMeterFactory()));

    [Fact]
    public void One_address_cannot_hold_more_than_its_share()
    {
        var budget = Build(perIp: 3);

        for (var i = 0; i < 3; i++)
            Assert.Null(budget.TryReserve($"c{i}", Ip, authenticated: true));

        Assert.Equal(HubConnectionBudget.PerIpPolicy, budget.TryReserve("c3", Ip, authenticated: true));

        // The cap is per address, not global: a different caller is unaffected by the first one's
        // exhaustion, which is the whole point of partitioning rather than lowering hard_limit.
        Assert.Null(budget.TryReserve("other", OtherIp, authenticated: true));
    }

    [Fact]
    public void A_closed_connection_gives_its_slot_back()
    {
        var budget = Build(perIp: 1);

        Assert.Null(budget.TryReserve("first", Ip, authenticated: true));
        Assert.Equal(HubConnectionBudget.PerIpPolicy, budget.TryReserve("second", Ip, authenticated: true));

        budget.Release("first");

        Assert.Equal(0, budget.LiveForIp(Ip));
        Assert.Null(budget.TryReserve("second", Ip, authenticated: true));
    }

    [Fact]
    public void Releasing_twice_or_releasing_a_stranger_does_not_free_somebody_elses_slot()
    {
        var budget = Build(perIp: 2);

        Assert.Null(budget.TryReserve("held", Ip, authenticated: true));

        budget.Release("never-reserved");
        budget.Release("held");
        budget.Release("held");

        Assert.Equal(0, budget.LiveForIp(Ip));
    }

    [Fact]
    public void Spectators_cannot_spend_the_share_reserved_for_players()
    {
        // Anonymous ceiling 4, and per-IP high enough not to be the thing that fires: this is the
        // launch-day shape, a crowd of onlookers arriving while players are still trying to play.
        var budget = Build(perIp: 100, anonymous: 4);

        for (var i = 0; i < 4; i++)
            Assert.Null(budget.TryReserve($"watcher{i}", Ip, authenticated: false));

        Assert.Equal(HubConnectionBudget.AnonymousPolicy,
            budget.TryReserve("watcher4", Ip, authenticated: false));

        // The line the whole item exists for: with the anonymous ceiling full, a signed-in player
        // still gets a socket.
        Assert.Null(budget.TryReserve("player", Ip, authenticated: true));
    }

    [Fact]
    public void An_authenticated_connection_never_counts_against_the_anonymous_ceiling()
    {
        var budget = Build(perIp: 100, anonymous: 2);

        for (var i = 0; i < 10; i++)
            Assert.Null(budget.TryReserve($"player{i}", Ip, authenticated: true));

        Assert.Equal(0, budget.LiveAnonymous);
    }

    [Fact]
    public void Headroom_answers_the_same_question_as_a_reservation_without_taking_one()
    {
        var budget = Build(perIp: 1, anonymous: 1);

        Assert.True(budget.HasHeadroom(Ip, authenticated: true));
        Assert.True(budget.HasHeadroom(Ip, authenticated: true));   // asking did not consume anything
        Assert.Equal(0, budget.LiveForIp(Ip));

        Assert.Null(budget.TryReserve("taken", Ip, authenticated: true));
        Assert.False(budget.HasHeadroom(Ip, authenticated: true));
    }

    [Fact]
    public void A_caller_with_no_address_is_never_refused_by_the_per_ip_cap()
    {
        // Fail-open, and deliberately so: every such caller keys to the same bucket, so enforcing the
        // per-IP cap there would not partition anything, it would cap the whole site at one bucket's
        // worth of connections because forwarded headers stopped working. The anonymous ceiling still
        // applies, which is what protects the players' reserve.
        var budget = Build(perIp: 2, anonymous: 100);

        for (var i = 0; i < 20; i++)
            Assert.Null(budget.TryReserve($"c{i}", HubConnectionBudget.UnknownAddress, authenticated: false));

        // Still counted, so the anonymous ceiling can do its half.
        Assert.Equal(20, budget.LiveAnonymous);

        var capped = Build(perIp: 2, anonymous: 3);
        for (var i = 0; i < 3; i++)
            Assert.Null(capped.TryReserve($"c{i}", HubConnectionBudget.UnknownAddress, authenticated: false));
        Assert.Equal(HubConnectionBudget.AnonymousPolicy,
            capped.TryReserve("over", HubConnectionBudget.UnknownAddress, authenticated: false));
    }

    [Fact]
    public void Disabled_means_no_counting_at_all_rather_than_counting_without_enforcing()
    {
        var budget = Build(perIp: 1, anonymous: 1, enabled: false);

        for (var i = 0; i < 50; i++)
            Assert.Null(budget.TryReserve($"c{i}", Ip, authenticated: false));

        Assert.Equal(0, budget.LiveForIp(Ip));
        Assert.Equal(0, budget.LiveAnonymous);
        Assert.True(budget.HasHeadroom(Ip, authenticated: false));
    }

    [Fact]
    public void Concurrent_arrivals_on_one_address_cannot_both_take_the_last_slot()
    {
        // The check and the increment are one critical section; this is the test that fails if that
        // ever stops being true. Two hundred threads, one slot.
        var budget = Build(perIp: 1);
        var admitted = 0;

        Parallel.For(0, 200, i =>
        {
            if (budget.TryReserve($"racer{i}", Ip, authenticated: true) is null)
                Interlocked.Increment(ref admitted);
        });

        Assert.Equal(1, admitted);
        Assert.Equal(1, budget.LiveForIp(Ip));
    }

    [Fact]
    public void A_refusal_is_counted_on_the_rejection_instrument_rather_than_being_silent()
    {
        // Same instrument the HTTP limiter uses, tagged surface="hub-connect": a refusal that is not
        // counted anywhere is a capacity problem nobody can see from the outside, which is the state
        // this whole item is about.
        using var meterFactory = new TestMeterFactory();
        var recorded = new List<(string Instrument, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KiriChessMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            recorded.Add((instrument.Name, tags.ToArray())));
        listener.Start();

        var budget = Build(perIp: 1, anonymous: 1, metrics: new KiriChessMetrics(meterFactory));
        budget.TryReserve("first", Ip, authenticated: false);
        budget.TryReserve("refused-by-ip", Ip, authenticated: false);
        budget.TryReserve("refused-by-ceiling", OtherIp, authenticated: false);

        var rejections = recorded.Where(r => r.Instrument == "kirichess.ratelimit.rejected").ToList();
        Assert.Equal(2, rejections.Count);
        Assert.All(rejections, r => Assert.Contains(r.Tags, t => t.Key == "surface" && Equals(t.Value, "hub-connect")));
        Assert.Contains(rejections, r => r.Tags.Any(t => t.Key == "policy" && Equals(t.Value, HubConnectionBudget.PerIpPolicy)));
        Assert.Contains(rejections, r => r.Tags.Any(t => t.Key == "policy" && Equals(t.Value, HubConnectionBudget.AnonymousPolicy)));
    }
}

using KiriChess.Engine.Config;
using KiriChess.Server.Analytics;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Games;

/// <summary>
/// The finished game's own two players, for <see cref="GameHub.AcceptRematch"/> to run the same
/// <see cref="PlayEligibilityPolicy"/> checks <c>ChallengeEndpoints</c> runs on its two players before
/// claiming the offer - see <see cref="RematchService.PeekOpenOfferAsync"/>'s doc comment for why that
/// has to happen between peeking the offer and claiming it, not before or after.
/// </summary>
public sealed record RematchCandidate(Guid GameId, Guid WhitePlayerId, Guid BlackPlayerId, bool Rated);

/// <summary>
/// The finished game's players/config/rating a claimed rematch offer hands back to
/// <see cref="GameHub.AcceptRematch"/>, colours already swapped from the original game.
/// </summary>
public sealed record RematchAcceptance(
    KiriChessConfig Config, Guid NewWhitePlayerId, Guid NewBlackPlayerId, string? Pool, bool Rated);

/// <summary>
/// What one <see cref="RematchService.OfferAsync"/> call actually did (C-6-3).
/// <paramref name="OpponentId"/> is the other original player, never the caller.
/// <paramref name="IsNewOffer"/> is true only when this call created the offer row, which is the one
/// case that should reach a player who is not looking at the page. It is false when the call found an
/// offer already accepted and did nothing, and there is no third case: every other repeat is refused
/// outright rather than reported here, so a caller cannot mistake "nothing happened" for "notify
/// again". Gating the mail and the push on this is what makes the reach-back fan-out one-shot per
/// finished game instead of one per hub call.
/// </summary>
public sealed record RematchOfferOutcome(Guid OpponentId, bool IsNewOffer);

/// <summary>
/// Offer/accept for a rematch of a just-finished game (Phase 11a.1). Scoped, like
/// <see cref="ChallengeService"/>, because <see cref="GameHub"/> is instantiated per invocation and a
/// scoped <see cref="KiriChessDbContext"/> injected here lives exactly as long as that call.
///
/// The accept guard is the same shape as <c>ChallengeEndpoints</c>'s own two-friends-at-once race
/// (Phase 9d.8 scope decision 3): one <c>ExecuteUpdateAsync</c> that only the first
/// caller to reach it can win. It has to be a Postgres round-trip rather than an in-memory flag,
/// because unlike a live <see cref="GameSession"/> a finished game has no owning instance (Phase
/// 8.4) - two accepts can land on two different processes in the same second, and only a shared store
/// sees both.
/// </summary>
public sealed class RematchService
{
    private readonly KiriChessDbContext _db;
    private readonly IGameClock _clock;
    private readonly ChallengeOptions _challenges;
    private readonly ILogger<RematchService> _logger;

    public RematchService(
        KiriChessDbContext db, IGameClock clock, IOptions<ChallengeOptions> challenges,
        ILogger<RematchService> logger)
    {
        _db = db;
        _clock = clock;
        _challenges = challenges.Value;
        _logger = logger;
    }

    /// <summary>
    /// How long after a game ends a rematch may still be offered (C-6-3). Borrowed deliberately from
    /// <see cref="ChallengeOptions.TtlMinutes"/> rather than given a number of its own: an offer and a
    /// challenge link are the same object seen from two sides, an unsolicited "do you want to play me"
    /// sitting in someone else's inbox, and the hour that file already argues for (long enough to walk
    /// away and come back, short enough that a stale one dies the same evening) is the identical
    /// judgement. Without a bound, every game either player has ever played stays an open channel to
    /// push-notify them, forever.
    /// </summary>
    private TimeSpan OfferWindow => TimeSpan.FromMinutes(_challenges.TtlMinutes);

    /// <summary>
    /// Records that <paramref name="byUserId"/> wants a rematch of <paramref name="gameId"/>. Requires
    /// the game to exist, be finished, have <paramref name="byUserId"/> as one of its two distinct
    /// players (a solo/bot game, same id both sides, has no one to rematch), and to have finished
    /// inside <see cref="OfferWindow"/>.
    ///
    /// <b>Not idempotent, and that is the fix, not an oversight.</b> This used to let any repeat call
    /// overwrite <see cref="RematchOffer.OfferedById"/> and <see cref="RematchOffer.OfferedAt"/> in
    /// place, which produced two distinct bugs that look unrelated and are the same line:
    ///
    /// <list type="bullet">
    /// <item>P-3-1: the recipient of the "accept the rematch" mail lands on the finished game's page
    /// and, if their client does not know an offer is standing, presses Offer. That overwrote the
    /// offer to point back at them, and <see cref="PeekOpenOfferAsync"/> refuses to let anyone accept
    /// their own offer, so the pair ping-pong forever and neither can ever accept. A state that was
    /// recoverable (someone only had to press Accept) became one that is not.</item>
    /// <item>C-6-3: each overwrite re-armed a fresh notification, so N calls produced N mails and N
    /// pushes for one offer.</item>
    /// </list>
    ///
    /// So a standing unaccepted offer is now answered, never rewritten: the other player is told to
    /// accept it, and the player who made it is told it is already out. An offer on an
    /// already-accepted game stays a silent no-op (the accept answered it) and reports
    /// <see cref="RematchOfferOutcome.IsNewOffer"/> false.
    /// </summary>
    public async Task<RematchOfferOutcome> OfferAsync(Guid gameId, Guid byUserId, CancellationToken ct = default)
    {
        var game = await _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId, ct)
            ?? throw new InvalidGameActionException("Game not found.");
        if (game.Result == GameResult.Ongoing)
            throw new InvalidGameActionException("This game is still in progress.");
        if (game.WhitePlayerId == game.BlackPlayerId)
            throw new InvalidGameActionException("Not a player in this game.");
        if (byUserId != game.WhitePlayerId && byUserId != game.BlackPlayerId)
            throw new InvalidGameActionException("Not a player in this game.");

        // Wall clock, not monotonic: EndedAt is a persisted instant written by another process on
        // another day, and only a wall clock means anything across that boundary (IGameClock's own
        // "wall clock for instants" rule). Injected rather than read off DateTimeOffset.UtcNow so the
        // window is testable without waiting an hour. EndedAt is set with Result on every real
        // finish; CreatedAt is the conservative fallback for a row that somehow lacks one, since it
        // can only ever make the window stricter.
        var now = _clock.UtcNow;
        var finishedAt = game.EndedAt ?? game.CreatedAt;
        if (now - finishedAt > OfferWindow)
            throw new InvalidGameActionException("That game finished too long ago to rematch. Start a new game instead.");

        var opponentId = byUserId == game.WhitePlayerId ? game.BlackPlayerId : game.WhitePlayerId;

        // AsNoTracking, unlike before: this method no longer writes to an existing row (that
        // overwrite was the bug), it only reads one to decide. Tracking it would hand back a cached
        // copy from earlier in the same scope, and the one field that decides the branch below is
        // AcceptedAt, which TryClaimAsync sets through ExecuteUpdateAsync - a statement the change
        // tracker never sees. A tracked read here reports an accepted offer as still open.
        var existing = await _db.RematchOffers.AsNoTracking().FirstOrDefaultAsync(r => r.GameId == gameId, ct);
        if (existing is null)
        {
            _db.RematchOffers.Add(new RematchOffer { GameId = gameId, OfferedById = byUserId, OfferedAt = now });
            await _db.SaveChangesAsync(ct);
            await RecordRematchCounterAsync(CounterKeys.RematchOffer, ct);
            return new RematchOfferOutcome(opponentId, IsNewOffer: true);
        }

        if (existing.AcceptedAt is not null)
            return new RematchOfferOutcome(opponentId, IsNewOffer: false);

        // The other player got here first. Their offer is the live one and this caller is the one who
        // can end this, so say so rather than turning the offer around.
        if (existing.OfferedById != byUserId)
            throw new InvalidGameActionException("Your opponent has already offered a rematch. Accept it instead.");

        // This caller's own offer is already standing and unanswered. There is nothing a second one
        // could add: the row already says exactly this, the opponent has already been told once, and
        // the only thing a repeat ever bought was another notification.
        throw new InvalidGameActionException("You have already offered a rematch on this game.");
    }

    /// <summary>
    /// Looks up (without claiming) whether <paramref name="gameId"/> has a live offer <paramref name="byUserId"/>
    /// may accept: it exists, is unaccepted, was not offered by this same caller, and this caller is
    /// one of the original game's two players. Null on any failure of those.
    ///
    /// Split from <see cref="TryClaimAsync"/> rather than one method, so <see cref="GameHub.AcceptRematch"/>
    /// can run <see cref="PlayEligibilityPolicy"/> (account ban / already-in-a-game / rated-eligibility)
    /// against the two real players in between - the same order <c>ChallengeEndpoints</c> uses (eligibility,
    /// then the guarded claim, then create), and for the same reason: claiming before checking eligibility
    /// would spend the one live offer on a rejected caller, and checking after creating the game is too
    /// late to matter.
    /// </summary>
    public async Task<RematchCandidate?> PeekOpenOfferAsync(Guid gameId, Guid byUserId, CancellationToken ct = default)
    {
        var offer = await _db.RematchOffers.AsNoTracking().FirstOrDefaultAsync(r => r.GameId == gameId, ct);
        if (offer is null || offer.AcceptedAt is not null) return null;
        if (offer.OfferedById == byUserId) return null; // can't accept your own offer

        var game = await _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null) return null;
        if (byUserId != game.WhitePlayerId && byUserId != game.BlackPlayerId) return null;

        return new RematchCandidate(gameId, game.WhitePlayerId, game.BlackPlayerId, game.IsRated);
    }

    /// <summary>
    /// The offerer of a still-unaccepted rematch on <paramref name="gameId"/>, or null. Unlike
    /// <see cref="PeekOpenOfferAsync"/> this returns the id even when <paramref name="asUserId"/> is
    /// the offerer, so the page can show "waiting" rather than Offer. Returns null for a caller who
    /// is not one of the two players, so a finished-game read that somehow skipped the participant
    /// check cannot learn who offered.
    /// </summary>
    public async Task<Guid?> PeekStandingOfferedByIdAsync(Guid gameId, Guid asUserId, CancellationToken ct = default)
    {
        var game = await _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null) return null;
        if (asUserId != game.WhitePlayerId && asUserId != game.BlackPlayerId) return null;

        return await _db.RematchOffers.AsNoTracking()
            .Where(r => r.GameId == gameId && r.AcceptedAt == null)
            .Select(r => (Guid?)r.OfferedById)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Claims the one live offer on <paramref name="gameId"/> for <paramref name="byUserId"/>, exactly
    /// once. Null (a caller who reaches this having already passed <see cref="PeekOpenOfferAsync"/> and
    /// eligibility loses only to a genuine race, not its own eligibility) when a racing accept claimed
    /// the offer first, or the game has vanished since the peek above.
    /// </summary>
    public async Task<RematchAcceptance?> TryClaimAsync(Guid gameId, Guid byUserId, CancellationToken ct = default)
    {
        // The guarded single-statement claim (see class doc comment): only the first caller to reach
        // this WHERE clause updates a row, every racing caller after it updates zero.
        var now = DateTimeOffset.UtcNow;
        var claimed = await _db.RematchOffers
            .Where(r => r.GameId == gameId && r.AcceptedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.AcceptedAt, now)
                .SetProperty(r => r.AcceptedById, byUserId), ct);
        if (claimed == 0) return null;

        var game = await _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null) return null;

        await RecordRematchCounterAsync(CounterKeys.RematchAccept, ct);

        // Colours swap: whoever was White in the finished game is Black in the rematch, and vice versa.
        return new RematchAcceptance(
            game.ConfigSnapshot.Clone(), game.BlackPlayerId, game.WhitePlayerId, game.RatingPool, game.IsRated);
    }

    /// <summary>
    /// Best-effort DailyCounters write. A counter fault must never fail Offer or Accept.
    /// </summary>
    private async Task RecordRematchCounterAsync(string key, CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await DailyCounterRecorder.RecordAsync(_db, today, key, CounterSources.Direct, 1, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record rematch counters; the rematch action itself succeeded.");
        }
    }

    /// <summary>Records the game the accepted offer produced. Best-effort audit trail only, nothing reads it back.</summary>
    public async Task RecordNewGameAsync(Guid gameId, Guid newGameId, CancellationToken ct = default) =>
        await _db.RematchOffers.Where(r => r.GameId == gameId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.NewGameId, newGameId), ct);
}

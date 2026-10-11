using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace KiriChess.LoadTest;

/// <summary>
/// One account: HTTP register, both hub connections, and the play loop. Mirrors what
/// ui/lib/realtime.ts plus the Board component do with the same MovePlayed/JoinGame payloads - no
/// chess logic, no FEN parsing, just LegalMoves off the wire and a per-game seeded RNG
/// (README § Decision 2).
/// </summary>
internal sealed class BotPlayer
{
    private readonly int _index;
    private readonly HttpClient _http;
    private readonly LoadOptions _options;
    private readonly GameCoordinator _coordinator;
    private readonly int _baseSeed;

    private string _token = "";
    private HubConnection? _gameConn;
    private HubConnection? _presenceConn;

    // Per-game state. Only ever touched from within this bot's own hub-message handlers, which
    // SignalR's client dispatches sequentially for a single connection, so no lock is needed beyond
    // the CompareExchange guard against re-entrant scheduling of PlayTurnAsync.
    private Guid _currentGameId;
    private string? _myColor;
    private bool _inGame;
    private string? _pendingUci;
    private long _pendingStartTicks;
    private int _thinking;
    private Random? _gameRng;
    private int _gameCounter;

    public string DisplayName { get; }
    public string Email { get; }

    public BotPlayer(int index, HttpClient http, LoadOptions options, GameCoordinator coordinator)
    {
        _index = index;
        _http = http;
        _options = options;
        _coordinator = coordinator;
        _baseSeed = options.Seed;
        Email = $"loadbot{index:D6}@load.invalid";
        DisplayName = $"loadbot{index:D6}";
    }

    private static string Password(int index) => $"LoadBot!{index}Aa1";

    /// <summary>Step 1: POST /api/auth/register. A 400 here fails the whole run loudly - see
    /// Program.cs, which does not swallow this exception.</summary>
    public async Task RegisterAsync(CancellationToken ct)
    {
        var body = new
        {
            email = Email,
            password = Password(_index),
            displayName = DisplayName,
            acceptedTerms = true,
            confirmedMinimumAge = true
        };

        using var resp = await _http.PostAsJsonAsync($"{_options.Api}/api/auth/register", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Registration failed for {Email}: {(int)resp.StatusCode} {resp.StatusCode} - {text}");
        }

        var auth = await resp.Content.ReadFromJsonAsync<AuthResponse>(JsonDefaults.Options, ct)
            ?? throw new InvalidOperationException($"Registration for {Email} returned an empty body.");
        _token = auth.Token;
    }

    /// <summary>Step 2: open the game hub connection (and, by default, the presence hub connection),
    /// and wire every handler the server can call on this connection.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _gameConn = new HubConnectionBuilder()
            .WithUrl($"{_options.Api}/hubs/game", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult(_token)!;
            })
            .Build();

        // Mandatory client result the server's LatencyProbe calls (README § Decision 1). A load
        // client that cannot answer this measures a different, worse server: an unanswered probe
        // holds a linked CancellationTokenSource open for Clock:ProbeTimeoutSeconds per connection
        // per interval.
        _gameConn.On("Ping", () =>
        {
            _coordinator.RecordPingAnswered();
            return 1L;
        });

        _gameConn.On<MatchFoundDto>("MatchFound", async dto => await OnMatchFoundAsync(dto, ct));
        _gameConn.On<MovePlayedDto>("MovePlayed", async dto => await OnMovePlayedAsync(dto, ct));
        _gameConn.On<JsonElement>("GameEnded", async _ => await OnGameEndedAsync(ct));

        // No-ops so nothing logs a warning about an un-handled server call.
        _gameConn.On<JsonElement>("DrawOffered", _ => Task.CompletedTask);
        _gameConn.On<JsonElement>("SpectatorsChanged", _ => Task.CompletedTask);
        _gameConn.On<JsonElement>("OpponentDisconnected", _ => Task.CompletedTask);
        _gameConn.On("OpponentReconnected", () => { });

        _gameConn.Closed += _ =>
        {
            _coordinator.RecordReconnectOrClose();
            return Task.CompletedTask;
        };

        await _gameConn.StartAsync(ct);

        if (_options.Presence)
        {
            _presenceConn = new HubConnectionBuilder()
                .WithUrl($"{_options.Api}/hubs/presence", o =>
                {
                    o.AccessTokenProvider = () => Task.FromResult(_token)!;
                })
                .Build();
            _presenceConn.On<JsonElement>("PresenceChanged", _ => Task.CompletedTask);
            _presenceConn.Closed += _ =>
            {
                _coordinator.RecordReconnectOrClose();
                return Task.CompletedTask;
            };
            await _presenceConn.StartAsync(ct);
        }
    }

    /// <summary>Step 3: JoinQueue.</summary>
    public async Task JoinQueueAsync(CancellationToken ct)
    {
        if (_gameConn is null) throw new InvalidOperationException("Not connected.");
        await _gameConn.InvokeAsync("JoinQueue", _options.Pool, ct);
    }

    private async Task OnMatchFoundAsync(MatchFoundDto dto, CancellationToken ct)
    {
        _coordinator.RecordMatchFound();
        _currentGameId = dto.GameId;
        _myColor = dto.Color;
        _inGame = true;
        _gameRng = new Random(HashCode.Combine(_baseSeed, _index, _gameCounter++));

        try
        {
            var snapshot = await _gameConn!.InvokeAsync<GameSnapshotDto>("JoinGame", dto.GameId.ToString(), ct);
            MaybeScheduleTurn(snapshot.State.SideToMove, snapshot.LegalMoves, dto.GameId);
        }
        catch (Exception ex)
        {
            _coordinator.RecordError(Categorize(ex));
        }
    }

    private async Task OnMovePlayedAsync(MovePlayedDto dto, CancellationToken ct)
    {
        if (_pendingUci is not null)
        {
            if (dto.Uci == _pendingUci)
            {
                var elapsedMs = Stopwatch_ElapsedMs(_pendingStartTicks);
                _coordinator.RecordMoveLatency(elapsedMs);
            }
            else
            {
                // Should not happen: only this bot can move between its own SubmitMove and the next
                // broadcast, since SideToMove flips to the opponent immediately after. A mismatch here
                // is a finding, not a silent skip.
                _coordinator.RecordError("latency-uci-mismatch");
            }
            _pendingUci = null;
        }

        if (dto.GameOver)
        {
            // Deviation from the task file's literal step 5 ("If LegalMoves is empty the game is
            // over; wait for GameEnded"): reading GameActionExecutor.SubmitMoveLocalAsync and
            // FlagFallMonitor.cs shows that is not how the protocol actually works. GameEndedDto's
            // own doc comment says it is broadcast only "on a non-move terminal transition (resign /
            // agreed draw / abandon / flag-fall)" - a game that ends BY a move (checkmate, stalemate,
            // threefold) signals game-over exclusively through this MovePlayedDto.GameOver flag and
            // NEVER gets a follow-up "GameEnded" broadcast (FlagFallMonitor's sweep sees the session
            // already persisted-over and skips re-announcing it). Waiting for "GameEnded" here would
            // strand both players forever on any game that ends by an actual chess result rather than
            // the clock or a disconnect - silently decaying the live-game count as the ladder runs.
            // See loadtest/README.md and the phase report for this finding.
            await EndCurrentGameAsync(ct);
            return;
        }

        MaybeScheduleTurn(dto.State.SideToMove, dto.LegalMoves, dto.State.GameId);
        await Task.CompletedTask;
    }

    private async Task OnGameEndedAsync(CancellationToken ct) => await EndCurrentGameAsync(ct);

    /// <summary>
    /// Shared end-of-game handling for both signals the server can end a game with (see the long
    /// comment in <see cref="OnMovePlayedAsync"/>): re-entrant-safe via the <c>_inGame</c> check, in
    /// case a future server change ever sent both for the same transition.
    /// </summary>
    private async Task EndCurrentGameAsync(CancellationToken ct)
    {
        if (!_inGame) return;
        _coordinator.RecordGameEnded();
        _inGame = false;
        _pendingUci = null;

        // Step 6: wait 1-2s (jittered) and re-queue, so live-game count holds near N instead of
        // decaying as random-legal play finishes games.
        var delayMs = Random.Shared.Next(1000, 2000);
        try
        {
            await Task.Delay(delayMs, ct);
            if (_gameConn is { State: HubConnectionState.Connected })
                await _gameConn.InvokeAsync("JoinQueue", _options.Pool, ct);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _coordinator.RecordError(Categorize(ex));
        }
    }

    private void MaybeScheduleTurn(string sideToMove, IReadOnlyList<string> legalMoves, Guid gameId)
    {
        if (!string.Equals(sideToMove, _myColor, StringComparison.Ordinal)) return;
        if (legalMoves.Count == 0) return; // Game is over; wait for GameEnded.
        if (Interlocked.CompareExchange(ref _thinking, 1, 0) != 0) return; // Already scheduled.

        _ = PlayTurnAsync(gameId, legalMoves.ToArray(), _gameRng!);
    }

    private async Task PlayTurnAsync(Guid gameId, IReadOnlyList<string> legalMoves, Random rng)
    {
        try
        {
            var jitter = _options.JitterMs > 0 ? rng.Next(-_options.JitterMs, _options.JitterMs + 1) : 0;
            var delayMs = Math.Max(0, _options.ThinkMs + jitter);
            await Task.Delay(delayMs);

            if (!_inGame) return; // Game ended while we were thinking.

            var move = legalMoves[rng.Next(legalMoves.Count)];
            _pendingUci = move;
            _pendingStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _coordinator.RecordMoveSubmitted();

            await _gameConn!.InvokeAsync("SubmitMove", gameId.ToString(), move);
        }
        catch (Exception ex)
        {
            _pendingUci = null;
            _coordinator.RecordError(Categorize(ex));
        }
        finally
        {
            Interlocked.Exchange(ref _thinking, 0);
        }
    }

    private static double Stopwatch_ElapsedMs(long startTicks)
    {
        var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
        return elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static string Categorize(Exception ex) => ex switch
    {
        HubException => "hub-exception",
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        HttpRequestException => "http",
        _ => "other:" + ex.GetType().Name
    };

    /// <summary>Step 7: LeaveQueue if not in a game, then dispose every connection.</summary>
    public async Task ShutdownAsync()
    {
        try
        {
            if (_gameConn is { State: HubConnectionState.Connected } && !_inGame)
                await _gameConn.InvokeAsync("LeaveQueue");
        }
        catch { /* best-effort on shutdown */ }

        if (_gameConn is not null) await _gameConn.DisposeAsync();
        if (_presenceConn is not null) await _presenceConn.DisposeAsync();
    }
}

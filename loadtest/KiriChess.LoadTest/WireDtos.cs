namespace KiriChess.LoadTest;

// Minimal, local wire records - only what the harness actually consumes. No project reference to
// KiriChess.Server: the server's real DTOs (Games/RealtimeDtos.cs, Games/GameDtos.cs,
// Auth/AuthDtos.cs) carry more fields than these; System.Text.Json ignores what a record's
// constructor does not ask for. See Phase 9b.3 § Decision 2 for why the harness
// deliberately does not share types (or chess logic) with the server it is measuring.

/// <summary>Response of POST /api/auth/register. The server's AuthResponse also carries RefreshToken,
/// ExpiresAt and Roles - unused here, so they are omitted.</summary>
internal sealed record AuthResponse(Guid UserId, string Email, string DisplayName, string Token);

/// <summary>Subset of the server's GameStateResponse: only what decides whose turn it is and whether
/// the game is over.</summary>
internal sealed record GameStateDto(
    Guid GameId, string SideToMove, int WhiteTime, int BlackTime, string Status,
    Guid WhitePlayerId, Guid BlackPlayerId);

/// <summary>Full state pushed by JoinGame.</summary>
internal sealed record GameSnapshotDto(GameStateDto State, IReadOnlyList<string> LegalMoves);

/// <summary>Broadcast after every move.</summary>
internal sealed record MovePlayedDto(
    GameStateDto State, string Uci, IReadOnlyList<string> LegalMoves, bool GameOver);

/// <summary>Sent to each matched player when matchmaking pairs them.</summary>
internal sealed record MatchFoundDto(Guid GameId, string Color, string Pool);

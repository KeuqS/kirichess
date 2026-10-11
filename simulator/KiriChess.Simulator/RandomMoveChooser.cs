namespace KiriChess.Simulator;

/// <summary>
/// Seeded random-legal picker, the same shape as engine <c>SelfPlay</c>. Useful for clock-economy
/// smoke runs that should not wait on Stockfish, and as the no-binary fallback.
/// </summary>
public sealed class RandomMoveChooser : IMoveChooser
{
    public string Name => "Random";

    public Task<string> ChooseAsync(
        string fen,
        int elo,
        IReadOnlyList<string> legalUci,
        Random rng,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = fen;
        _ = elo;
        return Task.FromResult(BotSkill.PickRandomMove(legalUci, rng));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

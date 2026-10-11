namespace KiriChess.Simulator;

/// <summary>Picks a legal UCI move for the side to move. One implementation per engine process.</summary>
public interface IMoveChooser : IAsyncDisposable
{
    string Name { get; }

    Task<string> ChooseAsync(
        string fen,
        int elo,
        IReadOnlyList<string> legalUci,
        Random rng,
        CancellationToken cancellationToken);
}

using System.Collections.Concurrent;

namespace KiriChess.Server.Games;

/// <summary>
/// I-1-6 / AS-8. Distinct players seated in live games, updated on create/restore and end/evict
/// instead of allocating a LINQ pass over every session on the presence tick.
/// </summary>
internal sealed class LivePlayerSet : IReadOnlySet<Guid>
{
    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    public int Count => _counts.Count;

    public bool Contains(Guid item) => _counts.ContainsKey(item);

    public void Add(Guid white, Guid black)
    {
        Increment(white);
        if (black != white)
            Increment(black);
    }

    public void Remove(Guid white, Guid black)
    {
        Decrement(white);
        if (black != white)
            Decrement(black);
    }

    private void Increment(Guid id) =>
        _counts.AddOrUpdate(id, 1, static (_, n) => n + 1);

    private void Decrement(Guid id)
    {
        while (true)
        {
            if (!_counts.TryGetValue(id, out var n))
                return;
            if (n <= 1)
            {
                if (_counts.TryRemove(KeyValuePair.Create(id, n)))
                    return;
            }
            else if (_counts.TryUpdate(id, n - 1, n))
            {
                return;
            }
        }
    }

    public IEnumerator<Guid> GetEnumerator()
    {
        foreach (var pair in _counts)
            yield return pair.Key;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private HashSet<Guid> Snapshot() => [.. _counts.Keys];

    public bool IsSubsetOf(IEnumerable<Guid> other) => Snapshot().IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<Guid> other) => Snapshot().IsSupersetOf(other);

    public bool IsProperSubsetOf(IEnumerable<Guid> other) => Snapshot().IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<Guid> other) => Snapshot().IsProperSupersetOf(other);

    public bool Overlaps(IEnumerable<Guid> other) => Snapshot().Overlaps(other);

    public bool SetEquals(IEnumerable<Guid> other) => Snapshot().SetEquals(other);
}

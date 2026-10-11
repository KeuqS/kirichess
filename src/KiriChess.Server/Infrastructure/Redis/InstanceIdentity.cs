namespace KiriChess.Server.Infrastructure.Redis;

/// <summary>
/// This process's identity in the instance set (Phase 8.4). Stable for the lifetime of the process
/// and never reused: game ownership, the reply channel of the forwarding RPC and the presence sets
/// are all keyed on it, so two processes sharing an id would silently corrupt all three.
///
/// Prefers Fly's machine id when present (that is the identity the platform already uses and the one
/// a human reading logs will recognise), and falls back to machine name plus a short random suffix,
/// the suffix matters because two containers on one host share a machine name.
/// </summary>
public sealed class InstanceIdentity
{
    public string Id { get; }

    public InstanceIdentity()
    {
        var fly = Environment.GetEnvironmentVariable("FLY_MACHINE_ID");
        Id = !string.IsNullOrWhiteSpace(fly)
            ? fly
            : $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>Test seam: an explicit id, for the multi-instance tests that fake two processes.</summary>
    public InstanceIdentity(string id) => Id = id;
}

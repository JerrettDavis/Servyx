using Servyx.Domain.Connectors;

namespace Servyx.Domain.Transport;

/// <summary>
/// Reads the write posture an <see cref="IExecutionTarget"/> is carrying, so an adapter can refuse an
/// operation up front instead of discovering the refusal partway through it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a second look at the same policy, never a second policy.</strong>
/// <see cref="WriteGuardedExecutionTarget"/> is the structural gate: it refuses every write, and every
/// command not declared <see cref="CommandIntent.ReadOnly"/>, before any I/O happens. What it cannot do is
/// refuse <em>early</em> — a backup that quiesces the server, reserves an archive name and only then meets
/// the guard has already touched things, and an operator reading that failure learns about <c>tar</c> rather
/// than about the write mode. Adapters use this type to surface the refusal the guard would make anyway, at
/// the top of the operation and with a message about the operation.
/// </para>
/// <para>
/// <strong>It also covers the one thing no transport guard can reach.</strong> An adapter step that never
/// travels a transport at all — the local process provisioner's <c>ensure-dir</c> verb is a
/// <c>Directory.CreateDirectory</c> call in this very process — has no seam for a decorator to
/// sit at. Consulting the posture that the target the adapter was handed is carrying is the only check
/// available there, which is why this lives in one shared place rather than being re-derived per adapter.
/// </para>
/// <para>
/// <strong>An unguarded target answers <see langword="null"/> and is allowed through.</strong> Absence of a
/// guard is not a claim about the server; inventing a refusal here for a target the composition root chose
/// not to guard would make this type a policy rather than a reader of one.
/// </para>
/// </remarks>
public static class ExecutionTargetWriteMode
{
    /// <summary>
    /// The write posture <paramref name="target"/> carries, looking through a composite to whichever half
    /// would perform the mutation, or <see langword="null"/> when no guard is present anywhere in it.
    /// </summary>
    /// <param name="target">The target to inspect. Never mutated, never called.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    public static WriteMode? Resolve(IExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target switch
        {
            WriteGuardedExecutionTarget guarded => guarded.Mode,
            ICompositeExecutionTarget composite => ResolveComposite(composite),
            _ => null,
        };
    }

    /// <summary>
    /// Throws <see cref="WritesDisabledException"/> when <paramref name="target"/> carries a posture other
    /// than <see cref="WriteMode.Enabled"/>, naming the operation and the server in the refusal.
    /// </summary>
    /// <param name="target">The target the operation would mutate through.</param>
    /// <param name="operation">
    /// The operation, phrased to follow "Refusing to " — e.g. <c>"create a backup"</c>.
    /// </param>
    /// <param name="serverId">The server the operation belongs to, named in the refusal.</param>
    /// <param name="stillAvailable">
    /// An optional sentence appended to the refusal listing what the caller <em>can</em> still do, so a
    /// read-only operator is told the shape of the tier rather than only what was denied.
    /// </param>
    /// <exception cref="WritesDisabledException">The resolved posture is not <see cref="WriteMode.Enabled"/>.</exception>
    public static void RequireWritesEnabled(
        IExecutionTarget target,
        string operation,
        string serverId,
        string? stillAvailable = null)
    {
        if (Resolve(target) is not { } mode || mode == WriteMode.Enabled)
        {
            return;
        }

        var tail = string.IsNullOrEmpty(stillAvailable) ? string.Empty : " " + stillAvailable;
        throw new WritesDisabledException(
            $"Refusing to {operation} for server '{serverId}': the server's write mode is {mode}. " +
            $"Writes require {nameof(WriteMode)}.{nameof(WriteMode.Enabled)}, set per server and never globally." +
            tail);
    }

    /// <remarks>
    /// Either half being read-only is enough to refuse: a caller that guarded only one half still meant
    /// "this server does not mutate", and an operation that needs both halves cannot be half-permitted.
    /// The modes are ordered least-permissive first, so the minimum is the stricter posture.
    /// </remarks>
    private static WriteMode? ResolveComposite(ICompositeExecutionTarget composite)
    {
        var file = composite.FileTarget is null ? null : Resolve(composite.FileTarget);
        var exec = composite.ExecTarget is null ? null : Resolve(composite.ExecTarget);

        return (file, exec) switch
        {
            (null, null) => null,
            (not null, null) => file,
            (null, not null) => exec,
            _ => (WriteMode)Math.Min((int)file!.Value, (int)exec!.Value),
        };
    }
}

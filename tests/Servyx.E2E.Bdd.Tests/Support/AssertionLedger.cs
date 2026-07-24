namespace Servyx.E2E.Bdd.Tests.Support;

/// <summary>
/// Per-scenario counter of business assertions actually made. Registered fresh into Reqnroll's
/// <c>IObjectContainer</c> for every scenario (see <see cref="ScenarioHooks"/>) so scenarios never share
/// state. <see cref="ScenarioHooks"/> fails any scenario that "passes" with a zero count — a scenario
/// whose every <c>[Then]</c> step forgot to call <see cref="Record"/> would otherwise pass having asserted
/// nothing at all.
/// </summary>
public sealed class AssertionLedger
{
    private int _count;

    public int Count => _count;

    public void Record() => Interlocked.Increment(ref _count);
}

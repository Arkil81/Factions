using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Serialises every test class that writes <see cref="Factions.Core.DebugTraces"/>' per-process
    /// statics, and keeps them off the same wall-clock as the rest of the suite.
    ///
    /// <para>xUnit runs test classes in parallel by default. Each of these classes both flips a trace
    /// flag and calls <c>DebugTraces.ResetToDefaults()</c> from <c>Dispose</c>, so one class's
    /// teardown lands inside another class's assertion. <c>DebugTracesTests</c> is the one that
    /// notices: it snapshots every flag, toggles one by name, then re-counts to prove exactly one
    /// moved — and a concurrent <c>ResetToDefaults</c> puts the flag back before that recount, so the
    /// count reads 0.</para>
    ///
    /// <para><see cref="CollectionDefinitionAttribute.DisableParallelization"/> rather than a bare
    /// collection: the flags are read by production code (<c>ProductionManager</c> consults
    /// <c>DebugTraces.SlotGate</c> on every bank), so an unrelated manager test running concurrently
    /// with a flag held on would collect log lines it never asked for. Sharing a collection only
    /// serialises these classes against each other; disabling parallelisation serialises them against
    /// the whole suite, which is what the statics actually require.</para>
    ///
    /// <para>Observed as a real failure — <c>bankdenials</c>, "Expected: 1 Actual: 0" — only once a
    /// fourth toggling test was added to <c>SlotGateTelemetryTests</c>. The hazard predates that
    /// test; it was a matter of timing, and the base branch passes only by luck of scheduling.</para>
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class DebugTracesCollection
    {
        public const string Name = "DebugTraces per-process statics";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-435: the patch pass both plugins run instead of <c>Harmony.PatchAll</c>. A class that fails
    /// to patch must cost its own group and nothing else — on the server, <c>PatchAll</c>'s rethrow
    /// ended the Torch process (2026-08-30, v0.92.0).
    ///
    /// <para>Every failure here is produced the way an SE update produces one: a target that does not
    /// resolve, and a prefix argument bound to a parameter name the method does not have.</para>
    /// </summary>
    public sealed class HarmonyPatcherTests : IDisposable
    {
        private readonly Harmony _harmony = new Harmony("factions.tests.harmonypatcher." + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            _harmony.UnpatchAll(_harmony.Id);
        }

        private HarmonyPatcher.Report Run()
        {
            PatchOrder.Calls.Clear();
            // A non-patch type in the list, as an assembly scan would hand it over.
            return HarmonyPatcher.Patch(_harmony, new[]
            {
                typeof(PatchTargets),
                typeof(CoupledFirst),
                typeof(CoupledMissing),
                typeof(CoupledAfter),
                typeof(Independent),
                typeof(Declined),
                typeof(NestedFamily.Live),
                typeof(NestedFamily.Missing),
                typeof(MultiTarget),
                typeof(OrderSecond),
                typeof(OrderFirst),
            });
        }

        private bool HasOurPatch(string method)
        {
            Patches info = Harmony.GetPatchInfo(AccessTools.Method(typeof(PatchTargets), method));
            if (info == null) return false;
            return info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                       .Any(p => p.owner == _harmony.Id);
        }

        [Fact]
        public void A_failing_class_rolls_back_its_group_and_nothing_else()
        {
            HarmonyPatcher.Report report = Run();

            // The independent class went on regardless of three failed groups around it.
            Assert.Equal(2, PatchTargets.Second());
            Assert.True(HarmonyPatcher.IsLive(typeof(Independent)));

            // TestCoupled: the class applied before the failure is undone, the one after never tried.
            Assert.Equal(1, PatchTargets.First());
            Assert.Equal(3, PatchTargets.Third());
            Assert.False(HasOurPatch(nameof(PatchTargets.First)));
            Assert.False(HasOurPatch(nameof(PatchTargets.Third)));
            Assert.False(HarmonyPatcher.IsLive(typeof(CoupledFirst)));

            HarmonyPatcher.GroupFailure coupled = report.Failures.Single(f => f.Group == "TestCoupled");
            Assert.Equal(nameof(CoupledMissing), coupled.FailedClass);
            Assert.Equal(new[] { nameof(CoupledFirst) }, coupled.RolledBack);
            Assert.Equal(new[] { nameof(CoupledAfter) }, coupled.NotAttempted);
            Assert.Null(coupled.RollbackError);
            Assert.NotNull(coupled.Exception);
            Assert.Contains("Exception", coupled.Reason);
            Assert.DoesNotContain("TargetInvocationException", coupled.Reason);
        }

        [Fact]
        public void Nested_classes_share_their_outer_type_s_group()
        {
            HarmonyPatcher.Report report = Run();

            HarmonyPatcher.GroupFailure nested = report.Failures.Single(f => f.Group == nameof(NestedFamily));
            Assert.Equal(nameof(NestedFamily.Missing), nested.FailedClass);
            Assert.Equal(new[] { nameof(NestedFamily.Live) }, nested.RolledBack);
            Assert.Equal(4, PatchTargets.Fourth());
            Assert.False(HasOurPatch(nameof(PatchTargets.Fourth)));
        }

        [Fact]
        public void A_class_that_fails_on_its_second_target_is_undone_on_its_first()
        {
            HarmonyPatcher.Report report = Run();

            // Harmony patched Fifth, then threw binding the postfix's "x" to Sixth(int y).
            HarmonyPatcher.GroupFailure multi = report.Failures.Single(f => f.Group == nameof(MultiTarget));
            Assert.Empty(multi.RolledBack);
            Assert.Equal(5, PatchTargets.Fifth(0));
            Assert.False(HasOurPatch(nameof(PatchTargets.Fifth)));
            Assert.False(HarmonyPatcher.IsLive(typeof(MultiTarget)));
        }

        [Fact]
        public void Classes_go_on_in_the_order_given_not_grouped()
        {
            Run();

            // Equal-priority prefixes run in the order they were added; reordering the pass would
            // reorder every shared target's prefixes.
            PatchTargets.Seventh();
            Assert.Equal(new[] { "second", "first" }, PatchOrder.Calls);
        }

        [Fact]
        public void The_report_counts_every_class_once()
        {
            HarmonyPatcher.Report report = Run();

            // Ten patch classes; PatchTargets is not one. Groups: TestCoupled, Independent, Declined,
            // NestedFamily, MultiTarget, OrderSecond, OrderFirst.
            Assert.Equal(10, report.Classes);
            Assert.Equal(7, report.Groups);
            Assert.Equal(3, report.Live);
            Assert.Equal(1, report.Declined);
            Assert.Equal(6, report.FailedClasses);
            Assert.Equal(report.Classes, report.Live + report.Declined + report.FailedClasses);
            Assert.Equal(3, report.Failures.Count);
            Assert.False(HarmonyPatcher.IsLive(typeof(Declined)));
            Assert.Equal(6, PatchTargets.Sixth(0));

            Assert.Equal("Harmony: 10 patch classes in 7 groups; 3 live, 1 declined by Prepare, "
                       + "3 group(s) failed (6 classes off).", report.Summary());
            Assert.StartsWith("Harmony patch group TestCoupled NOT applied: CoupledMissing threw (",
                              report.Failures.Single(f => f.Group == "TestCoupled").Describe());
            Assert.Contains("; rolled back 1: CoupledFirst; not attempted 1: CoupledAfter.",
                            report.Failures.Single(f => f.Group == "TestCoupled").Describe());
        }

        [Fact]
        public void An_all_good_pass_reports_no_failures()
        {
            HarmonyPatcher.Report report = HarmonyPatcher.Patch(_harmony, new[] { typeof(Independent) });

            Assert.Empty(report.Failures);
            Assert.Equal(1, report.Live);
            Assert.Equal("Harmony: 1 patch classes in 1 groups; 1 live, 0 declined by Prepare, "
                       + "0 group(s) failed (0 classes off).", report.Summary());
        }

        [Fact]
        public void Describe_flattens_the_chain_without_reflection_wrappers()
        {
            // HarmonyException's (message, inner) constructor is not public; the shape is the same.
            var ex = new ArgumentException("Patching exception in method X",
                new TargetInvocationException(new InvalidOperationException("found 0 and 0\nsecond line")));

            Assert.Equal("ArgumentException: Patching exception in method X -> InvalidOperationException: found 0 and 0",
                         HarmonyPatcher.Describe(ex));
        }
    }

    internal static class PatchOrder
    {
        public static readonly List<string> Calls = new List<string>();
    }

    internal static class PatchTargets
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public static int First() { return 1; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Second() { return 1; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Third() { return 3; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Fourth() { return 4; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Fifth(int x) { return 5; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Sixth(int y) { return 6; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static void Seventh() { }
    }

    [HarmonyPatch(typeof(PatchTargets), nameof(PatchTargets.First))]
    [PatchGroup("TestCoupled")]
    internal static class CoupledFirst
    {
        private static void Postfix(ref int __result) { __result += 100; }
    }

    [HarmonyPatch(typeof(PatchTargets), "DoesNotExist")]
    [PatchGroup("TestCoupled")]
    internal static class CoupledMissing
    {
        private static void Prefix() { }
    }

    [HarmonyPatch(typeof(PatchTargets), nameof(PatchTargets.Third))]
    [PatchGroup("TestCoupled")]
    internal static class CoupledAfter
    {
        private static void Postfix(ref int __result) { __result += 100; }
    }

    [HarmonyPatch(typeof(PatchTargets), nameof(PatchTargets.Second))]
    internal static class Independent
    {
        private static void Postfix(ref int __result) { __result += 1; }
    }

    [HarmonyPatch(typeof(PatchTargets), nameof(PatchTargets.Sixth))]
    internal static class Declined
    {
        private static bool Prepare() { return false; }
        private static void Postfix(ref int __result) { __result += 100; }
    }

    internal static class NestedFamily
    {
        [HarmonyPatch(typeof(PatchTargets), nameof(PatchTargets.Fourth))]
        internal static class Live
        {
            private static void Postfix(ref int __result) { __result += 100; }
        }

        [HarmonyPatch(typeof(PatchTargets), "AlsoDoesNotExist")]
        internal static class Missing
        {
            private static void Prefix() { }
        }
    }

    [HarmonyPatch]
    internal static class MultiTarget
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PatchTargets), nameof(PatchTargets.Fifth));
            yield return AccessTools.Method(typeof(PatchTargets), nameof(PatchTargets.Sixth));
        }

        private static void Postfix(int x, ref int __result) { __result += 100; }
    }

    [HarmonyPatch(typeof(PatchTargets), nameof(PatchTargets.Seventh))]
    internal static class OrderFirst
    {
        private static void Prefix() { PatchOrder.Calls.Add("first"); }
    }

    [HarmonyPatch(typeof(PatchTargets), nameof(PatchTargets.Seventh))]
    internal static class OrderSecond
    {
        private static void Prefix() { PatchOrder.Calls.Add("second"); }
    }
}

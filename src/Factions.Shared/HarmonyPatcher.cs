using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Factions
{
    /// <summary>
    /// Puts a top-level Harmony class into a named patch group, so it is applied and rolled back with
    /// the other classes carrying the same name. A nested patch class needs no attribute: it already
    /// belongs to its outermost declaring type's group, so a family written as nested classes is one
    /// group by construction.
    ///
    /// <para>Group only classes that are correct together: one hands state to another, or they are
    /// two halves of one ledger or one measurement. Patches that merely share a subject (several
    /// refusals, several terminal services) stay apart, so one renamed SE member costs one of
    /// them rather than all.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class PatchGroupAttribute : Attribute
    {
        public PatchGroupAttribute(string name) { Name = name; }

        public string Name { get; private set; }
    }

    /// <summary>
    /// Applies an assembly's attribute-declared Harmony patches the way <c>Harmony.PatchAll</c> does,
    /// except that a class which fails costs only its own group: the group's already-applied classes
    /// are unpatched, its remaining classes are not attempted, and every other group carries on.
    ///
    /// <para><b>Why.</b> <c>PatchAll</c> stops at the first class that throws — a target that no longer
    /// resolves, a prefix argument bound to a renamed parameter, an ambiguous overload, a transpiler
    /// that refuses — and rethrows out of plugin init. Torch does not catch a plugin's <c>Init</c>, so
    /// on the server that throw ends the process (2026-08-30, v0.92.0: <c>[FATAL] Initializer</c>,
    /// minidump, no watchdog). Pulsar does catch it, but Harmony keeps every class patched before the
    /// miss while the rest of init never runs. FAC-435.</para>
    ///
    /// <para><b>Order is Harmony's.</b> Classes are applied in the assembly's type order, exactly as
    /// <c>PatchAll</c> applies them, because equal-priority patches on one method run in the order they
    /// were added. Groups decide what is rolled back, never what is applied first.</para>
    ///
    /// <para><b>Shared by both ends</b> so the rule cannot drift between them; each host logs the
    /// <see cref="Report"/> itself.</para>
    /// </summary>
    internal static class HarmonyPatcher
    {
        /// <summary>A group that did not go on: which class threw and why, and what that cost.</summary>
        internal sealed class GroupFailure
        {
            public string Group;
            public string FailedClass;
            public string Reason;
            public Exception Exception;

            /// <summary>Group classes applied before the failure and unpatched because of it.</summary>
            public readonly List<string> RolledBack = new List<string>();

            /// <summary>Group classes after the failing one, never attempted.</summary>
            public readonly List<string> NotAttempted = new List<string>();

            /// <summary>Null when the rollback removed every patch it looked for.</summary>
            public string RollbackError;

            public string Describe()
            {
                string text = "Harmony patch group " + Group + " NOT applied: " + FailedClass + " threw ("
                            + Reason + ")";
                if (RolledBack.Count > 0)
                    text += "; rolled back " + RolledBack.Count + ": " + string.Join(", ", RolledBack.ToArray());
                if (NotAttempted.Count > 0)
                    text += "; not attempted " + NotAttempted.Count + ": " + string.Join(", ", NotAttempted.ToArray());
                if (RollbackError != null)
                    text += "; rollback incomplete (" + RollbackError + ")";
                return text + ".";
            }
        }

        internal sealed class Report
        {
            public int Classes;
            public int Groups;

            /// <summary>Classes with at least one patch in place when the pass ended.</summary>
            public int Live;

            /// <summary>Classes that completed without installing anything — a <c>[HarmonyPrepare]</c>
            /// that returned false.</summary>
            public int Declined;

            /// <summary>Classes off because their group failed: the thrower, the rolled back and the
            /// not attempted.</summary>
            public int FailedClasses;

            public readonly List<GroupFailure> Failures = new List<GroupFailure>();

            public string Summary()
            {
                return "Harmony: " + Classes + " patch classes in " + Groups + " groups; " + Live + " live, "
                     + Declined + " declined by Prepare, " + Failures.Count + " group(s) failed ("
                     + FailedClasses + " classes off).";
            }
        }

        // Replaced whole, never mutated after publication, so a reader on any thread sees one
        // consistent set without a lock.
        private static volatile HashSet<Type> _live = new HashSet<Type>();

        /// <summary>
        /// Whether <paramref name="patchClass"/> has a patch in place after the last pass. False for a
        /// class whose Prepare declined, whose own patching threw, or whose group was rolled back.
        /// This is the answer a <c>Bound</c> flag set inside <c>Prepare</c> cannot give: Prepare runs
        /// before anything is patched, and before a sibling's failure can undo it.
        /// </summary>
        internal static bool IsLive(Type patchClass)
        {
            return patchClass != null && _live.Contains(patchClass);
        }

        internal static Report PatchAll(Harmony harmony, Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException("assembly");
            return Patch(harmony, AccessTools.GetTypesFromAssembly(assembly));
        }

        /// <summary>Applies <paramref name="types"/> in the order given. Types that are not Harmony
        /// patch classes are skipped, as <c>PatchAll</c> skips them. Never throws for a patch.</summary>
        internal static Report Patch(Harmony harmony, IEnumerable<Type> types)
        {
            if (harmony == null) throw new ArgumentNullException("harmony");
            if (types == null) throw new ArgumentNullException("types");

            var report = new Report();
            var live = new HashSet<Type>(_live);
            var groupsSeen = new HashSet<string>();
            var applied = new Dictionary<string, List<Type>>();
            var failed = new Dictionary<string, GroupFailure>();
            var failedTypes = new List<Type>();

            foreach (Type type in types)
            {
                if (type == null || HarmonyMethodExtensions.GetFromType(type).Count == 0) continue;

                report.Classes++;
                string groupKey;
                string groupName = GroupOf(type, out groupKey);
                if (groupsSeen.Add(groupKey)) report.Groups++;

                GroupFailure failure;
                if (failed.TryGetValue(groupKey, out failure))
                {
                    failure.NotAttempted.Add(type.Name);
                    failedTypes.Add(type);
                    continue;
                }

                List<MethodInfo> replacements;
                try
                {
                    replacements = harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception ex)
                {
                    failure = new GroupFailure
                    {
                        Group = groupName,
                        FailedClass = type.Name,
                        Reason = Describe(ex),
                        Exception = ex,
                    };

                    // The thrower is undone too: a class with several targets keeps the ones it
                    // patched before the one that failed.
                    var undo = new List<Type>();
                    List<Type> before;
                    if (applied.TryGetValue(groupKey, out before)) undo.AddRange(before);
                    undo.Add(type);
                    failure.RollbackError = Unpatch(harmony, undo);
                    foreach (Type t in undo)
                    {
                        live.Remove(t);
                        failedTypes.Add(t);
                        if (t != type) failure.RolledBack.Add(t.Name);
                    }

                    failed[groupKey] = failure;
                    report.Failures.Add(failure);
                    continue;
                }

                List<Type> members;
                if (!applied.TryGetValue(groupKey, out members))
                {
                    members = new List<Type>();
                    applied[groupKey] = members;
                }
                members.Add(type);
                if (replacements != null && replacements.Count > 0) live.Add(type);
            }

            report.FailedClasses = failedTypes.Count;
            foreach (KeyValuePair<string, List<Type>> group in applied)
            {
                if (failed.ContainsKey(group.Key)) continue;
                foreach (Type t in group.Value)
                {
                    if (live.Contains(t)) report.Live++;
                    else report.Declined++;
                }
            }

            _live = live;
            return report;
        }

        /// <summary>The group's display name; <paramref name="key"/> keeps an explicit name and a class
        /// of the same name apart.</summary>
        private static string GroupOf(Type type, out string key)
        {
            var named = (PatchGroupAttribute)Attribute.GetCustomAttribute(type, typeof(PatchGroupAttribute), false);
            if (named != null && !string.IsNullOrEmpty(named.Name))
            {
                key = "group:" + named.Name;
                return named.Name;
            }

            Type outer = type;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            if (outer != type)
            {
                named = (PatchGroupAttribute)Attribute.GetCustomAttribute(outer, typeof(PatchGroupAttribute), false);
                if (named != null && !string.IsNullOrEmpty(named.Name))
                {
                    key = "group:" + named.Name;
                    return named.Name;
                }
            }
            key = "type:" + outer.FullName;
            return outer.Name;
        }

        /// <summary>Removes every patch this Harmony instance placed from a method declared on one of
        /// <paramref name="types"/>. Null on success, otherwise what could not be removed.</summary>
        private static string Unpatch(Harmony harmony, List<Type> types)
        {
            var owners = new HashSet<Type>(types);
            var work = new List<KeyValuePair<MethodBase, MethodInfo>>();
            List<string> errors = null;
            try
            {
                foreach (MethodBase original in harmony.GetPatchedMethods())
                {
                    Patches info = Harmony.GetPatchInfo(original);
                    if (info == null) continue;
                    Collect(info.Prefixes, harmony.Id, owners, original, work);
                    Collect(info.Postfixes, harmony.Id, owners, original, work);
                    Collect(info.Transpilers, harmony.Id, owners, original, work);
                    Collect(info.Finalizers, harmony.Id, owners, original, work);
                }
            }
            catch (Exception ex)
            {
                return "could not list patched methods: " + Describe(ex);
            }

            foreach (KeyValuePair<MethodBase, MethodInfo> item in work)
            {
                try
                {
                    harmony.Unpatch(item.Key, item.Value);
                }
                catch (Exception ex)
                {
                    if (errors == null) errors = new List<string>();
                    errors.Add(item.Value.DeclaringType.Name + "." + item.Value.Name + ": " + Describe(ex));
                }
            }
            return errors == null ? null : string.Join("; ", errors.ToArray());
        }

        private static void Collect(IEnumerable<HarmonyLib.Patch> patches, string owner, HashSet<Type> owners,
                                    MethodBase original, List<KeyValuePair<MethodBase, MethodInfo>> work)
        {
            if (patches == null) return;
            foreach (HarmonyLib.Patch patch in patches)
            {
                if (patch.owner != owner || patch.PatchMethod == null) continue;
                if (!owners.Contains(patch.PatchMethod.DeclaringType)) continue;
                work.Add(new KeyValuePair<MethodBase, MethodInfo>(original, patch.PatchMethod));
            }
        }

        /// <summary>The exception chain on one line, each link as <c>Type: first line of message</c>,
        /// without the reflection wrappers that only say "an invocation threw".</summary>
        internal static string Describe(Exception ex)
        {
            var parts = new List<string>();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is TargetInvocationException) continue;
                string message = e.Message ?? "";
                int newline = message.IndexOfAny(new[] { '\r', '\n' });
                if (newline >= 0) message = message.Substring(0, newline);
                string part = e.GetType().Name + ": " + message;
                if (!parts.Contains(part)) parts.Add(part);
            }
            return parts.Count == 0 ? "unknown" : string.Join(" -> ", parts.ToArray());
        }
    }
}

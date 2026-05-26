// Engine/ClashGroupingEngine.cs  — v4.0
//
// PROFESSIONAL IMPROVEMENT #4: Clash Grouping Engine
//
// Replicates Navisworks coordination grouping logic:
//   Instead of showing 17 raw clashes, shows 1 grouped issue with 17 members.
//
// Grouping strategies (applied in order):
//   1. Same discipline pair + same level + same route path   → "Same Route"
//   2. Same discipline pair + same zone/grid                 → "Same Zone"
//   3. Same primary offender element (root cause detection)  → "Same Source"
//   4. Same discipline pair + same level                     → "Same Level"
//
// IMPROVEMENT #19: Root Cause Detection
//   Identifies the PRIMARY OFFENDER — one large duct causing 34 clashes.
//   This is far more valuable than raw clash counts.

using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Engine
{
    public class ClashGroupingEngine
    {
        // ════════════════════════════════════════════════════════════════
        //  GROUP CLASHES  — main entry point
        // ════════════════════════════════════════════════════════════════

        public List<ClashGroup> GroupClashes(List<ClashResult> clashes)
        {
            if (clashes == null || clashes.Count == 0)
                return new List<ClashGroup>();

            var ungrouped  = new List<ClashResult>(clashes);
            var groups     = new List<ClashGroup>();

            // Strategy 1: Route-based grouping (same system route in same corridor)
            var routeGroups = GroupByRoute(ungrouped);
            foreach (var g in routeGroups)
            {
                if (g.Count >= 2)
                {
                    var group = BuildGroup(g, "Same Route Path");
                    groups.Add(group);
                    foreach (var c in g) ungrouped.Remove(c);
                }
            }

            // Strategy 2: Zone-based grouping (same grid + level corridor)
            var zoneGroups = GroupByZone(ungrouped);
            foreach (var g in zoneGroups)
            {
                if (g.Count >= 3)
                {
                    var group = BuildGroup(g, "Same Coordination Zone");
                    groups.Add(group);
                    foreach (var c in g) ungrouped.Remove(c);
                }
            }

            // Strategy 3: Root cause — single element causing multiple clashes
            var rootGroups = GroupByRootCause(ungrouped);
            foreach (var g in rootGroups)
            {
                if (g.Count >= 3)
                {
                    var group = BuildGroup(g, "Same Primary Offender");
                    group.PrimaryOffender = IdentifyPrimaryOffender(g);
                    groups.Add(group);
                    foreach (var c in g) ungrouped.Remove(c);
                }
            }

            // Strategy 4: Level-based grouping (same discipline pair + same level)
            var levelGroups = GroupByLevel(ungrouped);
            foreach (var g in levelGroups)
            {
                if (g.Count >= 2)
                {
                    var group = BuildGroup(g, "Same Level — Same Discipline Pair");
                    groups.Add(group);
                    foreach (var c in g) ungrouped.Remove(c);
                }
            }

            // Remainder: individual groups (one per ungrouped clash)
            foreach (var c in ungrouped)
            {
                var solo = BuildGroup(new List<ClashResult> { c }, "Individual");
                groups.Add(solo);
            }

            // Sort by severity, then by group size (largest first)
            return groups
                .OrderBy(g => (int)g.MaxSeverity)
                .ThenByDescending(g => g.Count)
                .ToList();
        }

        // ════════════════════════════════════════════════════════════════
        //  STRATEGY 1: ROUTE-BASED GROUPING
        //  Groups clashes where ElementA shares the same system type
        //  and their clash points are within a corridor (3m linear distance)
        // ════════════════════════════════════════════════════════════════

        private List<List<ClashResult>> GroupByRoute(List<ClashResult> clashes)
        {
            var groups = new List<List<ClashResult>>();
            var visited = new HashSet<string>();

            foreach (var clash in clashes)
            {
                if (visited.Contains(clash.ClashId)) continue;

                // Find clashes with same discipline pair + system type + nearby points
                var group = clashes
                    .Where(c => !visited.Contains(c.ClashId)
                        && c.DisciplineA == clash.DisciplineA
                        && c.DisciplineB == clash.DisciplineB
                        && c.LevelName == clash.LevelName
                        && !string.IsNullOrEmpty(c.SystemTypeA)
                        && c.SystemTypeA == clash.SystemTypeA
                        && PointDistance(c.ClashPoint, clash.ClashPoint) < 9.84) // 3m
                    .ToList();

                if (group.Count > 0)
                {
                    groups.Add(group);
                    foreach (var c in group) visited.Add(c.ClashId);
                }
            }
            return groups;
        }

        // ════════════════════════════════════════════════════════════════
        //  STRATEGY 2: ZONE-BASED GROUPING
        //  Groups clashes within the same structural bay (same grid ref)
        // ════════════════════════════════════════════════════════════════

        private List<List<ClashResult>> GroupByZone(List<ClashResult> clashes)
        {
            return clashes
                .Where(c => !string.IsNullOrEmpty(c.GridRef) && c.GridRef != "N/A")
                .GroupBy(c => $"{c.GridRef}|{c.LevelName}|{c.DisciplineA}|{c.DisciplineB}")
                .Select(g => g.ToList())
                .Where(g => g.Count >= 2)
                .ToList();
        }

        // ════════════════════════════════════════════════════════════════
        //  STRATEGY 3: ROOT CAUSE GROUPING
        //  One element (e.g., a large duct) causing clashes with many others.
        //  Groups by the element ID that appears most frequently.
        // ════════════════════════════════════════════════════════════════

        private List<List<ClashResult>> GroupByRootCause(List<ClashResult> clashes)
        {
            var groups = new List<List<ClashResult>>();
            var visited = new HashSet<string>();

            // Count element appearances (both A and B sides)
            var elementFreq = new Dictionary<long, int>();
            foreach (var c in clashes)
            {
                long idA = c.ElementA?.Id.Value ?? 0;
                long idB = c.ElementB?.Id.Value ?? 0;
                if (idA != 0) elementFreq[idA] = (elementFreq.TryGetValue(idA, out int va) ? va : 0) + 1;
                if (idB != 0) elementFreq[idB] = (elementFreq.TryGetValue(idB, out int vb) ? vb : 0) + 1;
            }

            // Find high-frequency offenders (appears in 3+ clashes)
            var offenders = elementFreq
                .Where(kv => kv.Value >= 3)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            foreach (long offenderId in offenders)
            {
                var group = clashes
                    .Where(c => !visited.Contains(c.ClashId)
                        && (c.ElementA?.Id.Value == offenderId || c.ElementB?.Id.Value == offenderId))
                    .ToList();

                if (group.Count >= 3)
                {
                    groups.Add(group);
                    foreach (var c in group) visited.Add(c.ClashId);
                }
            }

            return groups;
        }

        // ════════════════════════════════════════════════════════════════
        //  STRATEGY 4: LEVEL-BASED GROUPING
        //  Same discipline pair + same level — catch remaining clusters
        // ════════════════════════════════════════════════════════════════

        private List<List<ClashResult>> GroupByLevel(List<ClashResult> clashes)
        {
            return clashes
                .GroupBy(c => $"{c.DisciplineA}|{c.DisciplineB}|{c.LevelName}|{c.Severity}")
                .Select(g => g.ToList())
                .Where(g => g.Count >= 2)
                .ToList();
        }

        // ════════════════════════════════════════════════════════════════
        //  BUILD GROUP  — create a ClashGroup from a list of clashes
        // ════════════════════════════════════════════════════════════════

        private static ClashGroup BuildGroup(List<ClashResult> clashes, string reason)
        {
            var first = clashes[0];
            var worstSev = (ClashSeverity)clashes.Min(c => (int)c.Severity);

            string title = BuildGroupTitle(clashes, reason);

            var group = new ClashGroup
            {
                GroupTitle      = title,
                MaxSeverity     = worstSev,
                GroupingReason  = reason,
                LevelName       = first.LevelName,
                ZoneName        = first.ZoneName,
                GridRef         = first.GridRef,
                DisciplineA     = first.DisciplineA,
                DisciplineB     = first.DisciplineB,
                Clashes         = clashes
            };

            // Assign group ID to all members
            foreach (var c in clashes)
                c.GroupId = group.GroupId;

            return group;
        }

        private static string BuildGroupTitle(List<ClashResult> clashes, string reason)
        {
            var first = clashes[0];
            string discA = first.DisciplineA.ToString();
            string discB = first.DisciplineB.ToString();
            string level = !string.IsNullOrEmpty(first.LevelName) ? $" @ {first.LevelName}" : "";
            string grid  = !string.IsNullOrEmpty(first.GridRef) && first.GridRef != "N/A"
                ? $" [{first.GridRef}]" : "";

            return $"{discA} vs {discB}{level}{grid} — {clashes.Count} clashes ({reason})";
        }

        // ════════════════════════════════════════════════════════════════
        //  ROOT CAUSE IDENTIFICATION
        //  Returns element description of the primary offender
        // ════════════════════════════════════════════════════════════════

        public static string IdentifyPrimaryOffender(List<ClashResult> clashes)
        {
            var freq = new Dictionary<long, int>();
            var names = new Dictionary<long, string>();

            foreach (var c in clashes)
            {
                long idA = c.ElementA?.Id.Value ?? 0;
                long idB = c.ElementB?.Id.Value ?? 0;

                if (idA != 0)
                {
                    freq[idA] = (freq.TryGetValue(idA, out int va) ? va : 0) + 1;
                    if (!names.ContainsKey(idA))
                        names[idA] = $"{c.ElementA?.Category?.Name ?? c.DisciplineA.ToString()} ID:{idA}";
                }
                if (idB != 0)
                {
                    freq[idB] = (freq.TryGetValue(idB, out int vb) ? vb : 0) + 1;
                    if (!names.ContainsKey(idB))
                        names[idB] = $"{c.ElementB?.Category?.Name ?? c.DisciplineB.ToString()} ID:{idB}";
                }
            }

            if (!freq.Any()) return "";
            long topId = freq.OrderByDescending(kv => kv.Value).First().Key;
            return names.TryGetValue(topId, out string name) ? name : topId.ToString();
        }

        // ════════════════════════════════════════════════════════════════
        //  COORDINATION INTELLIGENCE
        //  Generates a human-readable coordination report for BIM managers
        // ════════════════════════════════════════════════════════════════

        public static string GenerateCoordinationReport(List<ClashGroup> groups)
        {
            if (!groups.Any()) return "No clashes detected.";

            int totalClashes = groups.Sum(g => g.Count);
            int groupCount   = groups.Count;
            int critical     = groups.Count(g => g.MaxSeverity == ClashSeverity.Critical);

            var report = new System.Text.StringBuilder();
            report.AppendLine($"═══ COORDINATION INTELLIGENCE REPORT ═══");
            report.AppendLine($"Total Issues: {groupCount} groups / {totalClashes} individual clashes");
            report.AppendLine($"Critical Groups: {critical}");
            report.AppendLine();

            // Top offenders (elements causing most clashes)
            var allClashes = groups.SelectMany(g => g.Clashes).ToList();
            var rootOffenders = GetTopOffenders(allClashes, top: 5);

            if (rootOffenders.Any())
            {
                report.AppendLine("── TOP ROOT CAUSE ELEMENTS ──");
                foreach (var (name, count) in rootOffenders)
                    report.AppendLine($"  • {name}: {count} clashes");
                report.AppendLine();
            }

            // Worst groups
            report.AppendLine("── CRITICAL GROUPS ──");
            foreach (var g in groups.Take(10))
            {
                string sev = g.MaxSeverity == ClashSeverity.Critical ? "🔴" :
                             g.MaxSeverity == ClashSeverity.Hard     ? "🟠" : "🟡";
                report.AppendLine($"  {sev} {g.GroupTitle}");
                if (!string.IsNullOrEmpty(g.PrimaryOffender))
                    report.AppendLine($"     Root cause: {g.PrimaryOffender}");
            }

            return report.ToString();
        }

        private static List<(string name, int count)> GetTopOffenders(
            List<ClashResult> clashes, int top = 5)
        {
            var freq  = new Dictionary<long, int>();
            var names = new Dictionary<long, string>();

            foreach (var c in clashes)
            {
                void Register(long id, string name)
                {
                    freq[id] = (freq.TryGetValue(id, out int v) ? v : 0) + 1;
                    if (!names.ContainsKey(id)) names[id] = name;
                }

                if (c.ElementA != null) Register(c.ElementA.Id.Value,
                    $"{c.ElementA.Category?.Name} (ID:{c.ElementA.Id.Value})");
                if (c.ElementB != null) Register(c.ElementB.Id.Value,
                    $"{c.ElementB.Category?.Name} (ID:{c.ElementB.Id.Value})");
            }

            return freq.OrderByDescending(kv => kv.Value).Take(top)
                .Select(kv => (names.TryGetValue(kv.Key, out string n) ? n : kv.Key.ToString(), kv.Value))
                .ToList();
        }

        private static double PointDistance(Autodesk.Revit.DB.XYZ a, Autodesk.Revit.DB.XYZ b)
            => Math.Sqrt(
                Math.Pow(a.X - b.X, 2) +
                Math.Pow(a.Y - b.Y, 2) +
                Math.Pow(a.Z - b.Z, 2));
    }
}

// AutoResolve/AutoResolveEngine.cs
using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;

namespace ClashResolveAI.AutoResolve
{
    public static class AutoResolveEngine
    {
        private const double MinInsulation  = 50.0;
        private const double MinMaintenance = 100.0;

        public static List<RoutingAlternative> Propose(ClashResult clash, Document doc)
        {
            var alts = new List<RoutingAlternative>();
            bool aYields = (int)clash.DisciplineA > (int)clash.DisciplineB;
            var  moving  = aYields ? clash.ElementA : clash.ElementB;
            var  staying = aYields ? clash.ElementB : clash.ElementA;
            var  movDisc = aYields ? clash.DisciplineA : clash.DisciplineB;

            if (movDisc == Discipline.GravityDrainage)
            {
                alts.Add(new RoutingAlternative { Description=$"⚠ Gravity drainage cannot change slope. Reroute {staying.Category?.Name} instead.", Direction="Reroute staying element", OffsetMM=0, Confidence="High" });
                return alts;
            }
            if (movDisc == Discipline.Structural)
            {
                alts.Add(new RoutingAlternative { Description=$"⛔ Structural cannot move. Reroute MEP element.", Direction="Reroute MEP", OffsetMM=0, Confidence="High" });
                return alts;
            }

            double overlap  = Math.Abs(Math.Min(clash.GapMM, 0));
            double required = overlap + MinInsulation + MinMaintenance;
            string movName  = moving.Category?.Name ?? movDisc.ToString();
            string staName  = staying.Category?.Name ?? "";

            alts.Add(new RoutingAlternative { Description=$"Raise {movName} by {required:F0}mm above {staName}. Verify soffit clearance.", Direction="Raise", OffsetMM=required, Confidence="High" });
            alts.Add(new RoutingAlternative { Description=$"Lower {movName} by {required:F0}mm below {staName}. Verify floor zone depth.", Direction="Lower", OffsetMM=required, Confidence="Medium" });

            double bb = GetWidth(staying) * 304.8 / 2 + required;
            alts.Add(new RoutingAlternative { Description=$"Reroute {movName} laterally by {bb:F0}mm. Add 2×45° elbows.", Direction="Lateral", OffsetMM=bb, Confidence="Medium" });

            if (clash.ClashType.Contains("Wall") || clash.ClashType.Contains("Floor"))
                alts.Add(new RoutingAlternative { Description=$"Install fire-rated sleeve per {Code(movDisc)}. Seal with intumescent.", Direction="Sleeve", OffsetMM=0, Confidence="High" });

            return alts;
        }

        public static string ToRfiText(ClashResult c)
        {
            bool aY = (int)c.DisciplineA > (int)c.DisciplineB;
            var  mEl= aY ? c.ElementA : c.ElementB;
            var  sEl= aY ? c.ElementB : c.ElementA;
            var  mD = aY ? c.DisciplineA : c.DisciplineB;
            var  sD = aY ? c.DisciplineB : c.DisciplineA;

            string alts = "";
            foreach (var a in c.Alternatives)
                alts += $"\n  • [{a.Confidence}] {a.Description}";

            string linkInfo = "";
            if (!string.IsNullOrEmpty(c.LinkFileA)) linkInfo += $"\nSource A: [{c.LinkFileA}]";
            if (!string.IsNullOrEmpty(c.LinkFileB)) linkInfo += $"\nSource B: [{c.LinkFileB}]";

            return
                $"RFI – CR-{c.ClashId}\n" +
                $"Issued    : {c.DetectedAt:dd/MM/yyyy HH:mm}\n" +
                $"Severity  : {c.Severity}  |  Priority: {c.Priority}\n" +
                $"Location  : {c.LocationText}\n" +
                $"Level     : {c.LevelName}  |  Grid: {c.GridRef}\n" +
                $"Rule      : {c.RuleApplied}{linkInfo}\n\n" +
                $"DESCRIPTION:\n" +
                $"A {c.Severity.ToString().ToLower()} clash between " +
                $"[{mEl.Category?.Name}] (ID {mEl.Id.Value}, {mD}) and " +
                $"[{sEl.Category?.Name}] (ID {sEl.Id.Value}, {sD}). " +
                $"Gap: {c.GapMM:F1}mm.\n\n" +
                $"DISCIPLINE PRIORITY:\n{sD}(priority {(int)sD}) > {mD}(priority {(int)mD}). " +
                $"{mEl.Category?.Name} must move.\n\n" +
                $"ROUTING OPTIONS:{(string.IsNullOrEmpty(alts) ? " See AI suggestion." : alts)}\n\n" +
                $"AI RESOLUTION:\n{c.AiSuggestion}\n\n" +
                $"Please confirm routing and update BIM model.\nStatus: {c.Status}";
        }

        private static double GetWidth(Element el)
        {
            var bb = el.get_BoundingBox(null);
            return bb != null ? Math.Min(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y) : 0.3;
        }

        private static string Code(Discipline d) => d switch
        {
            Discipline.Plumbing   => "IPC 717 / BS EN 1366-3",
            Discipline.HVAC       => "IMC 607 / BS EN 1366-1",
            Discipline.Electrical => "NEC 300.21 / BS EN 1366-5",
            _                     => "local fire code"
        };
    }
}

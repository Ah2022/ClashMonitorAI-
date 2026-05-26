// Reports/WordReportGenerator.cs  — v4.1
//
// FIX v4.1 — Word report crash:
//   "Unable to determine the identity of domain."
//
//   Root cause 1: Long/special-char project names (e.g. BD142-ARTAR-SD-B01-M3D-PF-350-R...)
//     cause DocumentFormat.OpenXml to fail when constructing its internal temp COM stream.
//     Fix: write to Path.GetTempPath() first, then File.Move() to final destination.
//
//   Root cause 2: The output path itself may be on a network share or a "downloaded"
//     location that .NET Framework 4.8 marks as a different security zone.
//     Fix: temp-then-move bypasses the zone check entirely.
//
//   Additional: project name is sanitised (truncated to 50 chars, special chars removed)
//   before use in the filename.

using ClashResolveAI.Core;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Reports
{
    public static class WordReportGenerator
    {
        private const string CNav    = "0D1B2E";
        private const string CAccent = "1A5276";
        private const string CCrit   = "922B21";
        private const string CHard   = "C0392B";
        private const string CSoft   = "E67E22";
        private const string CAlt    = "F5F8FA";
        private const string CHdr    = "0D1B2E";

        public static string Generate(
            List<ClashResult> clashes,
            string project,
            string folder)
        {
            // ── Sanitise project name ─────────────────────────────────────
            string safeProject = SanitiseName(project);
            string fileName    = $"ClashReport_{safeProject}_{DateTime.Now:yyyyMMdd_HHmm}.docx";

            // ── FIX: write to temp first, then move ───────────────────────
            // Avoids "Unable to determine the identity of domain" when the
            // destination is on a network path or security-zone-flagged location.
            string tempPath = Path.Combine(Path.GetTempPath(), fileName);
            string destPath = Path.Combine(folder, fileName);

            try
            {
                BuildDocument(clashes, project, tempPath);

                // Move from temp to requested folder
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);

                return destPath;
            }
            catch
            {
                // If move fails, return temp path so caller can still access it
                if (File.Exists(tempPath)) return tempPath;
                throw;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  DOCUMENT BUILDER
        // ════════════════════════════════════════════════════════════════

        private static void BuildDocument(
            List<ClashResult> clashes, string project, string path)
        {
            using var doc  = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var main       = doc.AddMainDocumentPart();
            main.Document  = new Document(new Body());
            AddStyles(main);
            var body = main.Document.Body!;

            // Cover
            body.AppendChild(P("MEP CLASH COORDINATION REPORT", 28, CNav, true, true));
            body.AppendChild(P(project.ToUpper(),               16, CAccent, true, true));
            body.AppendChild(P($"Date: {DateTime.Now:dd MMMM yyyy}  |  ClashResolve AI v4.1",
                                10, "888888", false, true));
            body.AppendChild(P(""));
            body.AppendChild(CoverTable(clashes));
            body.AppendChild(Brk());

            // Linked model summary
            var crossLink = clashes.Where(c =>
                !string.IsNullOrEmpty(c.LinkFileA) || !string.IsNullOrEmpty(c.LinkFileB)).ToList();

            if (crossLink.Any())
            {
                body.AppendChild(H1("Linked Model Clashes"));
                body.AppendChild(P(
                    $"{crossLink.Count} clashes detected across linked models. " +
                    "These are the most critical coordination issues as they span multiple discipline files.",
                    11));
                var links = crossLink.GroupBy(c =>
                    string.IsNullOrEmpty(c.LinkFileA) ? c.LinkFileB : c.LinkFileA);
                foreach (var g in links)
                    body.AppendChild(P(
                        $"  - {g.Key}: {g.Count()} clashes " +
                        $"({g.Count(c => c.Severity == ClashSeverity.Critical)} critical, " +
                        $"{g.Count(c => c.Severity == ClashSeverity.Hard)} hard)",
                        10, CAccent));
                body.AppendChild(Brk());
            }

            // Executive Summary
            body.AppendChild(H1("Executive Summary"));
            body.AppendChild(P(
                $"ClashResolve AI v4.1 completed a full MEP coordination scan of \"{project}\". " +
                $"Total: {clashes.Count} clashes — " +
                $"{clashes.Count(c => c.Severity == ClashSeverity.Critical)} Critical, " +
                $"{clashes.Count(c => c.Severity == ClashSeverity.Hard)} Hard, " +
                $"{clashes.Count(c => c.Severity == ClashSeverity.Soft)} Soft, " +
                $"{clashes.Count(c => c.Severity == ClashSeverity.Clearance)} Clearance. " +
                "Priority: Structural > Gravity Drainage > Medical Gas > HVAC > Fire Protection > " +
                "Plumbing > Electrical > Cable Tray > Conduit.", 11));
            body.AppendChild(Brk());

            // Clash Table
            body.AppendChild(H1("Clash Detail Report"));
            ClashTable(body, clashes);
            body.AppendChild(Brk());

            // RFI Register
            body.AppendChild(H1("RFI Register"));
            foreach (var c in clashes)
            {
                string col = c.Severity == ClashSeverity.Critical ? CCrit :
                             c.Severity == ClashSeverity.Hard     ? CHard :
                             c.Severity == ClashSeverity.Soft     ? CSoft : "2980B9";

                string linkInfo = string.IsNullOrEmpty(c.LinkFileA)
                    ? "" : $"  [Links: {c.LinkFileA}/{c.LinkFileB}]";

                body.AppendChild(SP(
                    $"RFI-{c.ClashId}  |  {c.Priority}  |  {c.Severity}  |  " +
                    $"{c.LevelName}  |  Grid:{c.GridRef}{linkInfo}",
                    col, 11, true));
                body.AppendChild(P(c.AiSuggestion ?? c.RfiText ?? "", 10));
                body.AppendChild(P(
                    $"Status: {c.Status}   Detected: {c.DetectedAt:dd/MM/yyyy HH:mm}",
                    9, "888888"));
                body.AppendChild(P("---", 8, "CCCCCC"));
                body.AppendChild(P(""));
            }

            body.AppendChild(P(
                $"ClashResolve AI v4.1  |  {DateTime.Now:dd MMM yyyy}",
                8, CAccent, false, true));

            main.Document.Save();
        }

        // ════════════════════════════════════════════════════════════════
        //  TABLE BUILDERS
        // ════════════════════════════════════════════════════════════════

        private static Table CoverTable(List<ClashResult> cl)
        {
            var t = new Table(new TableProperties(
                new TableWidth  { Type = TableWidthUnitValues.Pct, Width = "100" },
                new TableJustification { Val = TableRowAlignmentValues.Center },
                Borders()));
            var row  = new TableRow();
            var items = new[]
            {
                ("TOTAL",     cl.Count.ToString(),                                             CNav),
                ("CRITICAL",  cl.Count(c => c.Severity == ClashSeverity.Critical).ToString(), CCrit),
                ("HARD",      cl.Count(c => c.Severity == ClashSeverity.Hard).ToString(),     CHard),
                ("SOFT",      cl.Count(c => c.Severity == ClashSeverity.Soft).ToString(),     CSoft),
                ("CLEARANCE", cl.Count(c => c.Severity == ClashSeverity.Clearance).ToString(),"2980B9"),
                ("CROSS-LNK", cl.Count(c =>
                    !string.IsNullOrEmpty(c.LinkFileA) ||
                    !string.IsNullOrEmpty(c.LinkFileB)).ToString(), "16A085"),
            };
            foreach (var (l, v, c) in items)
                row.AppendChild(new TableCell(
                    new TableCellProperties(
                        new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "16" },
                        new Shading { Val = ShadingPatternValues.Clear, Fill = "EBF5FB" }),
                    P(v, 18, c, true, true),
                    P(l, 8,  CNav, true, true)));
            t.AppendChild(row);
            return t;
        }

        private static void ClashTable(Body body, List<ClashResult> cl)
        {
            var t = new Table(new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Pct, Width = "100" },
                Borders()));

            // Header row
            string[] hdrs  = { "ID",  "Pri", "Sev", "Gap mm", "Element A",     "Element B",     "Level",  "Grid", "Link",   "AI Resolution" };
            int[]    wids  = {  600,   700,   900,    600,      1600,             1600,             1100,    600,    900,       2000           };
            var      hr    = new TableRow();
            for (int i = 0; i < hdrs.Length; i++)
                hr.AppendChild(TC(hdrs[i], wids[i], CHdr, "FFFFFF", 9, true));
            t.AppendChild(hr);

            bool alt = false;
            foreach (var c in cl)
            {
                string fill = alt ? CAlt : "FFFFFF";
                string sCol = c.Severity == ClashSeverity.Critical ? CCrit :
                              c.Severity == ClashSeverity.Hard     ? CHard :
                              c.Severity == ClashSeverity.Soft     ? CSoft : "2980B9";
                string pCol = c.Priority == "Critical" ? CCrit :
                              c.Priority == "High"     ? CHard :
                              c.Priority == "Medium"   ? CSoft : "27AE60";
                string lnk  = "";
                if (!string.IsNullOrEmpty(c.LinkFileA)) lnk = c.LinkFileA;
                if (!string.IsNullOrEmpty(c.LinkFileB) && c.LinkFileB != c.LinkFileA)
                    lnk += "/" + c.LinkFileB;

                var r = new TableRow();
                r.AppendChild(TC(c.ClashId,                                                wids[0], fill,  CNav,  9));
                r.AppendChild(TC(c.Priority,                                               wids[1], fill,  pCol,  9, true));
                r.AppendChild(TC(c.Severity.ToString(),                                    wids[2], fill,  sCol,  9, true));
                r.AppendChild(TC($"{c.GapMM:F1}",                                         wids[3], fill,  CNav,  9));
                r.AppendChild(TC($"{c.ElementA?.Category?.Name ?? ""}\nID {c.ElementA?.Id.Value}", wids[4], fill, CNav, 9));
                r.AppendChild(TC($"{c.ElementB?.Category?.Name ?? ""}\nID {c.ElementB?.Id.Value}", wids[5], fill, CNav, 9));
                r.AppendChild(TC(c.LevelName,                                              wids[6], fill,  CNav,  9));
                r.AppendChild(TC(c.GridRef,                                                wids[7], fill,  CNav,  9));
                r.AppendChild(TC(lnk, wids[8], string.IsNullOrEmpty(lnk) ? fill : "D5F5E3", "16A085", 8));
                r.AppendChild(TC(c.AiSuggestion ?? "",                                    wids[9], fill, "333333", 9));
                t.AppendChild(r);
                alt = !alt;
            }
            body.AppendChild(t);
        }

        // ════════════════════════════════════════════════════════════════
        //  ELEMENT BUILDERS
        // ════════════════════════════════════════════════════════════════

        private static Paragraph P(string t, int sz = 11, string c = "000000",
            bool b = false, bool ctr = false)
        {
            var run = new Run(new Text(t) { Space = SpaceProcessingModeValues.Preserve });
            run.RunProperties = new RunProperties
            {
                FontSize = new FontSize { Val = (sz * 2).ToString() },
                Color    = new Color    { Val = c },
                Bold     = b ? new Bold() : null!,
                RunFonts = new RunFonts { Ascii = "Calibri" }
            };
            var p = new Paragraph(run);
            if (ctr) p.ParagraphProperties = new ParagraphProperties(
                new Justification { Val = JustificationValues.Center });
            return p;
        }

        private static Paragraph H1(string t)
        {
            var r = new Run(new Text(t));
            r.RunProperties = new RunProperties
            {
                Bold     = new Bold(),
                FontSize = new FontSize { Val = "28" },
                Color    = new Color   { Val = CNav },
                RunFonts = new RunFonts { Ascii = "Calibri" }
            };
            return new Paragraph(r)
            {
                ParagraphProperties = new ParagraphProperties(
                    new SpacingBetweenLines { Before = "240", After = "120" })
            };
        }

        private static Paragraph SP(string t, string c, int sz, bool b)
        {
            var r = new Run(new Text(t));
            r.RunProperties = new RunProperties
            {
                Bold     = b ? new Bold() : null!,
                FontSize = new FontSize { Val = (sz * 2).ToString() },
                Color    = new Color   { Val = c },
                RunFonts = new RunFonts { Ascii = "Calibri" }
            };
            return new Paragraph(r);
        }

        private static Paragraph Brk()
        {
            var p = new Paragraph();
            p.AppendChild(new Run(new Break { Type = BreakValues.Page }));
            return p;
        }

        private static TableCell TC(string t, int w, string bg, string fg,
            int sz = 10, bool b = false) =>
            new TableCell(
                new TableCellProperties(
                    new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = w.ToString() },
                    new Shading { Val = ShadingPatternValues.Clear, Fill = bg },
                    new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }),
                P(t, sz, fg, b));

        private static TableBorders Borders() => new TableBorders(
            new TopBorder           { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
            new BottomBorder        { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
            new LeftBorder          { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
            new RightBorder         { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 2, Color = "DDDDDD" },
            new InsideVerticalBorder   { Val = BorderValues.Single, Size = 2, Color = "DDDDDD" });

        private static void AddStyles(MainDocumentPart m)
        {
            var sp = m.AddNewPart<StyleDefinitionsPart>();
            sp.Styles = new Styles(new DocDefaults(new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri" },
                    new FontSize { Val   = "22"       }))));
            sp.Styles.Save();
        }

        // ════════════════════════════════════════════════════════════════
        //  NAME SANITISER
        // ════════════════════════════════════════════════════════════════

        private static string SanitiseName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "Project";
            var invalid = Path.GetInvalidFileNameChars();
            var sb      = new System.Text.StringBuilder();
            foreach (char c in input)
                sb.Append(invalid.Contains(c) ? '_' : c);
            string result = sb.ToString().Trim();
            // Truncate to 50 chars to keep full path under MAX_PATH
            return result.Length > 50 ? result.Substring(0, 50) : result;
        }
    }
}

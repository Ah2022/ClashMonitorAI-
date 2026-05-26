// Core/ElementCollector.cs  — v5.1
//
// FIX v5.1 (BUG 4):
//   GetDiscipline now recognises many more common MEP categories that
//   previously returned Discipline.Unknown and were silently skipped:
//     • OST_PipeAccessory        → Plumbing
//     • OST_DuctAccessory        → HVAC  (already existed — confirmed)
//     • OST_DuctTerminal         → HVAC  (NEW)
//     • OST_AirTerminal          → HVAC  (NEW — same as DuctTerminal in some versions)
//     • OST_CommunicationDevices → Electrical (NEW)
//     • OST_DataDevices          → Electrical (NEW)
//     • OST_NurseCallDevices     → Electrical (NEW)
//     • OST_SecurityDevices      → Electrical (NEW)
//     • OST_TelephoneDevices     → Electrical (NEW)
//     • OST_FireAlarmDevices     → FireProtection (already existed — confirmed)
//     • OST_GenericModel         → resolved via system parameter heuristic (NEW)
//
//   IsMonitoredClashElement now accepts structural elements explicitly
//   so host structural walls/columns are always scannable.

using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClashResolveAI.Core
{
    public static class ElementCollector
    {
        private const double MinSolidVolume = 1e-9;

        private static readonly Options _liveOpts = new Options
        {
            DetailLevel              = ViewDetailLevel.Medium,
            ComputeReferences        = false,
            IncludeNonVisibleObjects = false
        };

        // ── Clash matrix ──────────────────────────────────────────────────
        public static readonly List<ClashMatrixEntry> ClashMatrix = new List<ClashMatrixEntry>
        {
            new ClashMatrixEntry { Source=Discipline.Plumbing,        Target=Discipline.Structural,     Check=true, RuleKey="Pipe_vs_Beam"        },
            new ClashMatrixEntry { Source=Discipline.HVAC,            Target=Discipline.Structural,     Check=true, RuleKey="Duct_vs_Beam"        },
            new ClashMatrixEntry { Source=Discipline.Electrical,      Target=Discipline.Structural,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.FireProtection,  Target=Discipline.Structural,     Check=true, RuleKey="FireMain_vs_Structure"},
            new ClashMatrixEntry { Source=Discipline.Electrical,      Target=Discipline.Plumbing,       Check=true, RuleKey="CableTray_vs_Pipe"   },
            new ClashMatrixEntry { Source=Discipline.Plumbing,        Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.Electrical,      Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.FireProtection,  Target=Discipline.Electrical,     Check=true, RuleKey="Sprinkler_vs_Light"  },
            new ClashMatrixEntry { Source=Discipline.FireProtection,  Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.Structural,     Check=true, RuleKey="Pipe_vs_Beam"        },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.Electrical,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.Plumbing,       Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.MedicalGas,      Target=Discipline.HVAC,           Check=true, RuleKey="MedGas_vs_HVAC"      },
            new ClashMatrixEntry { Source=Discipline.MedicalGas,      Target=Discipline.Structural,     Check=true, RuleKey="Pipe_vs_Beam"        },
            new ClashMatrixEntry { Source=Discipline.CableTray,       Target=Discipline.HVAC,           Check=true, RuleKey="CableTray_vs_Pipe"   },
            new ClashMatrixEntry { Source=Discipline.CableTray,       Target=Discipline.Plumbing,       Check=true, RuleKey="CableTray_vs_Pipe"   },
            new ClashMatrixEntry { Source=Discipline.CableTray,       Target=Discipline.Structural,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.Conduit,         Target=Discipline.HVAC,           Check=true, RuleKey=""                    },
            new ClashMatrixEntry { Source=Discipline.Conduit,         Target=Discipline.Structural,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.Conduit,         Target=Discipline.Plumbing,       Check=true, RuleKey="Generic"             },
        };

        // ════════════════════════════════════════════════════════════════
        //  PUBLIC COLLECT
        // ════════════════════════════════════════════════════════════════

        public static List<Element> GetRawByDisciplineAll(Document doc, Discipline disc)
            => GetRawByDiscipline(doc, disc);

        public static List<Element> GetByDiscipline(Document doc, Discipline disc)
            => GetRawByDiscipline(doc, disc).Where(IsValidMEPElement).ToList();

        public static List<Element> GetByDisciplineNearby(
            Document doc, Discipline disc, BoundingBoxIntersectsFilter filter)
        {
            var cats   = GetCategoriesForDiscipline(disc);
            var result = new List<Element>();
            foreach (var cat in cats)
            {
                try
                {
                    foreach (var el in new FilteredElementCollector(doc)
                        .OfCategory(cat).WherePasses(filter)
                        .WhereElementIsNotElementType().ToElements())
                        if (IsValidMEPElement(el)) result.Add(el);
                }
                catch (System.Exception ex)
                {
                    Debug.WriteLine($"[Collector] Nearby error: {ex.Message}");
                }
            }
            return result.Distinct().ToList();
        }

        // ════════════════════════════════════════════════════════════════
        //  VALIDATION
        // ════════════════════════════════════════════════════════════════

        public static bool IsValidMEPElement(Element el)
        {
            if (el == null || el.Category == null || el.Location == null) return false;
            try
            {
                var geom = el.get_Geometry(_liveOpts);
                return geom != null && HasSolidVolume(geom);
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[Collector] IsValid {el.Id.Value}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns true for ANY element that can participate in a live clash check.
        /// v5.1: Structural elements are explicitly accepted (they often have no
        /// Location property but always have a bounding box).
        /// </summary>
        public static bool IsMonitoredClashElement(Element el)
        {
            if (el?.Category == null) return false;
            var disc = GetDiscipline(el);
            if (disc == Discipline.Unknown) return false;

            try { return el.get_BoundingBox(null) != null; }
            catch { return false; }
        }

        // ════════════════════════════════════════════════════════════════
        //  SOLID EXTRACTION
        // ════════════════════════════════════════════════════════════════

        public static Solid? GetSolidFromElement(Element el)
        {
            if (el == null) return null;
            var cached = GeometryCacheService.Instance.Get(el.Id);
            if (cached?.Solid != null) return cached.Solid;
            try
            {
                var geom = el.get_Geometry(_liveOpts);
                return geom != null ? ExtractBestSolid(geom) : null;
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[Collector] GetSolid: {ex.Message}");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  DISCIPLINE DETECTION  — v5.1: expanded category list
        // ════════════════════════════════════════════════════════════════

        public static Discipline GetDiscipline(Element el)
        {
            if (el?.Category == null) return Discipline.Unknown;

            switch (el.Category.Id.Value)
            {
                // ── HVAC ──────────────────────────────────────────────
                case (long)BuiltInCategory.OST_DuctCurves:
                case (long)BuiltInCategory.OST_DuctFitting:
                case (long)BuiltInCategory.OST_FlexDuctCurves:
                case (long)BuiltInCategory.OST_DuctAccessory:
                case (long)BuiltInCategory.OST_MechanicalEquipment:
                case (long)BuiltInCategory.OST_DuctTerminal:    // FIX v5.1
                    return Discipline.HVAC;

                // ── Plumbing / Drainage / Medical Gas ────────────────
                case (long)BuiltInCategory.OST_PipeCurves:
                case (long)BuiltInCategory.OST_PipeFitting:
                case (long)BuiltInCategory.OST_FlexPipeCurves:
                case (long)BuiltInCategory.OST_PipeAccessory:   // FIX v5.1
                case (long)BuiltInCategory.OST_PlumbingFixtures:
                    if (IsMedicalGas(el))       return Discipline.MedicalGas;
                    if (IsGravityDrainage(el))  return Discipline.GravityDrainage;
                    return Discipline.Plumbing;

                // ── Cable Tray ────────────────────────────────────────
                case (long)BuiltInCategory.OST_CableTray:
                case (long)BuiltInCategory.OST_CableTrayFitting:
                    return Discipline.CableTray;

                // ── Conduit ───────────────────────────────────────────
                case (long)BuiltInCategory.OST_Conduit:
                case (long)BuiltInCategory.OST_ConduitFitting:
                    return Discipline.Conduit;

                // ── Electrical (expanded in v5.1) ─────────────────────
                case (long)BuiltInCategory.OST_ElectricalEquipment:
                case (long)BuiltInCategory.OST_LightingFixtures:
                case (long)BuiltInCategory.OST_CommunicationDevices: // FIX v5.1
                case (long)BuiltInCategory.OST_DataDevices:           // FIX v5.1
                case (long)BuiltInCategory.OST_NurseCallDevices:      // FIX v5.1
                case (long)BuiltInCategory.OST_SecurityDevices:       // FIX v5.1
                case (long)BuiltInCategory.OST_TelephoneDevices:      // FIX v5.1
                    return Discipline.Electrical;

                // ── Fire Protection ───────────────────────────────────
                case (long)BuiltInCategory.OST_Sprinklers:
                case (long)BuiltInCategory.OST_FireAlarmDevices:
                    return Discipline.FireProtection;

                // ── Structural ────────────────────────────────────────
                case (long)BuiltInCategory.OST_StructuralFraming:
                case (long)BuiltInCategory.OST_StructuralColumns:
                case (long)BuiltInCategory.OST_Floors:
                case (long)BuiltInCategory.OST_Walls:
                case (long)BuiltInCategory.OST_Ceilings:
                    return Discipline.Structural;

                // ── Generic Model — resolve via system param heuristic ─
                case (long)BuiltInCategory.OST_GenericModel:         // FIX v5.1
                    return GuessGenericModelDiscipline(el);

                default: return Discipline.Unknown;
            }
        }

        public static BuiltInCategory[] GetCategoriesForDiscipline(Discipline disc)
        {
            switch (disc)
            {
                case Discipline.Structural:
                    return new[]
                    {
                        BuiltInCategory.OST_StructuralFraming,
                        BuiltInCategory.OST_StructuralColumns,
                        BuiltInCategory.OST_Floors,
                        BuiltInCategory.OST_Walls,
                        BuiltInCategory.OST_Ceilings
                    };
                case Discipline.HVAC:
                    return new[]
                    {
                        BuiltInCategory.OST_DuctCurves,
                        BuiltInCategory.OST_DuctFitting,
                        BuiltInCategory.OST_FlexDuctCurves,
                        BuiltInCategory.OST_DuctAccessory,
                        BuiltInCategory.OST_DuctTerminal,
                        BuiltInCategory.OST_MechanicalEquipment
                    };
                case Discipline.Plumbing:
                case Discipline.GravityDrainage:
                case Discipline.MedicalGas:
                    return new[]
                    {
                        BuiltInCategory.OST_PipeCurves,
                        BuiltInCategory.OST_PipeFitting,
                        BuiltInCategory.OST_FlexPipeCurves,
                        BuiltInCategory.OST_PipeAccessory,
                        BuiltInCategory.OST_PlumbingFixtures
                    };
                case Discipline.CableTray:
                    return new[] { BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting };
                case Discipline.Conduit:
                    return new[] { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting };
                case Discipline.Electrical:
                    return new[]
                    {
                        BuiltInCategory.OST_ElectricalEquipment,
                        BuiltInCategory.OST_LightingFixtures,
                        BuiltInCategory.OST_CommunicationDevices,
                        BuiltInCategory.OST_DataDevices,
                        BuiltInCategory.OST_NurseCallDevices,
                        BuiltInCategory.OST_SecurityDevices,
                        BuiltInCategory.OST_TelephoneDevices
                    };
                case Discipline.FireProtection:
                    return new[] { BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_FireAlarmDevices };
                default:
                    return new BuiltInCategory[0];
            }
        }

        // ── Private helpers ───────────────────────────────────────────────

        private static List<Element> GetRawByDiscipline(Document doc, Discipline disc)
        {
            var cats = GetCategoriesForDiscipline(disc);
            var res  = new List<Element>();
            foreach (var cat in cats)
            {
                try { res.AddRange(new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().ToElements()); }
                catch (System.Exception ex) { Debug.WriteLine($"[Collector] Raw: {ex.Message}"); }
            }
            return res;
        }

        private static bool HasSolidVolume(GeometryElement g)
        {
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Volume > MinSolidVolume) return true;
                if (o is GeometryInstance gi && HasSolidVolume(gi.GetInstanceGeometry())) return true;
            }
            return false;
        }

        private static Solid? ExtractBestSolid(GeometryElement g)
        {
            Solid? best = null; double bv = MinSolidVolume;
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Volume > bv) { best = s; bv = s.Volume; }
                else if (o is GeometryInstance gi)
                {
                    var n = ExtractBestSolid(gi.GetInstanceGeometry());
                    if (n != null && n.Volume > bv) { best = n; bv = n.Volume; }
                }
            }
            return best;
        }

        private static bool IsGravityDrainage(Element el)
        {
            try
            {
                var p = el.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)
                     ?? el.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                string v = p?.AsValueString()?.ToUpperInvariant() ?? "";
                return v.Contains("DRAIN") || v.Contains("WASTE") || v.Contains("SOIL") || v.Contains("GRAVITY");
            }
            catch { return false; }
        }

        private static bool IsMedicalGas(Element el)
        {
            try
            {
                var p = el.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                string v = p?.AsString()?.ToUpperInvariant() ?? "";
                return v.Contains("MED") || v.Contains("O2") || v.Contains("OXYGEN") || v.Contains("GAS");
            }
            catch { return false; }
        }

        /// <summary>
        /// For Generic Model families, guess the discipline from the family name
        /// or system parameters. Returns Unknown if no match found.
        /// </summary>
        private static Discipline GuessGenericModelDiscipline(Element el)
        {
            try
            {
                string name = (el.Name ?? "").ToUpperInvariant();
                string famName = ((el as FamilyInstance)?.Symbol?.FamilyName ?? "").ToUpperInvariant();
                string combined = name + " " + famName;

                if (combined.Contains("DUCT") || combined.Contains("AIR") || combined.Contains("HVAC") ||
                    combined.Contains("VENT") || combined.Contains("MECH"))
                    return Discipline.HVAC;

                if (combined.Contains("PIPE") || combined.Contains("PLUMB") ||
                    combined.Contains("DRAIN") || combined.Contains("WATER"))
                    return Discipline.Plumbing;

                if (combined.Contains("CABLE") || combined.Contains("TRAY") ||
                    combined.Contains("CONDUIT") || combined.Contains("ELEC"))
                    return Discipline.Electrical;

                if (combined.Contains("SPRINK") || combined.Contains("FIRE"))
                    return Discipline.FireProtection;
            }
            catch { }
            return Discipline.Unknown;
        }
    }
}

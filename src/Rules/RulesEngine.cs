// Rules/RulesEngine.cs  — v4.0
//
// PROFESSIONAL IMPROVEMENT #6: Rule-Based Tolerance Engine
//
// Replaces hardcoded dictionaries with external JSON configuration.
// Supports:
//   • Per project type rules (hospital, data center, industrial, high-rise)
//   • NFPA / ASHRAE / local code rule sets
//   • Configurable clearance per discipline pair
//   • Priority overrides
//   • Severity overrides per discipline pair
//
// Rule file location:
//   %APPDATA%\ClashResolveAI\Rules\{ruleset}.json
//   Fallback: embedded DefaultRules.json

using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ClashResolveAI.Rules
{
    // ══════════════════════════════════════════════════════════════════════
    //  RULE DATA MODEL
    // ══════════════════════════════════════════════════════════════════════

    public class ClashRule
    {
        [JsonProperty("clearance_mm")]  public double   ClearanceMM  { get; set; } = 50;
        [JsonProperty("priority")]      public string   Priority     { get; set; } = "Medium";
        [JsonProperty("severity")]      public string   Severity     { get; set; } = "Hard";
        [JsonProperty("moving")]        public string   Moving       { get; set; } = "auto";
        [JsonProperty("note")]          public string   Note         { get; set; } = "";
        [JsonProperty("code_ref")]      public string   CodeRef      { get; set; } = "";
        [JsonProperty("ignore")]        public bool     Ignore       { get; set; } = false;
    }

    public class RuleSet
    {
        [JsonProperty("name")]          public string Name         { get; set; } = "Default";
        [JsonProperty("version")]       public string Version      { get; set; } = "1.0";
        [JsonProperty("project_type")]  public string ProjectType  { get; set; } = "Generic";
        [JsonProperty("rules")]
        public Dictionary<string, ClashRule> Rules { get; set; } = new Dictionary<string, ClashRule>();
        [JsonProperty("discipline_pairs")]
        public Dictionary<string, ClashRule> DisciplinePairs { get; set; } = new Dictionary<string, ClashRule>();
        [JsonProperty("category_pairs")]
        public Dictionary<string, ClashRule> CategoryPairs   { get; set; } = new Dictionary<string, ClashRule>();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  RULES ENGINE
    // ══════════════════════════════════════════════════════════════════════

    public class RulesEngine
    {
        private RuleSet _ruleSet;
        private static readonly string _rulesFolder;

        static RulesEngine()
        {
            _rulesFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClashResolveAI", "Rules");
            Directory.CreateDirectory(_rulesFolder);

            // Export default rules on first run
            string defaultPath = Path.Combine(_rulesFolder, "DefaultRules.json");
            if (!File.Exists(defaultPath))
                ExportDefaultRules(defaultPath);
        }

        public RulesEngine(string rulesetName = "DefaultRules")
        {
            _ruleSet = LoadRuleSet(rulesetName);
        }

        // ════════════════════════════════════════════════════════════════
        //  EVALUATE  — resolve severity for a clash pair
        // ════════════════════════════════════════════════════════════════

        public (ClashSeverity sev, string rule) Evaluate(
            Element elA, Element elB,
            Discipline dA, Discipline dB,
            string ruleKey, ClashSeverity geometric)
        {
            // 1. Named rule key lookup
            if (_ruleSet.Rules.TryGetValue(ruleKey, out var namedRule))
                return Apply(namedRule, $"Rule:{ruleKey}");

            // 2. Category pair lookup (both orders)
            string cA      = elA.Category?.Name ?? "";
            string cB      = elB.Category?.Name ?? "";
            string catKeyAB = $"{cA}|{cB}";
            string catKeyBA = $"{cB}|{cA}";

            if (_ruleSet.CategoryPairs.TryGetValue(catKeyAB, out var catRule))
                return Apply(catRule, $"CatPair:{catKeyAB}");
            if (_ruleSet.CategoryPairs.TryGetValue(catKeyBA, out catRule))
                return Apply(catRule, $"CatPair:{catKeyBA}");

            // 3. Discipline pair lookup (both orders)
            string discKeyAB = $"{dA}|{dB}";
            string discKeyBA = $"{dB}|{dA}";

            if (_ruleSet.DisciplinePairs.TryGetValue(discKeyAB, out var discRule))
                return Apply(discRule, $"DiscPair:{discKeyAB}");
            if (_ruleSet.DisciplinePairs.TryGetValue(discKeyBA, out discRule))
                return Apply(discRule, $"DiscPair:{discKeyBA}");

            // 4. Fall through to geometric
            return (geometric, "Geometric");
        }

        private static (ClashSeverity, string) Apply(ClashRule rule, string label)
        {
            if (rule.Ignore) return (ClashSeverity.Ignore, $"{label}:Ignored");
            ClashSeverity sev = ParseSeverity(rule.Severity);
            return (sev, label);
        }

        // ════════════════════════════════════════════════════════════════
        //  GET CLEARANCE  — returns project-specific clearance in feet
        // ════════════════════════════════════════════════════════════════

        public double GetClearanceFt(Discipline dA, Discipline dB, string ruleKey = "")
        {
            // Check named rule first
            if (!string.IsNullOrEmpty(ruleKey) &&
                _ruleSet.Rules.TryGetValue(ruleKey, out var rule))
                return rule.ClearanceMM / 304.8;

            // Check discipline pair
            string key = $"{dA}|{dB}";
            if (_ruleSet.DisciplinePairs.TryGetValue(key, out rule))
                return rule.ClearanceMM / 304.8;

            // Default clearances
            return DefaultClearanceFt(dA, dB);
        }

        // ════════════════════════════════════════════════════════════════
        //  RULESET MANAGEMENT
        // ════════════════════════════════════════════════════════════════

        public static List<string> GetAvailableRuleSets()
        {
            var result = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(_rulesFolder, "*.json"))
                    result.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            return result;
        }

        public void SwitchRuleSet(string name) => _ruleSet = LoadRuleSet(name);

        public string CurrentRuleSetName => _ruleSet.Name;
        public string CurrentProjectType  => _ruleSet.ProjectType;

        private static RuleSet LoadRuleSet(string name)
        {
            string path = Path.Combine(_rulesFolder, $"{name}.json");
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<RuleSet>(json)
                           ?? BuildDefaultRuleSet();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RulesEngine] Load error '{name}': {ex.Message}");
                }
            }
            return BuildDefaultRuleSet();
        }

        // ════════════════════════════════════════════════════════════════
        //  DEFAULT RULES  — exported to JSON on first run
        // ════════════════════════════════════════════════════════════════

        private static RuleSet BuildDefaultRuleSet() => new RuleSet
        {
            Name        = "DefaultRules",
            Version     = "4.0",
            ProjectType = "Generic",
            Rules = new Dictionary<string, ClashRule>
            {
                { "Pipe_vs_Beam",          new ClashRule { ClearanceMM=50,  Severity="Critical", Priority="Critical", Moving="auto",  CodeRef="ASHRAE 90.1" }},
                { "Duct_vs_Beam",          new ClashRule { ClearanceMM=75,  Severity="Critical", Priority="Critical", Moving="auto"  }},
                { "FireMain_vs_Structure", new ClashRule { ClearanceMM=100, Severity="Critical", Priority="Critical", Moving="auto",  CodeRef="NFPA 13"     }},
                { "MedGas_vs_HVAC",        new ClashRule { ClearanceMM=150, Severity="Hard",     Priority="High",     Moving="HVAC"  }},
                { "CableTray_vs_Pipe",     new ClashRule { ClearanceMM=50,  Severity="Soft",     Priority="Medium",   Moving="auto"  }},
                { "Duct_Clearance",        new ClashRule { ClearanceMM=50,  Severity="Soft",     Priority="Medium",   Moving="auto"  }},
                { "Pipe_vs_Wall",          new ClashRule { ClearanceMM=0,   Severity="Ignore",   Ignore=true, Note="Pipes penetrate walls by design" }},
                { "Sprinkler_vs_Light",    new ClashRule { ClearanceMM=0,   Severity="Ignore",   Ignore=true, Note="Coordination item only"          }},
                { "Duct_vs_Ceiling",       new ClashRule { ClearanceMM=0,   Severity="Ignore",   Ignore=true                                        }},
                { "Generic",               new ClashRule { ClearanceMM=50,  Severity="Hard",     Priority="Medium",   Moving="auto"  }},
            },
            DisciplinePairs = new Dictionary<string, ClashRule>
            {
                { "GravityDrainage|Structural", new ClashRule { ClearanceMM=75,  Severity="Critical", Priority="Critical", Note="Gravity drainage NEVER rerouted upward" }},
                { "Structural|GravityDrainage", new ClashRule { ClearanceMM=75,  Severity="Critical", Priority="Critical" }},
                { "FireProtection|Structural",  new ClashRule { ClearanceMM=100, Severity="Critical", Priority="Critical", CodeRef="NFPA 13" }},
                { "MedicalGas|Structural",      new ClashRule { ClearanceMM=100, Severity="Critical", Priority="Critical" }},
                { "HVAC|Structural",            new ClashRule { ClearanceMM=75,  Severity="Hard",     Priority="High"      }},
                { "Plumbing|HVAC",              new ClashRule { ClearanceMM=50,  Severity="Hard",     Priority="High",     Moving="HVAC" }},
                { "CableTray|HVAC",             new ClashRule { ClearanceMM=50,  Severity="Soft",     Priority="Medium",   Moving="CableTray" }},
                { "CableTray|Plumbing",         new ClashRule { ClearanceMM=50,  Severity="Soft",     Priority="Medium",   Moving="CableTray" }},
                { "Conduit|HVAC",               new ClashRule { ClearanceMM=25,  Severity="Soft",     Priority="Low",      Moving="Conduit"   }},
            },
            CategoryPairs = new Dictionary<string, ClashRule>
            {
                { "Pipe Curves|Structural Framing",  new ClashRule { ClearanceMM=50,  Severity="Critical" }},
                { "Pipe Curves|Structural Columns",  new ClashRule { ClearanceMM=75,  Severity="Critical" }},
                { "Ducts|Structural Framing",        new ClashRule { ClearanceMM=75,  Severity="Critical" }},
                { "Pipe Curves|Walls",               new ClashRule { Ignore=true }},
                { "Sprinklers|Ceilings",             new ClashRule { Ignore=true }},
                { "Cable Trays|Conduits",            new ClashRule { ClearanceMM=25,  Severity="Soft"     }},
            }
        };

        private static void ExportDefaultRules(string path)
        {
            try
            {
                var defaults = BuildDefaultRuleSet();
                string json  = JsonConvert.SerializeObject(defaults, Formatting.Indented);
                File.WriteAllText(path, json);
                Debug.WriteLine($"[RulesEngine] Exported default rules to: {path}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RulesEngine] Export error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════

        private static ClashSeverity ParseSeverity(string s)
        {
            switch (s?.ToLowerInvariant())
            {
                case "critical":  return ClashSeverity.Critical;
                case "hard":      return ClashSeverity.Hard;
                case "soft":      return ClashSeverity.Soft;
                case "clearance": return ClashSeverity.Clearance;
                case "ignore":    return ClashSeverity.Ignore;
                default:          return ClashSeverity.Hard;
            }
        }

        private static double DefaultClearanceFt(Discipline a, Discipline b)
        {
            // Use the higher-priority discipline's clearance
            int pa = PriorityMatrix.Get(a);
            int pb = PriorityMatrix.Get(b);
            Discipline higher = pa <= pb ? a : b;
            switch (higher)
            {
                case Discipline.HVAC:            return 0.164;  // 50 mm
                case Discipline.GravityDrainage: return 0.246;  // 75 mm
                case Discipline.FireProtection:  return 0.328;  // 100 mm
                case Discipline.MedicalGas:      return 0.492;  // 150 mm
                case Discipline.Plumbing:        return 0.098;  // 30 mm
                case Discipline.Electrical:      return 0.197;  // 60 mm
                default:                         return 0.164;
            }
        }

        /// <summary>Human-readable clash description with rule reference.</summary>
        public static string Describe(ClashResult c)
        {
            string cA   = c.ElementA.Category?.Name ?? c.DisciplineA.ToString();
            string cB   = c.ElementB.Category?.Name ?? c.DisciplineB.ToString();
            string rule = string.IsNullOrEmpty(c.RuleApplied) ? "" : $" [{c.RuleApplied}]";
            switch (c.Severity)
            {
                case ClashSeverity.Critical:
                    return $"🔴 CRITICAL: {cA} intersects {cB} at {c.GridRef}/{c.LevelName}.{rule} Resolve before structural works.";
                case ClashSeverity.Hard:
                    return $"🟠 HARD: {cA} intersects {cB}. Gap: {c.GapMM:F1}mm.{rule}";
                case ClashSeverity.Soft:
                    return $"🟡 SOFT: {cA} within clearance zone of {cB}. Gap: {c.GapMM:F1}mm.{rule}";
                case ClashSeverity.Clearance:
                    return $"🔵 CLEARANCE: {cA} within maintenance zone of {cB}. Gap: {c.GapMM:F1}mm.{rule}";
                default:
                    return $"ℹ Clash: {cA} vs {cB}.";
            }
        }
    }
}

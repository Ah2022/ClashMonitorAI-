// Core/Models.cs  — v4.0  Professional BIM Coordination Engine
// Major additions:
//   • Full clash lifecycle states (NEW → CLOSED)
//   • ClashGroup — Navisworks-style grouped issues
//   • ClashMetadata — ownership, due dates, comments, revision log
//   • PriorityMatrix — semantic discipline ordering
//   • CoordinationZone — level/grid/workset filtering
//   • CachedGeometry — geometry cache data structure
//   • BCF-ready topic fields

using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace ClashResolveAI.Core
{
    // ══════════════════════════════════════════════════════════════════════
    //  ENUMERATIONS
    // ══════════════════════════════════════════════════════════════════════

    public enum Discipline
    {
        Structural          = 0,
        GravityDrainage     = 1,
        HVAC                = 2,
        Electrical          = 3,
        Plumbing            = 4,
        FireProtection      = 5,
        MedicalGas          = 6,
        CableTray           = 7,
        Conduit             = 8,
        Unknown             = 99
    }

    public enum ClashSeverity
    {
        Critical  = 0,   // Structural interference — immediate resolution required
        Hard      = 1,   // True solid overlap — must be resolved
        Soft      = 2,   // Within clearance zone — should be resolved
        Clearance = 3,   // Within maintenance zone — review required
        Ignore    = 99
    }

    /// <summary>Professional Navisworks-compatible clash lifecycle states.</summary>
    public enum ClashStatus
    {
        New       = 0,
        Active    = 1,
        InReview  = 2,
        Approved  = 3,
        OnSite    = 4,
        Resolved  = 5,
        Ignored   = 6,
        Closed    = 7
    }

    public enum ClashTestType
    {
        HardClash       = 0,   // Solid overlap (Boolean intersection)
        ClearanceClash  = 1,   // Expanded solid within clearance distance
        DuplicateClash  = 2    // Same pair reported more than once
    }

    public static class ClashType
    {
        public const string PipeVsBeam         = "Pipe_vs_Beam";
        public const string PipeVsWall         = "Pipe_vs_Wall";
        public const string DuctVsBeam         = "Duct_vs_Beam";
        public const string DuctClearance      = "Duct_Clearance";
        public const string CableTrayVsPipe    = "CableTray_vs_Pipe";
        public const string SprinklerVsLight   = "Sprinkler_vs_Light";
        public const string MedGasVsHVAC       = "MedGas_vs_HVAC";
        public const string FireMainVsStructure = "FireMain_vs_Structure";
        public const string Generic            = "Generic";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  PRIORITY MATRIX  — Navisworks coordination logic
    //  Lower number = higher construction priority (must NOT be moved)
    // ══════════════════════════════════════════════════════════════════════

    public static class PriorityMatrix
    {
        private static readonly Dictionary<Discipline, int> _priority =
            new Dictionary<Discipline, int>
            {
                { Discipline.Structural,      0 },   // NEVER MOVE
                { Discipline.FireProtection,  1 },   // Fire main — high
                { Discipline.GravityDrainage, 1 },   // Gravity — cannot reroute upward
                { Discipline.MedicalGas,      2 },   // Medical gas — critical
                { Discipline.HVAC,            3 },   // Large ducts — medium-high
                { Discipline.Plumbing,        4 },   // Plumbing pipes
                { Discipline.CableTray,       5 },   // Cable tray
                { Discipline.Electrical,      6 },   // Small conduit
                { Discipline.Conduit,         7 },   // Lowest
                { Discipline.Unknown,         99 }
            };

        public static int Get(Discipline d) =>
            _priority.TryGetValue(d, out int v) ? v : 99;

        /// <summary>Returns the discipline that should move to resolve this clash.</summary>
        public static Discipline GetMovingDiscipline(Discipline a, Discipline b) =>
            Get(a) >= Get(b) ? a : b;

        public static string GetPriorityLabel(Discipline d)
        {
            int p = Get(d);
            if (p == 0) return "NEVER MOVE";
            if (p <= 2) return "CRITICAL";
            if (p <= 3) return "HIGH";
            if (p <= 5) return "MEDIUM";
            return "LOW";
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CLASH METADATA  — full coordination lifecycle data
    // ══════════════════════════════════════════════════════════════════════

    public class ClashMetadata
    {
        public string       AssignedDiscipline  { get; set; } = "";
        public string       AssignedEngineer    { get; set; } = "";
        public DateTime?    DueDate             { get; set; }
        public string       Comments            { get; set; } = "";
        public string       ResolutionNotes     { get; set; } = "";
        public string       MeetingNotes        { get; set; } = "";
        public string       SnapshotPath        { get; set; } = "";
        public string       ViewpointGuid       { get; set; } = "";
        public List<ClashRevision> RevisionLog  { get; set; } = new List<ClashRevision>();
    }

    public class ClashRevision
    {
        public DateTime     Timestamp   { get; set; } = DateTime.Now;
        public string       Author      { get; set; } = "";
        public ClashStatus  OldStatus   { get; set; }
        public ClashStatus  NewStatus   { get; set; }
        public string       Comment     { get; set; } = "";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CLASH RESULT  — core detection output
    // ══════════════════════════════════════════════════════════════════════

    public class ClashResult
    {
        // Identity
        public string        ClashId           { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
        public string        GroupId           { get; set; } = "";   // set by grouping engine
        public DateTime      DetectedAt        { get; set; } = DateTime.Now;

        // Elements
        public Element       ElementA          { get; set; } = null!;
        public Element       ElementB          { get; set; } = null!;
        public Discipline    DisciplineA       { get; set; }
        public Discipline    DisciplineB       { get; set; }
        public string        SystemTypeA       { get; set; } = "";
        public string        SystemTypeB       { get; set; } = "";

        // Geometry
        public string        ClashType         { get; set; } = Core.ClashType.Generic;
        public ClashTestType TestType          { get; set; } = ClashTestType.HardClash;
        public ClashSeverity Severity          { get; set; }
        public double        GapMM             { get; set; }
        public double        OverlapVolumeMM3  { get; set; }
        public XYZ           ClashPoint        { get; set; } = XYZ.Zero;

        // Location
        public string        LevelName         { get; set; } = "";
        public string        GridRef           { get; set; } = "";
        public string        ZoneName          { get; set; } = "";
        public string        LocationText      { get; set; } = "";

        // Rules & priority
        public string        RuleApplied       { get; set; } = "";
        public string        Priority          { get; set; } = "Medium";
        public string        MovingDiscipline  { get; set; } = "";

        // Lifecycle — NEW in v4.0
        public ClashStatus   Status            { get; set; } = ClashStatus.New;
        public ClashMetadata Metadata          { get; set; } = new ClashMetadata();

        // AI / RFI
        public string        AiSuggestion      { get; set; } = "";
        public string        RfiText           { get; set; } = "";
        public List<RoutingAlternative> Alternatives { get; set; } = new List<RoutingAlternative>();

        // Links
        public string        LinkFileA         { get; set; } = "";
        public string        LinkFileB         { get; set; } = "";

        // Duplicate suppression key (NormalizedPairKey)
        public string NormalizedKey =>
            string.Concat(
                Math.Min(ElementA?.Id.Value ?? 0, ElementB?.Id.Value ?? 0).ToString(),
                "-",
                Math.Max(ElementA?.Id.Value ?? 0, ElementB?.Id.Value ?? 0).ToString());
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CLASH GROUP  — Navisworks-style grouped issues
    // ══════════════════════════════════════════════════════════════════════

    public class ClashGroup
    {
        public string            GroupId          { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
        public string            GroupTitle       { get; set; } = "";
        public ClashSeverity     MaxSeverity      { get; set; }
        public ClashStatus       Status           { get; set; } = ClashStatus.New;
        public string            GroupingReason   { get; set; } = "";  // "Same route", "Same zone", etc.
        public string            LevelName        { get; set; } = "";
        public string            ZoneName         { get; set; } = "";
        public string            GridRef          { get; set; } = "";
        public Discipline        DisciplineA      { get; set; }
        public Discipline        DisciplineB      { get; set; }
        public List<ClashResult> Clashes          { get; set; } = new List<ClashResult>();
        public int               Count            => Clashes.Count;
        public string            PrimaryOffender  { get; set; } = "";  // Root cause element
        public DateTime          DetectedAt       { get; set; } = DateTime.Now;
        public ClashMetadata     Metadata         { get; set; } = new ClashMetadata();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CACHED GEOMETRY  — geometry cache data structure
    // ══════════════════════════════════════════════════════════════════════

    public class CachedGeometry
    {
        public ElementId     ElementId       { get; set; } = null!;
        public Solid?        Solid           { get; set; }
        public BoundingBoxXYZ BoundingBox    { get; set; } = null!;
        public XYZ           Center          { get; set; } = XYZ.Zero;
        public BoundingBoxXYZ ClearanceBox   { get; set; } = null!;
        public Discipline    Discipline      { get; set; }
        public string        SystemType      { get; set; } = "";
        public string        LevelId         { get; set; } = "";
        public DateTime      CachedAt        { get; set; } = DateTime.Now;
        public bool          IsValid         { get; set; } = true;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  COORDINATION ZONE  — scan filtering
    // ══════════════════════════════════════════════════════════════════════

    public class CoordinationZone
    {
        public string   Name         { get; set; } = "";
        public string   LevelName    { get; set; } = "";
        public string   GridRangeMin { get; set; } = "";
        public string   GridRangeMax { get; set; } = "";
        public string   WorksetName  { get; set; } = "";
        public string   LinkedModel  { get; set; } = "";
        public bool     IsActive     { get; set; } = true;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DISCIPLINE MATRIX ENTRY
    // ══════════════════════════════════════════════════════════════════════

    public class ClashMatrixEntry
    {
        public Discipline Source  { get; set; }
        public Discipline Target  { get; set; }
        public bool       Check   { get; set; }
        public string     RuleKey { get; set; } = ClashType.Generic;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ROUTING ALTERNATIVES
    // ══════════════════════════════════════════════════════════════════════

    public class RoutingAlternative
    {
        public string Description { get; set; } = "";
        public string Direction   { get; set; } = "";
        public double OffsetMM    { get; set; }
        public string Confidence  { get; set; } = "Medium";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DASHBOARD STATISTICS
    // ══════════════════════════════════════════════════════════════════════

    public class DashboardStats
    {
        public int    TotalClashes       { get; set; }
        public int    Critical           { get; set; }
        public int    Hard               { get; set; }
        public int    Soft               { get; set; }
        public int    ClearanceOnly      { get; set; }
        public int    Resolved           { get; set; }
        public int    Open               { get; set; }
        public int    Ignored            { get; set; }
        public int    GroupCount         { get; set; }
        public double CoordinationHealthScore { get; set; }  // 0-100
        public DateTime LastScan         { get; set; }
        public List<ClashResult>  RecentClashes      { get; set; } = new List<ClashResult>();
        public List<ClashGroup>   ActiveGroups        { get; set; } = new List<ClashGroup>();
        public Dictionary<string, int> DisciplineMatrix { get; set; } = new Dictionary<string, int>();
        public List<WeeklyTrend>  Trends              { get; set; } = new List<WeeklyTrend>();
    }

    public class WeeklyTrend
    {
        public string WeekLabel    { get; set; } = "";
        public int    ClashCount   { get; set; }
        public int    Resolved     { get; set; }
        public DateTime WeekStart  { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  BCF TOPIC  — BIM Collaboration Format 2.1
    // ══════════════════════════════════════════════════════════════════════

    public class BcfTopic
    {
        public string   Guid            { get; set; } = System.Guid.NewGuid().ToString();
        public string   TopicType       { get; set; } = "Clash";
        public string   TopicStatus     { get; set; } = "Open";
        public string   Title           { get; set; } = "";
        public string   Description     { get; set; } = "";
        public string   AssignedTo      { get; set; } = "";
        public string   CreationAuthor  { get; set; } = "ClashResolveAI";
        public DateTime CreationDate    { get; set; } = DateTime.Now;
        public string   Priority        { get; set; } = "Normal";
        public List<BcfComment>   Comments   { get; set; } = new List<BcfComment>();
        public List<BcfViewpoint> Viewpoints { get; set; } = new List<BcfViewpoint>();
        public List<string>       Labels     { get; set; } = new List<string>();
    }

    public class BcfComment
    {
        public string   Guid        { get; set; } = System.Guid.NewGuid().ToString();
        public string   Author      { get; set; } = "";
        public string   Comment     { get; set; } = "";
        public DateTime Date        { get; set; } = DateTime.Now;
        public string   ViewpointGuid { get; set; } = "";
    }

    public class BcfViewpoint
    {
        public string   Guid        { get; set; } = System.Guid.NewGuid().ToString();
        public XYZ      CameraPos   { get; set; } = XYZ.Zero;
        public XYZ      CameraDir   { get; set; } = XYZ.Zero;
        public XYZ      CameraUp    { get; set; } = XYZ.Zero;
        public double   FieldOfView { get; set; } = 60.0;
        public string   SnapshotPath { get; set; } = "";
        public List<string> SelectedComponents { get; set; } = new List<string>();
    }
}

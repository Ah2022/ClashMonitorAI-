// Core/ClashDatabase.cs  — v4.0
//
// PROFESSIONAL IMPROVEMENT #12: Persistent Clash Database
//
// Clashes no longer live only in session memory.
// This service provides:
//   • SQLite persistence across Revit sessions
//   • Full clash lifecycle tracking
//   • Comments, assignments, revision history
//   • Analytics queries (trends, discipline matrix)
//   • BCF-compatible export data
//
// The database is stored at:
//   %APPDATA%\ClashResolveAI\{ProjectName}.clash.db

using ClashResolveAI.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Core
{
    /// <summary>
    /// SQLite-backed persistent storage for clash results, groups,
    /// lifecycle history, and coordination analytics.
    /// </summary>
    public class ClashDatabase : IDisposable
    {
        // ── Singleton ─────────────────────────────────────────────────────
        private static ClashDatabase? _instance;
        public  static ClashDatabase   Instance =>
            _instance ?? (_instance = new ClashDatabase());

        private SQLiteConnection? _conn;
        private string            _dbPath = "";

        // ════════════════════════════════════════════════════════════════
        //  INIT / OPEN
        // ════════════════════════════════════════════════════════════════

        /// <summary>Open or create database for a given project.</summary>
        public void Open(string projectName)
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClashResolveAI");
                Directory.CreateDirectory(folder);

                string safeName = string.Join("_",
                    projectName.Split(Path.GetInvalidFileNameChars()));
                _dbPath = Path.Combine(folder, $"{safeName}.clash.db");

                _conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
                _conn.Open();

                CreateSchema();
                Debug.WriteLine($"[ClashDB] Opened: {_dbPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] Open error: {ex.Message}");
                _conn = null;
            }
        }

        private void CreateSchema()
        {
            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS Clashes (
                    ClashId         TEXT PRIMARY KEY,
                    GroupId         TEXT,
                    DetectedAt      TEXT,
                    ElementAId      INTEGER,
                    ElementBId      INTEGER,
                    DisciplineA     TEXT,
                    DisciplineB     TEXT,
                    ClashType       TEXT,
                    Severity        TEXT,
                    Status          TEXT DEFAULT 'New',
                    GapMM           REAL,
                    OverlapVolMM3   REAL,
                    ClashPoint      TEXT,
                    LevelName       TEXT,
                    GridRef         TEXT,
                    ZoneName        TEXT,
                    Priority        TEXT,
                    MovingDisc      TEXT,
                    RuleApplied     TEXT,
                    AiSuggestion    TEXT,
                    RfiText         TEXT,
                    LinkFileA       TEXT,
                    LinkFileB       TEXT,
                    MetadataJson    TEXT,
                    UpdatedAt       TEXT
                );");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS ClashGroups (
                    GroupId         TEXT PRIMARY KEY,
                    GroupTitle      TEXT,
                    MaxSeverity     TEXT,
                    Status          TEXT DEFAULT 'New',
                    GroupingReason  TEXT,
                    LevelName       TEXT,
                    ZoneName        TEXT,
                    GridRef         TEXT,
                    DisciplineA     TEXT,
                    DisciplineB     TEXT,
                    PrimaryOffender TEXT,
                    DetectedAt      TEXT,
                    MetadataJson    TEXT
                );");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS ClashRevisions (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClashId         TEXT,
                    Timestamp       TEXT,
                    Author          TEXT,
                    OldStatus       TEXT,
                    NewStatus       TEXT,
                    Comment         TEXT,
                    FOREIGN KEY(ClashId) REFERENCES Clashes(ClashId)
                );");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS WeeklySnapshots (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    WeekStart       TEXT,
                    TotalClashes    INTEGER,
                    Resolved        INTEGER,
                    Critical        INTEGER
                );");

            // Indexes for performance
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_clash_status ON Clashes(Status);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_clash_severity ON Clashes(Severity);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_clash_group ON Clashes(GroupId);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_clash_level ON Clashes(LevelName);");
        }

        // ════════════════════════════════════════════════════════════════
        //  UPSERT CLASH  — insert or update a single clash
        // ════════════════════════════════════════════════════════════════

        public void UpsertClash(ClashResult c)
        {
            if (_conn == null || c == null) return;
            try
            {
                string metaJson = JsonConvert.SerializeObject(c.Metadata);

                ExecuteNonQuery(@"
                    INSERT OR REPLACE INTO Clashes
                    (ClashId, GroupId, DetectedAt, ElementAId, ElementBId,
                     DisciplineA, DisciplineB, ClashType, Severity, Status,
                     GapMM, OverlapVolMM3, ClashPoint, LevelName, GridRef,
                     ZoneName, Priority, MovingDisc, RuleApplied,
                     AiSuggestion, RfiText, LinkFileA, LinkFileB,
                     MetadataJson, UpdatedAt)
                    VALUES
                    (@id, @gid, @det, @eaId, @ebId,
                     @da, @db, @ct, @sev, @status,
                     @gap, @vol, @pt, @lv, @gr,
                     @zone, @pri, @mdisc, @rule,
                     @ai, @rfi, @lfa, @lfb,
                     @meta, @upd)",
                    ("@id",     c.ClashId),
                    ("@gid",    c.GroupId),
                    ("@det",    c.DetectedAt.ToString("o")),
                    ("@eaId",   c.ElementA?.Id.Value ?? 0),
                    ("@ebId",   c.ElementB?.Id.Value ?? 0),
                    ("@da",     c.DisciplineA.ToString()),
                    ("@db",     c.DisciplineB.ToString()),
                    ("@ct",     c.ClashType),
                    ("@sev",    c.Severity.ToString()),
                    ("@status", c.Status.ToString()),
                    ("@gap",    c.GapMM),
                    ("@vol",    c.OverlapVolumeMM3),
                    ("@pt",     $"{c.ClashPoint.X:F3},{c.ClashPoint.Y:F3},{c.ClashPoint.Z:F3}"),
                    ("@lv",     c.LevelName),
                    ("@gr",     c.GridRef),
                    ("@zone",   c.ZoneName),
                    ("@pri",    c.Priority),
                    ("@mdisc",  c.MovingDiscipline),
                    ("@rule",   c.RuleApplied),
                    ("@ai",     c.AiSuggestion),
                    ("@rfi",    c.RfiText),
                    ("@lfa",    c.LinkFileA),
                    ("@lfb",    c.LinkFileB),
                    ("@meta",   metaJson),
                    ("@upd",    DateTime.Now.ToString("o")));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] UpsertClash error: {ex.Message}");
            }
        }

        /// <summary>Bulk insert clash results (uses a transaction for performance).</summary>
        public void BulkInsertClashes(IEnumerable<ClashResult> clashes)
        {
            if (_conn == null) return;
            using (var tx = _conn.BeginTransaction())
            {
                try
                {
                    foreach (var c in clashes)
                        UpsertClash(c);
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.Rollback();
                    Debug.WriteLine($"[ClashDB] BulkInsert error: {ex.Message}");
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  UPDATE STATUS  — lifecycle transition with revision log
        // ════════════════════════════════════════════════════════════════

        public void UpdateClashStatus(
            string clashId, ClashStatus newStatus,
            string author = "", string comment = "")
        {
            if (_conn == null) return;
            try
            {
                // Get current status for revision log
                var oldStatusStr = ExecuteScalar(
                    "SELECT Status FROM Clashes WHERE ClashId=@id",
                    ("@id", clashId)) as string ?? "New";
                ClashStatus.TryParse(oldStatusStr, out ClashStatus oldStatus);

                // Update status
                ExecuteNonQuery(
                    "UPDATE Clashes SET Status=@s, UpdatedAt=@u WHERE ClashId=@id",
                    ("@s",  newStatus.ToString()),
                    ("@u",  DateTime.Now.ToString("o")),
                    ("@id", clashId));

                // Log revision
                ExecuteNonQuery(@"
                    INSERT INTO ClashRevisions (ClashId, Timestamp, Author, OldStatus, NewStatus, Comment)
                    VALUES (@cid, @ts, @auth, @old, @new, @cmt)",
                    ("@cid",  clashId),
                    ("@ts",   DateTime.Now.ToString("o")),
                    ("@auth", author),
                    ("@old",  oldStatus.ToString()),
                    ("@new",  newStatus.ToString()),
                    ("@cmt",  comment));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] UpdateStatus error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  UPSERT GROUP
        // ════════════════════════════════════════════════════════════════

        public void UpsertGroup(ClashGroup g)
        {
            if (_conn == null || g == null) return;
            try
            {
                string metaJson = JsonConvert.SerializeObject(g.Metadata);
                ExecuteNonQuery(@"
                    INSERT OR REPLACE INTO ClashGroups
                    (GroupId, GroupTitle, MaxSeverity, Status, GroupingReason,
                     LevelName, ZoneName, GridRef, DisciplineA, DisciplineB,
                     PrimaryOffender, DetectedAt, MetadataJson)
                    VALUES
                    (@gid, @title, @sev, @status, @reason,
                     @lv, @zone, @gr, @da, @db,
                     @off, @det, @meta)",
                    ("@gid",    g.GroupId),
                    ("@title",  g.GroupTitle),
                    ("@sev",    g.MaxSeverity.ToString()),
                    ("@status", g.Status.ToString()),
                    ("@reason", g.GroupingReason),
                    ("@lv",     g.LevelName),
                    ("@zone",   g.ZoneName),
                    ("@gr",     g.GridRef),
                    ("@da",     g.DisciplineA.ToString()),
                    ("@db",     g.DisciplineB.ToString()),
                    ("@off",    g.PrimaryOffender),
                    ("@det",    g.DetectedAt.ToString("o")),
                    ("@meta",   metaJson));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] UpsertGroup error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  QUERIES
        // ════════════════════════════════════════════════════════════════

        public int GetClashCount(ClashStatus? status = null)
        {
            if (_conn == null) return 0;
            try
            {
                string sql = status.HasValue
                    ? "SELECT COUNT(*) FROM Clashes WHERE Status=@s"
                    : "SELECT COUNT(*) FROM Clashes";
                var result = status.HasValue
                    ? ExecuteScalar(sql, ("@s", status.Value.ToString()))
                    : ExecuteScalar(sql);
                return Convert.ToInt32(result);
            }
            catch { return 0; }
        }

        public int GetGroupCount()
        {
            if (_conn == null) return 0;
            try { return Convert.ToInt32(ExecuteScalar("SELECT COUNT(*) FROM ClashGroups")); }
            catch { return 0; }
        }

        /// <summary>Returns discipline-pair clash counts for the matrix view.</summary>
        public Dictionary<string, int> GetDisciplineMatrix()
        {
            var result = new Dictionary<string, int>();
            if (_conn == null) return result;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT DisciplineA, DisciplineB, COUNT(*) as cnt
                    FROM Clashes
                    WHERE Status NOT IN ('Resolved','Ignored','Closed')
                    GROUP BY DisciplineA, DisciplineB";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = $"{reader.GetString(0)} vs {reader.GetString(1)}";
                    result[key] = reader.GetInt32(2);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] DisciplineMatrix error: {ex.Message}");
            }
            return result;
        }

        /// <summary>Weekly trend data for the dashboard chart.</summary>
        public List<WeeklyTrend> GetWeeklyTrends(int weeksBack = 8)
        {
            var result = new List<WeeklyTrend>();
            if (_conn == null) return result;
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT strftime('%Y-W%W', DetectedAt) as week,
                           COUNT(*) as total,
                           SUM(CASE WHEN Status IN ('Resolved','Closed') THEN 1 ELSE 0 END) as res
                    FROM Clashes
                    WHERE DetectedAt >= @since
                    GROUP BY week ORDER BY week";
                cmd.Parameters.AddWithValue("@since",
                    DateTime.Now.AddDays(-weeksBack * 7).ToString("o"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new WeeklyTrend
                    {
                        WeekLabel  = reader.GetString(0),
                        ClashCount = reader.GetInt32(1),
                        Resolved   = reader.GetInt32(2)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] Trends error: {ex.Message}");
            }
            return result;
        }

        /// <summary>Health score 0–100 based on resolution rate and open critical count.</summary>
        public double GetHealthScore()
        {
            if (_conn == null) return 0;
            try
            {
                int total    = GetClashCount();
                int resolved = GetClashCount(ClashStatus.Resolved) +
                               GetClashCount(ClashStatus.Closed);
                int critical = GetClashCount(ClashStatus.Active);

                if (total == 0) return 100;
                double resRate   = (double)resolved / total * 60;
                double critPen   = Math.Min(40, critical * 2.0);
                return Math.Max(0, Math.Min(100, resRate + (40 - critPen)));
            }
            catch { return 0; }
        }

        /// <summary>Save weekly snapshot for trend tracking.</summary>
        public void SaveWeeklySnapshot()
        {
            if (_conn == null) return;
            try
            {
                // Check if already saved this week
                string weekStart = GetWeekStart().ToString("o");
                var existing = ExecuteScalar(
                    "SELECT COUNT(*) FROM WeeklySnapshots WHERE WeekStart=@w",
                    ("@w", weekStart));
                if (Convert.ToInt32(existing) > 0) return;

                ExecuteNonQuery(@"
                    INSERT INTO WeeklySnapshots (WeekStart, TotalClashes, Resolved, Critical)
                    VALUES (@w, @t, @r, @c)",
                    ("@w", weekStart),
                    ("@t", GetClashCount()),
                    ("@r", GetClashCount(ClashStatus.Resolved)),
                    ("@c", GetClashCount(ClashStatus.Active)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashDB] Snapshot error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  PRIVATE HELPERS
        // ════════════════════════════════════════════════════════════════

        private void ExecuteNonQuery(string sql, params (string, object)[] parms)
        {
            if (_conn == null) return;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in parms)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private object? ExecuteScalar(string sql, params (string, object)[] parms)
        {
            if (_conn == null) return null;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in parms)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            return cmd.ExecuteScalar();
        }

        private static DateTime GetWeekStart()
        {
            var today = DateTime.Today;
            int diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            return today.AddDays(-diff);
        }

        public void Dispose()
        {
            _conn?.Close();
            _conn?.Dispose();
            _conn = null;
        }
    }
}

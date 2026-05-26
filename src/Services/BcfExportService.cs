// Services/BcfExportService.cs  — v4.1
//
// FIX v4.1 — BCF export crash:
//   "The ':' character, hexadecimal value 0x3A, cannot be included in a name."
//
//   Root cause: LINQ to XML forbids a literal colon in an attribute name string.
//   new XAttribute("xsi:noNamespaceSchemaLocation", ...) is invalid.
//   Fix: declare the xsi XNamespace object and use it as the attribute prefix.
//   new XAttribute(xsi + "noNamespaceSchemaLocation", ...) is correct.
//
//   Also fixed: project name sanitisation to remove characters illegal in XML
//   attribute values (angle brackets, ampersand, control chars).

using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ClashResolveAI.Services
{
    public class BcfExportService
    {
        private const string BCF_VERSION = "2.1";
        private const string AUTHOR      = "ClashResolveAI v4.0";

        // Declared once — used by every XML builder that needs xsi attributes
        private static readonly XNamespace _xsi =
            "http://www.w3.org/2001/XMLSchema-instance";

        // ════════════════════════════════════════════════════════════════
        //  EXPORT  — write clash groups to BCF 2.1 ZIP
        // ════════════════════════════════════════════════════════════════

        public string ExportGroups(
            List<ClashGroup> groups,
            string projectName,
            string outputPath)
        {
            try
            {
                // Sanitise project name for safe use in filename and XML
                string safeName = SanitiseName(projectName);

                string fileName = Path.Combine(outputPath,
                    $"{safeName}_Clashes_{DateTime.Now:yyyyMMdd_HHmm}.bcf");

                using (var archive = ZipFile.Open(fileName, ZipArchiveMode.Create))
                {
                    WriteEntry(archive, "bcf.version", BuildVersionXml());
                    WriteEntry(archive, "project.bcfp", BuildProjectXml(safeName));

                    int topicIndex = 1;
                    foreach (var group in groups)
                    {
                        string topicGuid = Guid.NewGuid().ToString();
                        var    topic     = BuildTopicFromGroup(group, topicIndex++);

                        WriteEntry(archive, $"{topicGuid}/markup.bcf",
                            BuildMarkupXml(topic, group));

                        if (group.Clashes.Any())
                        {
                            string vpGuid = Guid.NewGuid().ToString();
                            WriteEntry(archive, $"{topicGuid}/{vpGuid}.bcfv",
                                BuildViewpointXml(group.Clashes[0], vpGuid));
                        }
                    }
                }

                Debug.WriteLine($"[BCF] Exported: {fileName}");
                return fileName;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BCF] Export error: {ex.Message}");
                throw;
            }
        }

        public string ExportClashes(
            List<ClashResult> clashes,
            string projectName,
            string outputPath)
        {
            var grouper = new Engine.ClashGroupingEngine();
            var groups  = grouper.GroupClashes(clashes);
            return ExportGroups(groups, projectName, outputPath);
        }

        // ════════════════════════════════════════════════════════════════
        //  IMPORT
        // ════════════════════════════════════════════════════════════════

        public List<BcfTopic> ImportBcf(string bcfPath)
        {
            var topics = new List<BcfTopic>();
            try
            {
                using (var archive = ZipFile.OpenRead(bcfPath))
                {
                    foreach (var entry in archive.Entries.Where(e => e.Name == "markup.bcf"))
                    {
                        try
                        {
                            using var stream = entry.Open();
                            using var reader = new StreamReader(stream);
                            var topic = ParseMarkupXml(reader.ReadToEnd());
                            if (topic != null) topics.Add(topic);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[BCF] Parse markup error: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BCF] Import error: {ex.Message}");
            }
            return topics;
        }

        // ════════════════════════════════════════════════════════════════
        //  XML BUILDERS
        // ════════════════════════════════════════════════════════════════

        // FIX: use XNamespace for xsi prefix — literal colon in name crashes LINQ to XML
        private static string BuildVersionXml()
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Version",
                    new XAttribute("VersionId", BCF_VERSION),
                    new XAttribute(XNamespace.Xmlns + "xsi", _xsi.NamespaceName),
                    new XAttribute(_xsi + "noNamespaceSchemaLocation",
                        "https://raw.githubusercontent.com/buildingSMART/BCF-XML/master/Schemas/version.xsd"),
                    new XElement("DetailedVersion",
                        $"BCF {BCF_VERSION} — Generated by {AUTHOR}")
                )).ToStringWithDeclaration();
        }

        private static string BuildProjectXml(string projectName)
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("ProjectExtension",
                    new XElement("Project",
                        new XAttribute("ProjectId", Guid.NewGuid().ToString()),
                        new XElement("Name", projectName)
                    ),
                    new XElement("ExtensionSchema", "")
                )).ToStringWithDeclaration();
        }

        private static string BuildMarkupXml(BcfTopic topic, ClashGroup group)
        {
            var components = new XElement("Components");
            foreach (var clash in group.Clashes.Take(50))
            {
                if (clash.ElementA != null)
                    components.Add(new XElement("Component",
                        new XAttribute("IfcGuid", GetIfcGuid(clash.ElementA.Id.Value)),
                        new XAttribute("AuthoringToolId", clash.ElementA.Id.Value.ToString()),
                        new XElement("OriginatingSystem", AUTHOR)));
                if (clash.ElementB != null)
                    components.Add(new XElement("Component",
                        new XAttribute("IfcGuid", GetIfcGuid(clash.ElementB.Id.Value)),
                        new XAttribute("AuthoringToolId", clash.ElementB.Id.Value.ToString()),
                        new XElement("OriginatingSystem", AUTHOR)));
            }

            var commentsEl = new XElement("Comments");
            if (!string.IsNullOrEmpty(group.Metadata?.Comments))
            {
                commentsEl.Add(new XElement("Comment",
                    new XAttribute("Guid", Guid.NewGuid().ToString()),
                    new XElement("Date",   DateTime.Now.ToString("o")),
                    new XElement("Author", AUTHOR),
                    new XElement("Comment", group.Metadata.Comments)));
            }

            // Coordination notes — sanitise content so no XML-illegal chars sneak in
            string notes = SanitiseXmlContent(BuildCoordinationNotes(group));
            commentsEl.Add(new XElement("Comment",
                new XAttribute("Guid", Guid.NewGuid().ToString()),
                new XElement("Date",   DateTime.Now.ToString("o")),
                new XElement("Author", AUTHOR),
                new XElement("Comment", notes)));

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Markup",
                    new XElement("Header"),
                    new XElement("Topic",
                        new XAttribute("Guid",        topic.Guid),
                        new XAttribute("TopicType",   topic.TopicType),
                        new XAttribute("TopicStatus", topic.TopicStatus),
                        new XElement("Title",          SanitiseXmlContent(topic.Title)),
                        new XElement("Priority",       topic.Priority),
                        new XElement("Index",          0),
                        new XElement("CreationDate",   topic.CreationDate.ToString("o")),
                        new XElement("CreationAuthor", topic.CreationAuthor),
                        new XElement("Description",    SanitiseXmlContent(topic.Description)),
                        new XElement("AssignedTo",     topic.AssignedTo),
                        topic.Labels.Select(l => new XElement("Labels", l))
                    ),
                    new XElement("Viewpoints",
                        new XElement("ViewPoint",
                            new XAttribute("Guid", Guid.NewGuid().ToString()),
                            new XElement("Viewpoint", "viewpoint.bcfv"),
                            new XElement("Snapshot",  "snapshot.png")
                        )
                    ),
                    commentsEl,
                    components
                )).ToStringWithDeclaration();
        }

        private static string BuildViewpointXml(ClashResult clash, string vpGuid)
        {
            double toM(double ft) => ft * 0.3048;
            double cx = toM(clash.ClashPoint.X);
            double cy = toM(clash.ClashPoint.Y);
            double cz = toM(clash.ClashPoint.Z);
            double camX = cx - 3.0, camY = cy - 3.0, camZ = cz + 2.0;

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("VisualizationInfo",
                    new XAttribute("Guid", vpGuid),
                    new XElement("PerspectiveCamera",
                        new XElement("CameraViewPoint",
                            new XElement("X", camX.ToString("F4")),
                            new XElement("Y", camY.ToString("F4")),
                            new XElement("Z", camZ.ToString("F4"))),
                        new XElement("CameraDirection",
                            new XElement("X", (cx - camX).ToString("F4")),
                            new XElement("Y", (cy - camY).ToString("F4")),
                            new XElement("Z", (cz - camZ).ToString("F4"))),
                        new XElement("CameraUpVector",
                            new XElement("X", "0"),
                            new XElement("Y", "0"),
                            new XElement("Z", "1")),
                        new XElement("FieldOfView", "60")),
                    new XElement("Lines"),
                    new XElement("ClippingPlanes"),
                    new XElement("Bitmaps")
                )).ToStringWithDeclaration();
        }

        // ════════════════════════════════════════════════════════════════
        //  IMPORT PARSER
        // ════════════════════════════════════════════════════════════════

        private static BcfTopic? ParseMarkupXml(string xml)
        {
            try
            {
                var doc     = XDocument.Parse(xml);
                var topicEl = doc.Root?.Element("Topic");
                if (topicEl == null) return null;

                var topic = new BcfTopic
                {
                    Guid           = topicEl.Attribute("Guid")?.Value        ?? Guid.NewGuid().ToString(),
                    TopicType      = topicEl.Attribute("TopicType")?.Value    ?? "Clash",
                    TopicStatus    = topicEl.Attribute("TopicStatus")?.Value  ?? "Open",
                    Title          = topicEl.Element("Title")?.Value          ?? "",
                    Description    = topicEl.Element("Description")?.Value    ?? "",
                    AssignedTo     = topicEl.Element("AssignedTo")?.Value     ?? "",
                    Priority       = topicEl.Element("Priority")?.Value       ?? "Normal",
                    CreationAuthor = topicEl.Element("CreationAuthor")?.Value ?? ""
                };

                if (DateTime.TryParse(topicEl.Element("CreationDate")?.Value, out DateTime dt))
                    topic.CreationDate = dt;

                var commentsEl = doc.Root?.Element("Comments");
                if (commentsEl != null)
                {
                    foreach (var cel in commentsEl.Elements("Comment"))
                    {
                        topic.Comments.Add(new BcfComment
                        {
                            Guid    = cel.Attribute("Guid")?.Value ?? Guid.NewGuid().ToString(),
                            Author  = cel.Element("Author")?.Value  ?? "",
                            Comment = cel.Element("Comment")?.Value ?? ""
                        });
                    }
                }
                return topic;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BCF] ParseMarkup: {ex.Message}");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════

        private static BcfTopic BuildTopicFromGroup(ClashGroup group, int index)
        {
            string statusBcf = group.Status == ClashStatus.Resolved ? "Resolved" :
                               group.Status == ClashStatus.Closed   ? "Closed"   :
                               group.Status == ClashStatus.Ignored  ? "Closed"   : "Open";

            string priority  = group.MaxSeverity == ClashSeverity.Critical ? "Critical" :
                               group.MaxSeverity == ClashSeverity.Hard     ? "Major"    :
                               group.MaxSeverity == ClashSeverity.Soft     ? "Normal"   : "Minor";

            return new BcfTopic
            {
                TopicType      = "Clash",
                TopicStatus    = statusBcf,
                Title          = group.GroupTitle,
                Description    = BuildCoordinationNotes(group),
                Priority       = priority,
                AssignedTo     = group.Metadata?.AssignedEngineer ?? "",
                CreationAuthor = AUTHOR,
                Labels         = new List<string>
                {
                    group.DisciplineA.ToString(),
                    group.DisciplineB.ToString(),
                    group.LevelName,
                    group.MaxSeverity.ToString()
                }.Where(l => !string.IsNullOrEmpty(l)).ToList()
            };
        }

        private static string BuildCoordinationNotes(ClashGroup group)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Group: {group.GroupTitle}");
            sb.AppendLine($"Severity: {group.MaxSeverity}");
            sb.AppendLine($"Clash Count: {group.Count}");
            sb.AppendLine($"Level: {group.LevelName}");
            if (!string.IsNullOrEmpty(group.GridRef))
                sb.AppendLine($"Grid: {group.GridRef}");
            if (!string.IsNullOrEmpty(group.PrimaryOffender))
                sb.AppendLine($"Primary Offender: {group.PrimaryOffender}");
            sb.AppendLine($"Grouping Reason: {group.GroupingReason}");
            if (!string.IsNullOrEmpty(group.Metadata?.ResolutionNotes))
                sb.AppendLine($"Resolution Notes: {group.Metadata.ResolutionNotes}");
            sb.AppendLine();
            sb.AppendLine("Affected Elements:");
            foreach (var c in group.Clashes.Take(20))
                sb.AppendLine(
                    $"  {c.DisciplineA} ID:{c.ElementA?.Id.Value} vs " +
                    $"{c.DisciplineB} ID:{c.ElementB?.Id.Value} | Gap:{c.GapMM:F1}mm");
            return sb.ToString();
        }

        /// <summary>Remove characters that are illegal in XML text nodes.</summary>
        private static string SanitiseXmlContent(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                // XML 1.0 legal characters
                if (c == 0x9 || c == 0xA || c == 0xD ||
                    (c >= 0x20 && c <= 0xD7FF) ||
                    (c >= 0xE000 && c <= 0xFFFD))
                    sb.Append(c);
                // else skip illegal char
            }
            return sb.ToString();
        }

        /// <summary>Make a string safe for use in file names and XML attributes.</summary>
        private static string SanitiseName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "Project";
            // Remove path-illegal chars
            var invalid = Path.GetInvalidFileNameChars()
                .Union(new[] { '<', '>', '&', '"', '\'', ':' })
                .ToArray();
            var sb = new StringBuilder();
            foreach (char c in input)
                sb.Append(invalid.Contains(c) ? '_' : c);
            // Truncate to 60 chars — long project names cause path-too-long on some systems
            string result = sb.ToString();
            return result.Length > 60 ? result.Substring(0, 60) : result;
        }

        private static string GetIfcGuid(long revitId)
        {
            var bytes = BitConverter.GetBytes(revitId).Concat(new byte[8]).Take(16).ToArray();
            return ConvertToIfcGuid(new Guid(bytes));
        }

        private static readonly char[] IfcBase64Chars =
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$".ToCharArray();

        private static string ConvertToIfcGuid(Guid guid)
        {
            var   bytes = guid.ToByteArray();
            var   sb    = new StringBuilder(22);
            ulong n     = 0;
            for (int i = 0; i < 16; i++) n = n * 256 + bytes[i];
            for (int i = 21; i >= 0; i--)
            {
                sb.Insert(0, IfcBase64Chars[n % 64]);
                n /= 64;
            }
            return sb.ToString();
        }

        private static void WriteEntry(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    internal static class XDocumentExtensions
    {
        public static string ToStringWithDeclaration(this XDocument doc)
        {
            using var sb     = new StringWriter();
            using var writer = System.Xml.XmlWriter.Create(sb, new System.Xml.XmlWriterSettings
            {
                Indent             = true,
                Encoding           = Encoding.UTF8,
                OmitXmlDeclaration = false
            });
            doc.WriteTo(writer);
            writer.Flush();
            return sb.ToString();
        }
    }
}

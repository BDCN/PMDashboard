using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace PMDashboard.Services;

/// <summary>
/// Strips Excel constructs that ClosedXML's loader can throw on, but that a plain "read header
/// row + data rows as text" import never needs: Tables (and their structured-reference
/// formulas), pivot tables/caches, slicers/timelines, charts/drawings, the calc chain, defined
/// names, data validations, and any worksheet <c>&lt;extLst&gt;</c> extension block (sparklines,
/// rich data, etc. — all attached there).
///
/// Real-world forecast exports (built by other teams, not this app) have hit two concrete
/// ClosedXML failures on load: an ArgumentException resolving a Table's structured-reference
/// column ("The column 'X' does not appear in the source range"), and a bare
/// NotImplementedException from an unsupported feature (observed on a file with a pivot table).
/// Both happen while ClosedXML eagerly parses the whole workbook, before any of our own
/// header/row reading code runs — so the fix has to happen at the file level, not by changing
/// how we read cells.
///
/// This only ever subtracts parts/elements and removes the references that point at them
/// (relationships, content-type overrides, &lt;tableParts&gt;/&lt;drawing&gt; pointers); every
/// cell's actual value, shared string, and style — and therefore ClosedXML's formatted
/// GetString() output — is left byte-for-byte untouched.
/// </summary>
public static class XlsxSanitizer
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel  = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct   = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly string[] RemovedPrefixes =
    [
        "xl/tables/", "xl/pivotcache/", "xl/pivottables/", "xl/slicers/",
        "xl/slicercaches/", "xl/timelines/", "xl/charts/", "xl/drawings/",
    ];

    private static bool IsRemovedPart(string path) =>
        string.Equals(path, "xl/calcChain.xml", StringComparison.OrdinalIgnoreCase) ||
        RemovedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static byte[] StripUnsupportedFeatures(string filePath)
    {
        using var input  = File.OpenRead(filePath);
        using var srcZip = new ZipArchive(input, ZipArchiveMode.Read);

        using var outStream = new MemoryStream();
        using (var destZip = new ZipArchive(outStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in srcZip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.Length == 0 || IsRemovedPart(path)) continue; // directory entry or dropped part

                byte[] bytes;
                using (var es = entry.Open())
                using (var ms = new MemoryStream())
                {
                    es.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                if (string.Equals(path, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                    bytes = StripContentTypes(bytes);
                else if (string.Equals(path, "xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
                    bytes = StripWorkbook(bytes);
                else if (string.Equals(path, "xl/_rels/workbook.xml.rels", StringComparison.OrdinalIgnoreCase))
                    bytes = StripRels(bytes, "xl/");
                else if (path.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                         && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                         && !path.Contains("_rels/", StringComparison.OrdinalIgnoreCase))
                    bytes = StripWorksheet(bytes);
                else if (path.StartsWith("xl/worksheets/_rels/", StringComparison.OrdinalIgnoreCase))
                    bytes = StripRels(bytes, "xl/worksheets/");

                var destEntry = destZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var destEntryStream = destEntry.Open();
                destEntryStream.Write(bytes, 0, bytes.Length);
            }
        }
        return outStream.ToArray();
    }

    private static byte[] StripContentTypes(byte[] xml)
    {
        var doc  = Parse(xml);
        var root = doc.Root!;
        root.Elements(Ct + "Override")
            .Where(e => IsRemovedPart((e.Attribute("PartName")?.Value ?? "").TrimStart('/')))
            .ToList()
            .ForEach(e => e.Remove());
        return Serialize(doc);
    }

    private static byte[] StripWorkbook(byte[] xml)
    {
        var doc  = Parse(xml);
        var root = doc.Root!;
        root.Elements(Main + "definedNames").Remove();
        root.Elements(Main + "pivotCaches").Remove();
        return Serialize(doc);
    }

    private static byte[] StripWorksheet(byte[] xml)
    {
        var doc  = Parse(xml);
        var root = doc.Root!;
        root.Elements(Main + "tableParts").Remove();
        root.Elements(Main + "dataValidations").Remove();
        root.Elements(Main + "drawing").Remove();
        root.Elements(Main + "extLst").Remove();
        // Drop formulas, keep each cell's cached <v>/<is> — we only ever read displayed text,
        // never recalculate, so the formula itself is dead weight that can reference a Table or
        // name we've just removed.
        root.Descendants(Main + "f").Remove();
        return Serialize(doc);
    }

    private static byte[] StripRels(byte[] xml, string baseFolder)
    {
        var doc  = Parse(xml);
        var root = doc.Root!;
        root.Elements(Rel + "Relationship")
            .Where(e =>
            {
                var target = e.Attribute("Target")?.Value ?? "";
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false; // external
                return IsRemovedPart(ResolvePath(baseFolder, target));
            })
            .ToList()
            .ForEach(e => e.Remove());
        return Serialize(doc);
    }

    /// <summary>Resolves a .rels Target — which OOXML permits as either absolute ("/xl/tables/
    /// table1.xml") or relative to baseFolder, possibly with "../" segments — into a normalized
    /// zip-entry-style path for comparison against IsRemovedPart.</summary>
    private static string ResolvePath(string baseFolder, string target)
    {
        var combined = target.StartsWith('/') ? target : baseFolder.TrimEnd('/') + "/" + target;
        var stack = new List<string>();
        foreach (var part in combined.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(part);
        }
        return string.Join("/", stack);
    }

    private static XDocument Parse(byte[] xml)
    {
        using var ms = new MemoryStream(xml);
        return XDocument.Load(ms);
    }

    private static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(false)))
            doc.Save(writer, SaveOptions.DisableFormatting);
        return ms.ToArray();
    }
}

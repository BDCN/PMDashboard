using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PMDashboard.Services;

/// <summary>
/// Writes a PDF of images, one per page — enough for exporting a rendered page (the Org Chart)
/// without adding a PDF library. Images are stored losslessly (RGB, Flate/zlib), so text stays
/// sharp. Each page is a fixed size (e.g. Letter landscape) with an optional heading in plain
/// Helvetica text; the image is scaled to fit the rest of the page, keeping its aspect ratio.
/// </summary>
public static class ImagePdfWriter
{
    public record Page(BitmapSource Image, string Heading);

    /// <summary>US Letter, landscape (11" × 8.5"), in points.</summary>
    public const double LetterLandscapeW = 792, LetterLandscapeH = 612;

    public static void Write(string path, IReadOnlyList<Page> pages, double pageW = LetterLandscapeW,
                             double pageH = LetterLandscapeH, double margin = 24, string title = "")
    {
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        using var fs = File.Create(path);
        var offsets = new List<long>();
        void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
        void Obj(string body) { offsets.Add(fs.Position); Ascii($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }

        // Objects: 1 catalog, 2 page tree, 3 font, then 3 per page (page, content, image), then info.
        int PageObj(int i) => 4 + i * 3;
        Ascii("%PDF-1.4\n");
        fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);   // binary-content marker line
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{PageObj(i)} 0 R"))}] /Count {pages.Count} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (int i = 0; i < pages.Count; i++)
        {
            var (image, heading) = (pages[i].Image, Safe(pages[i].Heading));

            // 24-bit RGB rows, top to bottom — what PDF's DeviceRGB / 8 bits per component expects.
            var rgb = new FormatConvertedBitmap(image, PixelFormats.Rgb24, null, 0);
            int w = rgb.PixelWidth, h = rgb.PixelHeight, stride = w * 3;
            var pixels = new byte[stride * h];
            rgb.CopyPixels(pixels, stride, 0);
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(pixels, 0, pixels.Length);
                compressed = ms.ToArray();
            }

            // Heading along the top; the image fills what's left, centred across, top-aligned.
            double headH = heading.Length > 0 ? 26 : 0;
            double boxW = pageW - 2 * margin, boxH = pageH - 2 * margin - headH;
            double s = Math.Min(boxW / w, boxH / h);
            double imgW = w * s, imgH = h * s;
            double x = margin + (boxW - imgW) / 2, y = pageH - margin - headH - imgH;

            var content = new StringBuilder();
            if (heading.Length > 0)
                content.Append($"BT /F1 14 Tf 0.06 0.09 0.16 rg {F(margin)} {F(pageH - margin - 14)} Td ({heading}) Tj ET\n");
            content.Append($"q {F(imgW)} 0 0 {F(imgH)} {F(x)} {F(y)} cm /Im0 Do Q");
            var c = content.ToString();

            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageW)} {F(pageH)}] " +
                $"/Resources << /Font << /F1 3 0 R >> /XObject << /Im0 {PageObj(i) + 2} 0 R >> >> /Contents {PageObj(i) + 1} 0 R >>");
            Obj($"<< /Length {c.Length} >>\nstream\n{c}\nendstream");
            offsets.Add(fs.Position);
            Ascii($"{offsets.Count} 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB " +
                  $"/BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
            fs.Write(compressed, 0, compressed.Length);
            Ascii("\nendstream\nendobj\n");
        }

        // Document title (shown by PDF viewers).
        Obj($"<< /Title ({Safe(title)}) /Producer (SAT) >>");

        long xref = fs.Position;
        Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Ascii($"{o:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info {offsets.Count} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    // ASCII-only, no characters that need escaping in a PDF string literal.
    private static string Safe(string s) =>
        new(s.Replace("—", "-").Replace("·", "-").Where(c => c >= 32 && c < 127 && c != '(' && c != ')' && c != '\\').ToArray());
}

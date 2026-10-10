using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PMDashboard.Services;

/// <summary>
/// Writes a one-page PDF containing a single image — enough for exporting a rendered page (the
/// Org Chart) without adding a PDF library. The image is stored losslessly (RGB, Flate/zlib), so
/// text stays sharp. The page is sized to the image's aspect ratio.
/// </summary>
public static class ImagePdfWriter
{
    /// <param name="pageWidthPt">Page width in points (1/72"); height follows the image's aspect ratio.</param>
    public static void Write(string path, BitmapSource image, double pageWidthPt, string title = "")
    {
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

        // PDF pages are limited to 14,400 pt on a side.
        double pageW = Math.Min(pageWidthPt, 14400), pageH = pageW * h / w;
        if (pageH > 14400) { pageW *= 14400 / pageH; pageH = 14400; }
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        using var fs = File.Create(path);
        var offsets = new List<long>();
        void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
        void Obj(string body) { offsets.Add(fs.Position); Ascii($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }

        Ascii("%PDF-1.4\n");
        fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);   // binary-content marker line
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageW)} {F(pageH)}] " +
            "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>");
        var content = $"q {F(pageW)} 0 0 {F(pageH)} 0 0 cm /Im0 Do Q";
        Obj($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        offsets.Add(fs.Position);
        Ascii($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB " +
              $"/BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
        fs.Write(compressed, 0, compressed.Length);
        Ascii("\nendstream\nendobj\n");
        // Document title (shown by PDF viewers); ASCII-only to keep the string literal simple.
        var safeTitle = new string(title.Where(c => c >= 32 && c < 127 && c != '(' && c != ')' && c != '\\').ToArray());
        Obj($"<< /Title ({safeTitle}) /Producer (SAT) >>");

        long xref = fs.Position;
        Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Ascii($"{o:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info {offsets.Count} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}

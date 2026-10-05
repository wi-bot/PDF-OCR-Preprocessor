using System.Globalization;
using System.Text;

namespace PdfOcrPreprocessor.Tests;

internal static class SyntheticPdf
{
    public static string Write(string directory, int rotation = 0, double unit = 1, bool crop = false, bool invisible = false,
        string text = "SYNTHETIC $1,985,420.00 00123 -45.60 03/04/2006")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"synthetic-{Guid.NewGuid():N}.pdf");
        var content = $"BT /F1 12 Tf {(invisible ? 3 : 0)} Tr 40 60 Td ({text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")}) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] {(crop ? "/CropBox [20 30 380 180]" : "")} /Rotate {rotation} /UserUnit {unit.ToString(CultureInfo.InvariantCulture)} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"
        };
        using var output = File.Create(path);
        void WriteText(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        WriteText("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(output.Position);
            WriteText($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var crossReference = output.Position;
        WriteText($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) WriteText($"{offset:0000000000} 00000 n \n");
        WriteText($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{crossReference}\n%%EOF\n");
        return path;
    }
}
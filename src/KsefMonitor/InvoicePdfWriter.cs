using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KsefMonitor;

internal sealed record InvoicePageImage(byte[] Jpeg, int Width, int Height);

// Prosty PDF 1.4: każda strona zawiera obraz wyrenderowanej strony WPF.
// Ten sam układ A4 jest używany na ekranie, w druku i w pliku PDF.
internal static class InvoicePdfWriter
{
    public static void Write(Stream destination, IReadOnlyList<InvoicePageImage> pages)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanSeek || pages.Count == 0) throw new ArgumentException("Brak stron lub strumienia z pozycjonowaniem.");
        using var writer = new BinaryWriter(destination, Encoding.Latin1, leaveOpen: true);
        void Text(string value) => writer.Write(Encoding.Latin1.GetBytes(value));
        var offsets = new List<long> { 0 };
        void Object(int number, string value)
        {
            offsets.Add(destination.Position);
            Text($"{number} 0 obj\n{value}\nendobj\n");
        }
        Text("%PDF-1.4\n%âãÏÓ\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = new StringBuilder();
        for (var index = 0; index < pages.Count; index++) kids.Append(CultureInfo.InvariantCulture, $"{3 + index * 3} 0 R ");
        Object(2, FormattableString.Invariant($"<< /Type /Pages /Count {pages.Count} /Kids [ {kids} ] >>"));
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            if (page.Width <= 0 || page.Height <= 0 || page.Jpeg.Length < 4 || page.Jpeg[0] != 255 || page.Jpeg[1] != 216)
                throw new InvalidDataException("Niepoprawny obraz strony PDF.");
            var number = 3 + index * 3;
            Object(number, FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /XObject << /Im0 {number + 1} 0 R >> >> /Contents {number + 2} 0 R >>"));
            offsets.Add(destination.Position);
            Text(FormattableString.Invariant($"{number + 1} 0 obj\n<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n"));
            writer.Write(page.Jpeg);
            Text("\nendstream\nendobj\n");
            const string drawing = "q\n595.28 0 0 841.89 0 0 cm\n/Im0 Do\nQ\n";
            Object(number + 2, FormattableString.Invariant($"<< /Length {drawing.Length} >>\nstream\n{drawing}endstream"));
        }
        var xref = destination.Position;
        Text(FormattableString.Invariant($"xref\n0 {offsets.Count}\n0000000000 65535 f \n"));
        for (var index = 1; index < offsets.Count; index++) Text($"{offsets[index].ToString("0000000000", CultureInfo.InvariantCulture)} 00000 n \n");
        Text(FormattableString.Invariant($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        writer.Flush();
    }
}

// Utility to regenerate MAN-P102-SCOPE-001.pdf with corrected scope boundary language
// Usage: dotnet script generate_p102_pdf.cs
// Or compile and run as a standalone utility

using iTextSharp.text;
using iTextSharp.text.pdf;
using System;
using System.IO;

class PdfGenerator
{
    static void Main()
    {
        var outputPath = "data/manuals/MAN-P102-SCOPE-001.pdf";

        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        using (var fs = new FileStream(outputPath, FileMode.Create))
        using (var document = new Document(PageSize.LETTER, 36, 36, 36, 36))
        using (var writer = PdfWriter.GetInstance(document, fs))
        {
            document.Open();

            // Page 1
            var titleFont = new Font(Font.FontFamily.HELVETICA, 11, Font.NORMAL);
            var bodyFont = new Font(Font.FontFamily.HELVETICA, 11, Font.NORMAL);
            var sectionFont = new Font(Font.FontFamily.HELVETICA, 11, Font.BOLD);

            // Header
            document.Add(new Paragraph("Synthetic maintenance-planning reference; not an ABB document.", bodyFont));
            document.Add(new Paragraph("Document ID: MAN-P102-SCOPE-001", bodyFont));
            document.Add(new Paragraph("Version: 1.0", bodyFont));
            document.Add(new Paragraph("Asset scope: P-102 (fictional CP-100 pump).", bodyFont));
            document.Add(new Paragraph("\n"));

            // Section 1
            document.Add(new Paragraph("Section: Inspection-only scope", sectionFont));
            document.Add(new Paragraph("\n"));
            document.Add(new Paragraph(
                "This planning scope covers inspection only for P-102. Do not substitute this scope for the P-101 bearing-replacement scope.",
                bodyFont));
            document.Add(new Paragraph("Planning estimate: 90 minutes; crew: one MECHANICAL technician.", bodyFont));
            document.Add(new Paragraph("\n\n\n\n\n\n\n\n\n\n"));

            // Page break
            document.NewPage();

            // Page 2 header
            document.Add(new Paragraph("Document ID: MAN-P102-SCOPE-001", bodyFont));
            document.Add(new Paragraph("Version: 1.0", bodyFont));
            document.Add(new Paragraph("\n"));

            // Section 2
            document.Add(new Paragraph("Section: Scope limitation", sectionFont));
            document.Add(new Paragraph("\n"));
            document.Add(new Paragraph("This inspection-only scope applies to P-102.", bodyFont));
            document.Add(new Paragraph("Do not substitute this scope for the P-101 bearing-replacement scope.", bodyFont));
            document.Add(new Paragraph("\n"));
            document.Add(new Paragraph(
                "This synthetic reference contains planning assumptions only, not repair procedures.",
                bodyFont));

            document.Close();
        }

        Console.WriteLine($"PDF generated successfully: {Path.GetFullPath(outputPath)}");
    }
}

using System;
using System.IO;
using System.Text.Json;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: TestPdfParser <pdf_path>");
            return;
        }

        string filePath = args[0];
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return;
        }

        try
        {
            using (var doc = UglyToad.PdfPig.PdfDocument.Open(filePath))
            {
                 var allText = string.Join("\n", doc.GetPages().Select(p => 
                     UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(p)));
                 Console.WriteLine("--- RAW TEXT START ---");
                 Console.WriteLine(allText);
                 Console.WriteLine("--- RAW TEXT END ---");
            }

            // Escape the path for spaces
            var result = PdfParser.Parse(filePath);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}

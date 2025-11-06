using System.Globalization;
using System.Text;
using System.Linq;
using netDxf;
using netDxf.Entities;
using netDxf.IO;

namespace DxfInspector;

internal static class Program
{
    public static int Main(string[] args)
    {
        var files = args.Length > 0 ? args : GetDefaultFiles();

        foreach (var file in files)
        {
            try
            {
                var fullPath = Path.GetFullPath(file);
                if (!File.Exists(fullPath))
                {
                    Console.WriteLine($"[WARN] File not found: {file}");
                    continue;
                }

                Console.WriteLine(new string('=', 80));
                Console.WriteLine($"DXF: {fullPath}");
                Console.WriteLine(new string('-', 80));

                var doc = LoadDocumentWithFallback(fullPath);
                if (doc is null)
                {
                    Console.WriteLine($"[WARN] Unable to load document: {file}");
                    continue;
                }

                PrintLayers(doc);
                PrintBlocks(doc);
                PrintInserts(doc);
                PrintTextualEntities(doc);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to process {file}: {ex}");
            }
        }

        return 0;
    }

    private static DxfDocument? LoadDocumentWithFallback(string path)
    {
        try
        {
            return DxfDocument.Load(path);
        }
        catch (DxfVersionNotSupportedException ex) when (TryLoadAfterHeaderUpgrade(path, out var upgraded))
        {
            Console.WriteLine($"[INFO] {ex.Message} — retried with AC1015 header.");
            return upgraded;
        }
        catch (DxfVersionNotSupportedException ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            return null;
        }
    }

    private static bool TryLoadAfterHeaderUpgrade(string path, out DxfDocument? document)
    {
        document = null;

        var bytes = File.ReadAllBytes(path);
        var marker = Encoding.ASCII.GetBytes("AC1014");
        var replacement = Encoding.ASCII.GetBytes("AC1015");
        var index = IndexOf(bytes, marker);
        if (index < 0)
        {
            return false;
        }

        Array.Copy(replacement, 0, bytes, index, replacement.Length);
        try
        {
            using var stream = new MemoryStream(bytes);
            document = DxfDocument.Load(stream);
            return true;
        }
        catch
        {
            document = null;
            return false;
        }
    }

    private static int IndexOf(byte[] source, byte[] pattern)
    {
        for (var i = 0; i <= source.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static string[] GetDefaultFiles()
    {
        var current = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(current, "NR119812.dxf"),
            Path.Combine(current, "NR 120184.dxf"),
            Path.Combine(current, "..", "NR119812.dxf"),
            Path.Combine(current, "..", "NR 120184.dxf"),
            Path.Combine(current, "..", "..", "NR119812.dxf"),
            Path.Combine(current, "..", "..", "NR 120184.dxf")
        };

        return candidates.Where(File.Exists).Distinct().ToArray();
    }

    private static void PrintLayers(DxfDocument doc)
    {
        Console.WriteLine("Layers");
        foreach (var layer in doc.Layers.OrderBy(l => l.Name))
        {
            var colorInfo = $"{layer.Color.Index} ({layer.Color})";
            var linetype = layer.Linetype?.Name ?? "ByLayer";
            var desc = string.IsNullOrWhiteSpace(layer.Description) ? string.Empty : $" desc=\"{layer.Description}\"";
            Console.WriteLine($" - {layer.Name} | color={colorInfo} | linetype={linetype}{desc}");
        }
        Console.WriteLine();
    }

    private static void PrintBlocks(DxfDocument doc)
    {
        Console.WriteLine("Blocks");
        foreach (var block in doc.Blocks.OrderBy(b => b.Name))
        {
            var entitySummary = block.Entities
                .GroupBy(e => e.Type)
                .Select(g => $"{g.Key}:{g.Count()}")
                .DefaultIfEmpty("none")
                .Aggregate((a, b) => $"{a}, {b}");

            var attrSummary = block.AttributeDefinitions.Any()
                ? string.Join(", ", block.AttributeDefinitions.Values
                    .Select(a => $"{a.Tag} (prompt=\"{a.Prompt}\")"))
                : "none";

            Console.WriteLine($" - {block.Name} | entities={entitySummary} | attributes={attrSummary}");
        }
        Console.WriteLine();
    }

    private static void PrintInserts(DxfDocument doc)
    {
        Console.WriteLine("Inserts");
        var inserts = doc.Entities.Inserts
            .OrderBy(i => i.Block.Name)
            .ThenBy(i => i.Layer?.Name);

        foreach (var insert in inserts)
        {
            var layer = insert.Layer?.Name ?? "(no layer)";
            var blockName = insert.Block.Name;
            var scale = $"({insert.Scale.X.ToString("0.###", CultureInfo.InvariantCulture)}, {insert.Scale.Y.ToString("0.###", CultureInfo.InvariantCulture)}, {insert.Scale.Z.ToString("0.###", CultureInfo.InvariantCulture)})";
            var pos = $"({insert.Position.X.ToString("0.###", CultureInfo.InvariantCulture)}, {insert.Position.Y.ToString("0.###", CultureInfo.InvariantCulture)}, {insert.Position.Z.ToString("0.###", CultureInfo.InvariantCulture)})";
            var rotation = insert.Rotation.ToString("0.###", CultureInfo.InvariantCulture);
            var attrSummary = insert.Attributes.Any()
                ? string.Join(", ", insert.Attributes.Select(a => $"{a.Tag}={a.Value}"))
                : "none";

            Console.WriteLine($" - block={blockName} | layer={layer} | pos={pos} | scale={scale} | rotation={rotation} | attributes=[{attrSummary}]");
        }
        Console.WriteLine();
    }

    private static void PrintTextualEntities(DxfDocument doc)
    {
        Console.WriteLine("Textual Entities");

        var texts = doc.Entities.Texts
            .OrderBy(t => t.Layer?.Name)
            .ThenBy(t => t.Value);
        foreach (var text in texts)
        {
            var layer = text.Layer?.Name ?? "(no layer)";
            Console.WriteLine($" - TEXT \"{text.Value}\" | layer={layer} | pos=({text.Position.X:0.###},{text.Position.Y:0.###})");
        }

        var mtexts = doc.Entities.MTexts
            .OrderBy(t => t.Layer?.Name)
            .ThenBy(t => t.Value);
        foreach (var mtext in mtexts)
        {
            var layer = mtext.Layer?.Name ?? "(no layer)";
            Console.WriteLine($" - MTEXT \"{mtext.Value}\" | layer={layer} | pos=({mtext.Position.X:0.###},{mtext.Position.Y:0.###})");
        }

        Console.WriteLine();
    }
}

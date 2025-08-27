using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace FileMonitor
{
    public static class PdfParser
    {
        // extrai do nome: "Ordem de Produção nº 514568.pdf"
        private static readonly Regex NumeroOpFromNameRegex = new(
            @"(?i)\bOrdem\s*de\s*Produ[cç][aã]o\s*n[ºo\.]?\s*(\d{4,})\b",
            RegexOptions.Compiled
        );

        public sealed record ParsedOp(
            string NumeroOp,
            string? CodigoProduto,
            string? DescricaoProduto,
            string? Cliente,
            string? DataOpIso,   // yyyy-MM-dd
            List<string> Materiais,
            bool Emborrachada
        );

        public static ParsedOp Parse(string pdfPath)
        {
            using var doc = PdfDocument.Open(pdfPath);

            // texto em ordem de leitura
            var allText = string.Join("\n", doc.GetPages().Select(p =>
                ContentOrderTextExtractor.GetText(p)));

            // 1) tenta pelo nome do arquivo
            var fileBase = Path.GetFileNameWithoutExtension(pdfPath);
            var mName = NumeroOpFromNameRegex.Match(fileBase);
            string numero = mName.Success ? mName.Groups[1].Value.Trim() : string.Empty;

            // 2) fallback: tenta dentro do PDF
            if (string.IsNullOrWhiteSpace(numero))
            {
                string Grab(string pattern)
                    => Regex.Match(allText, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                            .Groups[1].Value.Trim();

                numero = Grab(@"(?:OP|NR|ORDEM\s*DE\s*PRODU(?:Ç|C)ÃO)\s*[:\-]?\s*([0-9]{4,})");
                if (string.IsNullOrWhiteSpace(numero))
                    numero = fileBase; // último fallback
            }

            // demais campos (ajuste conforme layout real)
            string Grab1(string pattern)
                => Regex.Match(allText, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                        .Groups[1].Value.Trim();

            var codigo  = Grab1(@"C(?:ó|o)digo(?:\s*do\s*Produto)?\s*[:\-]\s*([A-Z0-9\.\-]+)");
            var descr   = Grab1(@"Descri[cç][aã]o\s*[:\-]\s*(.+)");
            var cliente = Grab1(@"Cliente\s*[:\-]\s*(.+)");
            var data    = Grab1(@"Data\s*[:\-]\s*([0-9]{2}[/\-][0-9]{2}[/\-][0-9]{2,4})");

            // bloco "Matéria-prima"
            var matBlock = Regex.Match(allText,
                @"Mat[ée]ria[-\s]*prima(.*?)(?:\n\s*\n|Observa[cç][aã]o|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;

            var materiais = Regex.Matches(matBlock, @"[^\r\n]+")
                .Select(m => m.Value.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            var emborrachada = materiais.Any(m =>
                m.Contains("BORRACHA", StringComparison.OrdinalIgnoreCase));

            static string? ToIso(string s) => DateTime.TryParse(s, out var d) ? d.ToString("yyyy-MM-dd") : null;

            return new ParsedOp(
                NumeroOp: numero,
                CodigoProduto: string.IsNullOrWhiteSpace(codigo) ? null : codigo,
                DescricaoProduto: string.IsNullOrWhiteSpace(descr) ? null : descr,
                Cliente: string.IsNullOrWhiteSpace(cliente) ? null : cliente,
                DataOpIso: string.IsNullOrWhiteSpace(data) ? null : ToIso(data),
                Materiais: materiais,
                Emborrachada: emborrachada
            );
        }
    }
}


using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

public static class PdfParser
{
    // continua útil, mas vamos reforçar com "só dígitos":
    private static readonly Regex NumeroOpFromNameRegex = new(
        @"(?i)\bOrdem\s*de\s*Produ[cç][aã]o\s*n[ºo\.]?\s*(\d{4,})\b",
        RegexOptions.Compiled
    );

    // pega "a ÚLTIMA sequência de 4+ dígitos" (evita confundir com CEP, CNPJ etc.)
    private static readonly Regex LastDigitsRegex = new(@"\b(\d{4,})\b", RegexOptions.Compiled);

    public sealed record ParsedOp(
        string NumeroOp,
        string? CodigoProduto,
        string? DescricaoProduto,
        string? Cliente,
        string? DataOpIso,
        List<string> Materiais,
        bool Emborrachada
    );

    public static ParsedOp Parse(string pdfPath)
    {
        using var doc = PdfDocument.Open(pdfPath);

        var allText = string.Join("\n", doc.GetPages().Select(p => 
            ContentOrderTextExtractor.GetText(p)));

        // 0) Helper para normalizar/remover acentos
        static string ToAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var nfkd = s.Normalize(NormalizationForm.FormD); // decompor
            var sb = new StringBuilder(nfkd.Length);
            foreach (var ch in nfkd)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        string fileBase = Path.GetFileNameWithoutExtension(pdfPath);
        string fileBaseAscii = ToAscii(fileBase).Replace("º", "o"); // casos típicos "nº"

        // 1) tenta pelo nome (padrão textual)
        var mName = NumeroOpFromNameRegex.Match(fileBaseAscii);
        string numero = mName.Success ? mName.Groups[1].Value.Trim() : string.Empty;

        // 2) fallback pelo CONTEÚDO (procura OP / NR / ORDEM DE PRODUCAO : ####)
        if (string.IsNullOrWhiteSpace(numero))
        {
            string Grab(string pattern) =>
                Regex.Match(ToAscii(allText), pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                     .Groups[1].Value.Trim();

            numero = Grab(@"(?:OP|NR|ORDEM\s*DE\s*PRODUCAO)\s*[:\-]?\s*([0-9]{4,})");
        }

        // 3) fallback final: extrair "a última sequência de dígitos" do NOME do arquivo
        if (string.IsNullOrWhiteSpace(numero))
        {
            var mm = LastDigitsRegex.Matches(fileBaseAscii);
            if (mm.Count > 0) numero = mm[^1].Groups[1].Value; // última ocorrência
        }

        // 4) como ÚLTIMO reduto: extrair do TEXTO “a última sequência de dígitos”
        if (string.IsNullOrWhiteSpace(numero))
        {
            var mm = LastDigitsRegex.Matches(ToAscii(allText));
            if (mm.Count > 0) numero = mm[^1].Groups[1].Value;
        }

        // se ainda assim vier vazio, zera (deixa claro que faltou número)
        if (string.IsNullOrWhiteSpace(numero))
            numero = "DESCONHECIDO";

        // --- Demais campos (mantidos) ---
        string Grab1(string pattern) =>
            Regex.Match(allText, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                 .Groups[1].Value.Trim();

        var codigo  = Grab1(@"C(?:ó|o)digo(?:\s*do\s*Produto)?\s*[:\-]\s*([A-Z0-9\.\-]+)");
        var descr   = Grab1(@"Descri[cç][aã]o\s*[:\-]\s*(.+)");
        var cliente = Grab1(@"Cliente\s*[:\-]\s*(.+)");
        var data    = Grab1(@"Data\s*[:\-]\s*([0-9]{2}[/\-][0-9]{2}[/\-][0-9]{2,4})");

        var matBlock = Regex.Match(allText,
            @"Mat[ée]ria[-\s]*prima(.*?)(?:\n\s*\n|Observa[cç][aã]o|$)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value;

        var materiais = Regex.Matches(matBlock, @"[^\r\n]+")
            .Select(m => m.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var emborrachada = materiais.Any(m => m.Contains("BORRACHA", StringComparison.OrdinalIgnoreCase));

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


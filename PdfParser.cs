using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

public static class PdfParser
{
    private static readonly Regex NumeroOpFromNameRegex = new(
        @"(?i)\bOrdem\s*de\s*Produ[cç][aã]o\s*n[ºo\.]?\s*(\d{4,})\b",
        RegexOptions.Compiled
    );

    private static readonly Regex LastDigitsRegex = new(@"\b(\d{4,})\b", RegexOptions.Compiled);

    private static readonly Regex MateriaPrimaBlockRegex = new(
        @"Mat[ée]ria[\-\s]*prima(.*?)(?:\n\s*\n|Observa[cç][aã]o|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex BorrachaLooseRegex = new(
        @"B\s*O\s*R\s*R\s*A\s*C\s*H\s*A",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly CultureInfo PtBr = new("pt-BR");

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

        // --- helpers ---
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

        static string NormalizeSpaces(string s) =>
            Regex.Replace(s ?? string.Empty, @"[ \t\r\n]+", " ").Trim();

        static string CollapseSpacesAndHyphens(string s) =>
            Regex.Replace(s ?? string.Empty, @"[\s\-]+", string.Empty);

        static string PrepareSearchText(string s)
        {
            var ascii = ToAscii(s).ToUpperInvariant();
            return NormalizeSpaces(ascii);
        }

        string fileBase = Path.GetFileNameWithoutExtension(pdfPath);
        string fileBaseAscii = ToAscii(fileBase).Replace("º", "o"); // casos típicos "nº"

        var mName = NumeroOpFromNameRegex.Match(fileBaseAscii);
        string numero = mName.Success ? mName.Groups[1].Value.Trim() : string.Empty;

        if (string.IsNullOrWhiteSpace(numero))
        {
            string Grab(string pattern) =>
                Regex.Match(ToAscii(allText), pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                     .Groups[1].Value.Trim();

            numero = Grab(@"(?:OP|NR|ORDEM\s*DE\s*PRODUCAO)\s*[:\-]?\s*([0-9]{4,})");
        }

        if (string.IsNullOrWhiteSpace(numero))
        {
            var mm = LastDigitsRegex.Matches(fileBaseAscii);
            if (mm.Count > 0) numero = mm[^1].Groups[1].Value; // última ocorrência
        }

        if (string.IsNullOrWhiteSpace(numero))
        {
            var mm = LastDigitsRegex.Matches(ToAscii(allText));
            if (mm.Count > 0) numero = mm[^1].Groups[1].Value;
        }

        if (string.IsNullOrWhiteSpace(numero))
            numero = "DESCONHECIDO";

        // --- Demais campos (refinados) ---
        string Grab1(string pattern) =>
            Regex.Match(allText, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                 .Groups[1].Value.Trim();

        var codigo  = Grab1(@"C(?:ó|o)digo(?:\s*do\s*Produto)?\s*[:\-]\s*([A-Z0-9\.\-]+)");
        var descr   = Grab1(@"Descri[cç][aã]o\s*[:\-]\s*(.+)");
        var cliente = Grab1(@"Cliente\s*[:\-]\s*(.+)");

        // datas dd/MM/yyyy, dd-MM-yyyy, dd/MM/yy etc.
        var dataRaw = Grab1(@"Data\s*[:\-]\s*([0-9]{2}[/\-][0-9]{2}[/\-][0-9]{2,4})");
        var dataIso = ToIso(dataRaw);

        // bloco de materia-prima (com variações)
        var matBlock = MateriaPrimaBlockRegex.Match(allText).Groups[1].Value;

        var materiais = Regex.Matches(matBlock, @"[^\r\n]+")
            .Select(m => m.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // detecção primária: linha que contenha 'BORRACHA'
        bool emborrachada = materiais.Any(m => m.Contains("BORRACHA", StringComparison.OrdinalIgnoreCase));

        // --- Fallbacks robustos ---
        if (!emborrachada)
        {
            var search = PrepareSearchText(allText); // sem acentos/upper e espaços colapsados
            var collapsed = CollapseSpacesAndHyphens(search); // remove espaços e hífens

            // 1) BORRACHA "colada" (cobre BOR- RACHA, BOR - RACHA etc.)
            if (collapsed.Contains("BORRACHA"))
                emborrachada = true;

            // 2) regex tolerante (letra a letra com espaços)
            if (!emborrachada && BorrachaLooseRegex.IsMatch(search))
                emborrachada = true;

            // 3) sinônimos comuns
            if (!emborrachada &&
                (search.Contains("EMBORRACHAD") || Regex.IsMatch(search, @"REVESTI(?:MENTO)?\s+DE\s+BORRACHA", RegexOptions.IgnoreCase)))
                emborrachada = true;

            // 4) se o bloco veio vazio mas o texto geral contém “borracha”, registre para diagnóstico
            if (materiais.Count == 0 && emborrachada)
            {
                Console.WriteLine($"[PdfParser] Materiais vazio, mas fallback achou BORRACHA (OP={numero})");
            }
        }

        return new ParsedOp(
            NumeroOp: numero,
            CodigoProduto: string.IsNullOrWhiteSpace(codigo) ? null : codigo,
            DescricaoProduto: string.IsNullOrWhiteSpace(descr) ? null : descr,
            Cliente: string.IsNullOrWhiteSpace(cliente) ? null : cliente,
            DataOpIso: dataIso,
            Materiais: materiais,
            Emborrachada: emborrachada
        );

        // ---- locals ----
        static string? ToIso(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;

            var formats = new[]
            {
                "dd/MM/yyyy","dd-MM-yyyy","dd.MM.yyyy",
                "dd/MM/yy","dd-MM-yy","dd.MM.yy"
            };

            if (DateTime.TryParseExact(s.Trim(), formats, PtBr, DateTimeStyles.None, out var d))
                return d.ToString("yyyy-MM-dd");

            if (DateTime.TryParse(s, PtBr, DateTimeStyles.None, out d))
                return d.ToString("yyyy-MM-dd");

            return null;
        }
    }
}


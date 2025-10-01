using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
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

    private static readonly Regex DestacadorRegex = new Regex(
        @"\bDESTACADOR\b\s*[:\-]?\s*([MFmf](?:\s*/\s*[MFmf])?)\b|\b([MFmf](?:\s*/\s*[MFmf]))\s*DESTACADOR\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex ModalidadeEntregaRegex = new Regex(
        @"\b(RETIRADA|A\s*ENTREGAR)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    // Aceita: dd/MM[/yyyy] ou yyyy/MM/dd, com rótulos variados (inclui 'ENTREGA REQUERIDA')
    private static readonly Regex DataEntregaRegex = new Regex(
        @"\b(?:DATA\s*ENTREGA|ENTREGA(?:\s*REQUERIDA)?)\b\s*[:\-]?\s*(\d{1,2}[\/\-]\d{1,2}(?:[\/\-]\d{2,4})?|\d{4}[\/\-]\d{1,2}[\/\-]\d{1,2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex HoraEntregaRegex = new Regex(
        @"\b(?:HORA\s*ENTREGA|HOR[AÁ]RIO|HORA|ENTREGA\s*REQUERIDA)\b\s*[:\-]?\s*(\d{1,2}:\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex EntregaRequeridaInlineRegex = new Regex(
        @"\bENTREGA\s*REQUERIDA\b\s*[:\-]?\s*(\d{1,2}[\/\-]\d{1,2})(?:\s+(\d{1,2}:\d{2}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex FallbackEntregaDateRegex = new Regex(
        @"\b(\d{1,2}[\/\-]\d{1,2}(?:[\/\-]\d{2,4})?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex FallbackEntregaTimeColonRegex = new Regex(
        @"\b(\d{1,2}):(\d{2})\b",
        RegexOptions.Compiled
    );
    private static readonly Regex FallbackEntregaTimeWithLabelRegex = new Regex(
        @"\b(\d{1,2})\s*(?:HORAS?|HRS?|HS|H)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex ObservacaoBlockRegex = new Regex(
        @"(?im)^\s*Observa[cç][aã]o(?:es)?\s*[:\-]?\s*(.+?)(?=^\s*[A-Z][^\r\n]*:|^\s*\w+:|$)",
        RegexOptions.Compiled | RegexOptions.Singleline
    );

    private static readonly Regex ObservacaoStopRegex = new Regex(
        @"(?im)^\s*(?:Mat[eé]ria[\s\-]*prima|Data\s+Emiss[aã]o|Data\s+Entrega|Etapa\s*/?\s*Eventos|Operador|Assinatura\s+Cliente)\b",
        RegexOptions.Compiled
    );

    private static readonly Regex ObservacaoBackwardStopRegex = new Regex(
        @"(?i)^\s*(?:Email|E-?mail|CNPJ|Inscri[cç][aã]o|Endere[cç]o|Cidade/UF|Telefone|CEP|Código\s+Produto|Descri[cç][aã]o\s+do\s+Produto|Cliente|Usu[áa]rio|Data\s+[:\-]|Data\s+Emiss[aã]o|Mat[eé]ria\s+prima)\b",
        RegexOptions.Compiled
    );

    private static readonly Regex UsuarioRegex = new Regex(
        @"(?i)\bUSU[ÁA]RIO\b\s*[:\-]?\s*([^\r\n]+)",
        RegexOptions.Compiled
    );

    private static readonly CultureInfo PtBr = new("pt-BR");

    public sealed record ParsedOp(
        string NumeroOp,
        string? CodigoProduto,
        string? DescricaoProduto,
        string? Cliente,
        string? DataOpIso,
        System.Collections.Generic.List<string> Materiais,
        bool Emborrachada,
        string? Destacador,
        string? ModalidadeEntrega,
        string? DataEntregaIso,
        string? HoraEntrega,
        string? Usuario,
        bool? Pertinax,
        bool? Poliester,
        bool? PapelCalibrado
    );

    public static ParsedOp Parse(string pdfPath)
    {
        using var doc = PdfDocument.Open(pdfPath);

        // extrai todo o texto
        var allText = string.Join("\n", doc.GetPages().Select(p =>
            ContentOrderTextExtractor.GetText(p)));

        // helpers internos
        static string ToAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var nfkd = s.Normalize(NormalizationForm.FormD);
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

        static string? ToIsoDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();
            var formatos = new[]
            {
                "dd/MM/yyyy","d/M/yyyy","dd-MM-yyyy","d-M-yyyy",
                "dd/MM/yy","d/M/yy","dd-MM-yy","d-M-yy",
                "yyyy-MM-dd","yyyy/MM/dd"
            };
            foreach (var fmt in formatos)
            {
                if (DateTime.TryParseExact(raw, fmt, PtBr, DateTimeStyles.None, out var dt))
                    return dt.ToString("yyyy-MM-dd");
            }
            // fallback mais flexível
            if (DateTime.TryParse(raw, PtBr, DateTimeStyles.None, out var dt2))
                return dt2.ToString("yyyy-MM-dd");
            return null;
        }

        // === encontra Numero OP como antes ===
        string fileBase = Path.GetFileNameWithoutExtension(pdfPath);
        string fileBaseAscii = ToAscii(fileBase).Replace("º", "o");

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
            if (mm.Count > 0) numero = mm[^1].Groups[1].Value;
        }
        if (string.IsNullOrWhiteSpace(numero))
        {
            var mm = LastDigitsRegex.Matches(ToAscii(allText));
            if (mm.Count > 0) numero = mm[^1].Groups[1].Value;
        }
        if (string.IsNullOrWhiteSpace(numero))
            numero = "DESCONHECIDO";

        // outros campos
        string Grab1(string pattern) =>
            Regex.Match(allText, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                 .Groups[1].Value.Trim();

        var codigo  = Grab1(@"C(?:ó|o)digo(?:\s*do\s*Produto)?\s*[:\-]\s*([A-Z0-9\.\-]+)");
        var descr   = Grab1(@"Descri[cç][aã]o\s*[:\-]\s*(.+?)\r?$");
        var cliente = Grab1(@"Cliente\s*[:\-]\s*(.+?)\r?$");

        var dataRaw = Grab1(@"Data\s*[:\-]\s*([0-9]{2}[/\-][0-9]{2}[/\-][0-9]{2,4})");
        var dataIso = ToIsoDate(dataRaw);

        // Materiais
        var matBlock = MateriaPrimaBlockRegex.Match(allText).Groups[1].Value;
        var materiais = Regex.Matches(matBlock, @"[^\r\n]+")
            .Select(m => m.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // Emborrachada
        bool emborrachada = materiais.Any(m => m.Contains("BORRACHA", StringComparison.OrdinalIgnoreCase));
        if (!emborrachada)
        {
            var search = ToAscii(allText).ToUpperInvariant();
            var collapsed = CollapseSpacesAndHyphens(search);
            if (collapsed.Contains("BORRACHA"))
                emborrachada = true;
            else if (BorrachaLooseRegex.IsMatch(search))
                emborrachada = true;
            else if (search.Contains("EMBORRACHAD") || Regex.IsMatch(search, @"REVESTI(?:MENTO)?\s+DE\s+BORRACHA", RegexOptions.IgnoreCase))
                emborrachada = true;
            if (materiais.Count == 0 && emborrachada)
                Console.WriteLine($"[PdfParser] Materiais vazio, mas fallback achou BORRACHA (OP={numero})");
        }

        // Extrair observações ou bloco para atributos extras
        bool hasObservacoesBlock = false;
        string observacoesBlock = "";
        var mObs = ObservacaoBlockRegex.Match(allText);
        if (mObs.Success)
        {
            hasObservacoesBlock = true;
            var rawObs = mObs.Groups[1].Value;
            var trimmed = TrimObservacoesBlock(rawObs);
            var pre = ExtractObservacoesPreBlock(allText, mObs.Index);
            observacoesBlock = BuildObservacoesBlock(pre, trimmed, rawObs);
        }
        else
        {
            observacoesBlock = allText;  // fallback
        }

        // Agora extrair novos atributos
        var attr = ParseExtrasFromText(observacoesBlock, allText, dataIso, hasObservacoesBlock);

        // Extrair “Usuario”
        string? usuario = null;
        var mUser = UsuarioRegex.Match(allText);
        if (mUser.Success)
        {
            var rawUsuario = NormalizeSpaces(mUser.Groups[1].Value);
            rawUsuario = rawUsuario.Trim('-', ':');
            if (!string.IsNullOrWhiteSpace(rawUsuario))
                usuario = rawUsuario;
        }

        // Montar retorno com todos campos
        return new ParsedOp(
            NumeroOp: numero,
            CodigoProduto: string.IsNullOrWhiteSpace(codigo) ? null : codigo,
            DescricaoProduto: string.IsNullOrWhiteSpace(descr) ? null : descr,
            Cliente: string.IsNullOrWhiteSpace(cliente) ? null : cliente,
            DataOpIso: dataIso,
            Materiais: materiais,
            Emborrachada: emborrachada,
            Destacador: attr.Destacador,
            ModalidadeEntrega: attr.ModalidadeEntrega,
            DataEntregaIso: attr.DataEntregaIso,
            HoraEntrega: attr.HoraEntrega,
            Usuario: usuario,
            Pertinax: attr.Pertinax,
            Poliester: attr.Poliester,
            PapelCalibrado: attr.PapelCalibrado
        );
    }

    private static Extras ParseExtrasFromText(string text, string fullText, string? dataOpIso, bool fromObservacoesBlock)
    {
        // Classe auxiliar interna
        Extras result = new Extras();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        string norm = text;
        norm = RemoveAcentos(norm);
        norm = norm.ToUpperInvariant();
        norm = Regex.Replace(norm, @"[\r\n\t]+", " ");
        norm = Regex.Replace(norm, @"\s{2,}", " ").Trim();

        var rawLines = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // Destacador
        var md = DestacadorRegex.Match(norm);
        if (md.Success)
        {
            string group = md.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(group) && md.Groups[2].Success)
                group = md.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(group))
            {
                group = group.Trim().Replace(" ", "").Replace("/", "/").ToUpperInvariant();
                if (group == "M/F" || group == "MF")
                    result.Destacador = "MF";
                else if (group == "M")
                    result.Destacador = "M";
                else if (group == "F")
                    result.Destacador = "F";
            }
        }

        // ModalidadeEntrega com sinônimos
        var mm = ModalidadeEntregaRegex.Match(norm);
        if (mm.Success)
        {
            string val = mm.Groups[1].Value.Trim().ToUpperInvariant();
            if (val.Contains("RETIRADA") || norm.Contains("RETIRA") || norm.Contains("VEM BUSCAR"))
                result.ModalidadeEntrega = "RETIRADA";
            else if (val.Contains("ENTREGAR") || norm.Contains("ENTREGA"))
                result.ModalidadeEntrega = "A ENTREGAR";
        }
        else
        {
            if (norm.Contains("RETIRA") || norm.Contains("RETIRADA") || norm.Contains("VEM BUSCAR"))
                result.ModalidadeEntrega = "RETIRADA";
            else if (norm.Contains("ENTREGA"))
                result.ModalidadeEntrega = "A ENTREGAR";
        }

        // Data/Hora Entrega (com suporte a 'ENTREGA REQUERIDA: dd/MM [HH:mm]')
        // 1) inline 'Entrega Requerida: dd/MM [HH:mm]'
        var inl = EntregaRequeridaInlineRegex.Match(norm);
        if (inl.Success)
        {
            string rawDate = inl.Groups[1].Value.Trim();
            string? rawTime = inl.Groups[2].Success ? inl.Groups[2].Value.Trim() : null;
            var iso = ToIsoDate(AjustaAnoSeNecessario(rawDate, dataOpIso, fullText));
            if (iso != null) { result.DataEntregaIso = iso; }
            if (!string.IsNullOrWhiteSpace(rawTime) && TimeSpan.TryParseExact(rawTime, new[] { "H\\:mm", "HH\\:mm" }, CultureInfo.InvariantCulture, out var tsIn))
                result.HoraEntrega = tsIn.ToString(@"hh\:mm");
        }
        else
        {
            // 2) separadas por rótulos
            var mdte = DataEntregaRegex.Match(norm);
            if (mdte.Success)
            {
                string raw = mdte.Groups[1].Value.Trim();
                var iso = ToIsoDate(AjustaAnoSeNecessario(raw, dataOpIso, fullText));
                if (iso != null)
                    result.DataEntregaIso = iso;
            }

            var mhr = HoraEntregaRegex.Match(norm);
            if (mhr.Success)
            {
                string rawh = mhr.Groups[1].Value.Trim();
                if (TimeSpan.TryParseExact(rawh, new[] { "H\\:mm", "HH\\:mm" }, CultureInfo.InvariantCulture, out var ts))
                    result.HoraEntrega = ts.ToString(@"hh\:mm");
            }
        }

        if (fromObservacoesBlock)
        {
            if (result.DataEntregaIso == null)
            {
                foreach (var line in rawLines)
                {
                    var cleaned = RemoveAcentos(line).ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(cleaned))
                        continue;
                    if (cleaned.StartsWith("DATA") || cleaned.Contains("DATA "))
                        continue;

                    var match = FallbackEntregaDateRegex.Match(line);
                    if (!match.Success)
                        continue;

                    var iso = ToIsoDate(AjustaAnoSeNecessario(match.Groups[1].Value, dataOpIso, fullText));
                    if (iso != null)
                    {
                        result.DataEntregaIso = iso;
                        break;
                    }
                }
            }

            if (result.HoraEntrega == null)
            {
                foreach (var line in rawLines)
                {
                    var cleaned = RemoveAcentos(line).ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(cleaned))
                        continue;

                    var colon = FallbackEntregaTimeColonRegex.Match(cleaned);
                    if (colon.Success && TryBuildHour(colon.Groups[1].Value, colon.Groups[2].Value, out var colonFormatted))
                    {
                        result.HoraEntrega = colonFormatted;
                        break;
                    }

                    var withLabel = FallbackEntregaTimeWithLabelRegex.Match(cleaned);
                    if (withLabel.Success && TryBuildHour(withLabel.Groups[1].Value, "00", out var labelFormatted))
                    {
                        result.HoraEntrega = labelFormatted;
                        break;
                    }
                }
            }
        }

        // Materiais especiais: Pertinax, Poliéster, Papel Calibrado
        string normFull = RemoveAcentos(fullText).ToUpperInvariant();
        if (norm.Contains("PERTINAX") || normFull.Contains("PERTINAX")) result.Pertinax = true;
        if (norm.Contains("POLIESTER") || normFull.Contains("POLIESTER") || normFull.Contains("POLIÉSTER")) result.Poliester = true;
        if (norm.Contains("PAPEL CALIBRADO") || normFull.Contains("PAPEL CALIBRADO") || normFull.Contains("CALIBRADO")) result.PapelCalibrado = true;

        // Regras adicionais para emborrachamento (BOR / SHORE) são avaliadas no chamador via materiais/flags

        return result;
    }

    private static bool TryBuildHour(string hourComponent, string minuteComponent, out string formatted)
    {
        formatted = string.Empty;
        if (!int.TryParse(hourComponent, out var hour)) return false;
        if (!int.TryParse(minuteComponent, out var minute)) return false;
        if (hour < 0 || hour > 23) return false;
        if (minute < 0 || minute > 59) return false;
        formatted = $"{hour:00}:{minute:00}";
        return true;
    }

    private static string AjustaAnoSeNecessario(string rawDate, string? dataOpIso, string fullText)
    {
        // Se a data já tem ano, retorna como veio
        if (Regex.IsMatch(rawDate, @"\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}") || Regex.IsMatch(rawDate, @"\d{4}[\/\-]\d{1,2}[\/\-]\d{1,2}"))
            return rawDate;

        // Sem ano (ex.: dd/MM). Tenta herdar do dataOpIso ou detectar no texto
        int year = DateTime.Now.Year;
        if (!string.IsNullOrWhiteSpace(dataOpIso))
        {
            var parts = dataOpIso.Split('-');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var y) && y >= 2000 && y <= 2100)
                year = y;
        }
        else
        {
            var m = Regex.Match(fullText, @"\b(20\d{2})\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var y2)) year = y2;
        }
        return rawDate + "/" + year;
    }

    private static string RemoveAcentos(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string TrimObservacoesBlock(string block)
    {
        if (string.IsNullOrWhiteSpace(block)) return block;

        var match = ObservacaoStopRegex.Match(block);
        if (match.Success && match.Index > 0)
            return block[..match.Index].TrimEnd();

        return block.TrimEnd();
    }

    private static string ExtractObservacoesPreBlock(string fullText, int observacaoIndex)
    {
        if (string.IsNullOrEmpty(fullText) || observacaoIndex <= 0)
            return string.Empty;

        int windowStart = Math.Max(0, observacaoIndex - 600);
        int length = observacaoIndex - windowStart;
        if (length <= 0)
            return string.Empty;

        var segment = fullText.Substring(windowStart, length);
        var lines = segment
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
            return string.Empty;

        var collected = new List<string>();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (ObservacaoBackwardStopRegex.IsMatch(line))
                break;

            collected.Add(line);

            if (collected.Count >= 8)
                break;
        }

        collected.Reverse();
        return string.Join("\n", collected);
    }

    private static string BuildObservacoesBlock(string preBlock, string trimmedBlock, string rawBlock)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(preBlock))
            parts.Add(preBlock.Trim());
        if (!string.IsNullOrWhiteSpace(trimmedBlock))
            parts.Add(trimmedBlock.Trim());

        if (parts.Count > 0)
            return string.Join("\n", parts);

        return rawBlock;
    }

    // classe auxiliar interna para extras
    private class Extras
    {
        public string? Destacador { get; set; }
        public string? ModalidadeEntrega { get; set; }
        public string? DataEntregaIso { get; set; }
        public string? HoraEntrega { get; set; }
        public bool? Pertinax { get; set; }
        public bool? Poliester { get; set; }
        public bool? PapelCalibrado { get; set; }
    }

    private static string? ToIsoDate(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    raw = raw.Trim();
    var formatos = new[]
    {
        "dd/MM/yyyy","d/M/yyyy","dd-MM-yyyy","d-M-yyyy",
        "dd/MM/yy","d/M/yy","dd-MM-yy","d-M-yy",
        "yyyy-MM-dd","yyyy/MM/dd"
    };
    foreach (var fmt in formatos)
    {
        if (DateTime.TryParseExact(raw, fmt, PtBr, DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd");
    }
    if (DateTime.TryParse(raw, PtBr, DateTimeStyles.None, out var dt2))
        return dt2.ToString("yyyy-MM-dd");
    return null;
}

}

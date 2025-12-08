using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        @"Mat[ée]ria[\- \s]*prima(.*?)(?:\n\s*\n|Observa[cç][aã]o|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex BorrachaLooseRegex = new(
        @"B\s*O\s*R\s*R\s*A\s*C\s*H\s*A",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );
    private static readonly Regex VincoTokenRegex = new(
        @"\bVINC(?:A|O)[A-Z0-9]*\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex DestacadorRegex = new Regex(
        @"\bDESTACADOR\b\s*[:\-]?\s*([MFmf](?:\s*/\s*[MFmf])?)\b|\b([MFmf](?:\s*/\s*[MFmf]))\s*DESTACADOR\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex ModalidadeEntregaRegex = new Regex(
        @"\b(RETIRADA|A\s*ENTREGAR)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
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
        @"(?im)^\s*Observa[cç][aã]o(?:es)?\s*[:\-]?\s*(.+?)(?=\n\s*[A-Z][^\r\n]*:|\n\s*\w+:|$)",
        RegexOptions.Compiled | RegexOptions.Singleline
    );

    private static readonly Regex ObservacaoStopRegex = new Regex(
        @"(?im)^\s*(?:Mat[eé]ria[\s\-]*prima|Data\s+Emiss[aã]o|Data\s+Entrega|Etapa\s*\/?\s*Eventos|Operador|Assinatura\s+Cliente)\b",
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
    
    private static readonly Regex HorarioRegex = new Regex(
        @"(?i)\b(?:Hor[aá]rio|Expediente)\s*[:\-]?\s*([^\r\n]+)", RegexOptions.Compiled
    );
    
    // Regex for unstructured address line (e.g., "09691-350 RUA LIBERO BADARO 1201 - PAULICEIA SAO BERNARDO DO CAMPO/SP")
    private static readonly Regex UnstructuredAddressLineRegex = new(
        @"(?i)(\d{5}-?\d{3})\s*(.+)", RegexOptions.Compiled
    );
    // Regex to parse the components of the full address string (e.g. "RUA LIBERO BADARO 1201 - PAULICEIA SAO BERNARDO DO CAMPO/SP")
    private static readonly Regex AddressComponentsRegex = new(
        @"(?i)(.+?)(?:\s*-\s*([^\r\n]+?))?\s*(?:([A-Z\u00C0-\u00FF].*?)\/([A-Z]{2}))?$", RegexOptions.Compiled
    );

    // New Regexes for additional client data
    private static readonly Regex CnpjCpfRegex = new(
        @"(?i)\b(?:CNPJ|CPF|C.N.P.J.|C.P.F.)?\s*[:\-]?\s*(\d{2}\.?\d{3}\.?\d{3}\/?\d{4}\-?\d{2}|\d{3}\.?\d{3}\.?\d{3}\-?\d{2})\b", RegexOptions.Compiled
    );
    private static readonly Regex InscricaoEstadualRegex = new(
        @"(?i)\b(?:INSCRI[CÇ][AÃ]O(?:\s*ESTADUAL)?|I\.E\.)?\s*[:\-]?\s*(\d{3,15})\b", RegexOptions.Compiled
    );
    private static readonly Regex TelefoneRegex = new(
        @"(?i)\b(?:TELEFONE|FONE|TEL)\s*[:\-]?\s*(\(?\d{2}\)?\s*\d{4,5}\-?\d{4})\b", RegexOptions.Compiled
    );
    private static readonly Regex EmailRegex = new(
        @"(?i)\b(?:E-?MAIL|EMAIL|E\-MAIL)\s*[:\-]?\s*([A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})\b", RegexOptions.Compiled
    );
    private static readonly Regex CepRegex = new(
        @"(?i)\b(?:CEP)?\s*[:\-]?\s*(\d{5}\-?\d{3})\b", RegexOptions.Compiled
    );

    private static readonly CultureInfo PtBr = new("pt-BR");

    public sealed record EnderecoSugerido(
        string? Uf,
        string? Cidade,
        string? Bairro,
        string? Logradouro,
        string? HorarioFuncionamento,
        string? PadraoEntrega,
        string? Cep
    );

    public sealed record ParsedOp(
        string NumeroOp,
        string? CodigoProduto,
        string? DescricaoProduto,
        string? Cliente,
        string? DataOpIso,
        List<string> Materiais,
        bool Emborrachada,
        bool VaiVinco,
        string? Destacador,
        string? ModalidadeEntrega,
        string? DataEntregaIso,
        string? HoraEntrega,
        string? Usuario,
        bool? Pertinax,
        bool? Poliester,
        bool? PapelCalibrado,
        string? ClienteNomeOficial,
        List<string> ApelidosSugeridos,
        List<EnderecoSugerido> EnderecosSugeridos,
        string? PadraoEntregaSugerido,
        string? DataUltimoServicoSugerida,
        string? CnpjCpf,
        string? InscricaoEstadual,
        string? Telefone,
        string? Email
    );

    public static ParsedOp Parse(string pdfPath)
    {
        using var doc = PdfDocument.Open(pdfPath);
        var allText = string.Join("\n", doc.GetPages().Select(p =>
            ContentOrderTextExtractor.GetText(p)));
        
        return Parse(allText, Path.GetFileNameWithoutExtension(pdfPath));
    }

    // Overload for testing, directly accepts allText
    public static ParsedOp Parse(string allText, string pdfFileName = "test.pdf")
    {
        string fileBase = pdfFileName;
        string fileBaseAscii = ToAscii(fileBase).Replace("º", "o");

        var mName = NumeroOpFromNameRegex.Match(fileBaseAscii);
        string numero = mName.Success ? mName.Groups[1].Value.Trim() : string.Empty;

        if (string.IsNullOrWhiteSpace(numero))
        {
            string Grab(string pattern) =>
                Regex.Match(ToAscii(allText), pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                     .Groups[1].Value.Trim();

            numero = Grab(@"(?:OP|NR|ORDEM\s*de\s*PRODUCAO)\s*[:\-]?\s*([0-9]{4,})");
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

        string Grab1(string pattern) =>
            Regex.Match(allText, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
                 .Groups[1].Value.Trim();

        var codigo  = Grab1(@"C(?:ó|o)digo(?:\s*do\s*Produto)?\s*[:\-]\s*([A-Z0-9\.\-]+)");
        var descr   = Grab1(@"Descri[cç][aã]o\s*[:\-]\s*(.+?)\r?$");
        
        // Flexible client extraction
        var cliente = Grab1(@"(?:Cliente|Raz[ãa]o\s*Social|Sacado)\s*[:\-]?\s*(?:[\r\n]+\s*)?([^\r\n]+)");

        // Fallback: If client seems to be a header/label captured by mistake (columnar layout issue)
        if (string.IsNullOrWhiteSpace(cliente) || 
            cliente.Contains("Razão Social", StringComparison.OrdinalIgnoreCase) || 
            cliente.Contains("Cliente", StringComparison.OrdinalIgnoreCase) ||
            cliente.Contains("Sacado", StringComparison.OrdinalIgnoreCase))
        {
            // Strategy: Find header "Cód Cliente Nome/Razão Social" and look for the value line below it.
            // Value line pattern: "01276 YCAR ARTES GRÁFICAS..." (Digits Space Letter...)
            try 
            {
                var headerMatch = Regex.Match(allText, @"C(?:ó|o)d(?:igo)?\s*Cliente\s*Nome/Raz[ãa]o\s*Social", RegexOptions.IgnoreCase);
                if (headerMatch.Success)
                {
                    var substring = allText.Substring(headerMatch.Index + headerMatch.Length);
                    using (var reader = new StringReader(substring))
                    {
                        string? line;
                        int linesChecked = 0;
                        while ((line = reader.ReadLine()) != null && linesChecked < 6)
                        {
                            line = line.Trim();
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                // Pattern: Starts with digits, space, then a letter (to avoid dates like 12/11/2025)
                                if (Regex.IsMatch(line, @"^\d+\s+[A-Z\u00C0-\u00FF]", RegexOptions.IgnoreCase))
                                {
                                    // Extract the name part (Group 1)
                                    var m = Regex.Match(line, @"^\d+\s+(.+)$");
                                    if (m.Success)
                                    {
                                        cliente = m.Groups[1].Value.Trim();
                                        break;
                                    }
                                }
                            }
                            linesChecked++;
                        }
                    }
                }
            }
            catch 
            {
                // Ignore errors in fallback
            }
        }
        
        if (string.IsNullOrWhiteSpace(cliente))
        {
            Console.WriteLine($"[PdfParser] WARN: Cliente não encontrado na OP {numero}. Dump do texto (inicio):");
            Console.WriteLine(allText.Length > 600 ? allText[..600] : allText);
        }

        var dataRaw = Grab1(@"Data\s*[:\-]\s*([0-9]{2}[\/\-][0-9]{2}[\/\-][0-9]{2,4})");
        var dataIso = ToIsoDate(dataRaw);

        var matBlock = MateriaPrimaBlockRegex.Match(allText).Groups[1].Value;
        var materiais = Regex.Matches(matBlock, @"[^\r\n]+")
            .Select(m => m.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var matBlockNormalized = RemoveAcentos(matBlock).ToUpperInvariant();
        bool vaiVinco = HasVinco(materiais, matBlockNormalized);

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
            observacoesBlock = allText;  
        }

        var attr = ParseExtrasFromText(observacoesBlock, allText, dataIso, hasObservacoesBlock);

        string? usuario = null;
        var mUser = UsuarioRegex.Match(allText);
        if (mUser.Success)
        {
            var rawUsuario = NormalizeSpaces(mUser.Groups[1].Value);
            rawUsuario = rawUsuario.Trim('-', ':');
            if (!string.IsNullOrWhiteSpace(rawUsuario))
                usuario = rawUsuario;
        }
        
        // Extract address and hours
        var enderecosSugeridos = ExtractAddresses(allText, attr.ModalidadeEntrega);

        // Extract new fields
        var cnpjCpf = Grab1(CnpjCpfRegex.ToString());
        var inscricaoEstadual = Grab1(InscricaoEstadualRegex.ToString());
        var telefone = Grab1(TelefoneRegex.ToString());
        var email = Grab1(EmailRegex.ToString());
        var cep = Grab1(CepRegex.ToString());
        
        var aliases = new List<string>();
        // No explicit alias extraction logic, but could be added here if OP has alias field.

        string? dataUltimoServico = attr.DataEntregaIso != null && attr.HoraEntrega != null 
             ? $"{attr.DataEntregaIso}T{attr.HoraEntrega}:00" 
             : (attr.DataEntregaIso != null ? $"{attr.DataEntregaIso}T00:00:00" : null);
             
        return new ParsedOp(
            NumeroOp: numero,
            CodigoProduto: string.IsNullOrWhiteSpace(codigo) ? null : codigo,
            DescricaoProduto: string.IsNullOrWhiteSpace(descr) ? null : descr,
            Cliente: string.IsNullOrWhiteSpace(cliente) ? null : cliente,
            DataOpIso: dataIso,
            Materiais: materiais,
            Emborrachada: emborrachada,
            VaiVinco: vaiVinco,
            Destacador: attr.Destacador,
            ModalidadeEntrega: attr.ModalidadeEntrega,
            DataEntregaIso: attr.DataEntregaIso,
            HoraEntrega: attr.HoraEntrega,
            Usuario: usuario,
            Pertinax: attr.Pertinax,
            Poliester: attr.Poliester,
            PapelCalibrado: attr.PapelCalibrado,
            ClienteNomeOficial: cliente,
            ApelidosSugeridos: aliases,
            EnderecosSugeridos: enderecosSugeridos,
            PadraoEntregaSugerido: attr.ModalidadeEntrega,
            DataUltimoServicoSugerida: dataUltimoServico,
            CnpjCpf: string.IsNullOrWhiteSpace(cnpjCpf) ? null : cnpjCpf,
            InscricaoEstadual: string.IsNullOrWhiteSpace(inscricaoEstadual) ? null : inscricaoEstadual,
            Telefone: string.IsNullOrWhiteSpace(telefone) ? null : telefone,
            Email: string.IsNullOrWhiteSpace(email) ? null : email
        );
    }

    private static List<EnderecoSugerido> ExtractAddresses(string allText, string? modalidadeEntrega)
    {
        var enderecosSugeridos = new List<EnderecoSugerido>();
        
        // Find the line that looks like the unstructured address, usually after "CEP Endereço..." header
        var headerLineMatch = Regex.Match(allText, @"CEP\s*Endere[cç]o(?:\s*\(rua,\s*nº,\s*complemento,\s*bairro\))?\s*Cidade/UF", RegexOptions.IgnoreCase);
        if (headerLineMatch.Success)
        {
            var searchArea = allText.Substring(headerLineMatch.Index + headerLineMatch.Length);
            // Limit search area to the next few lines
            var linesList = new List<string>();
            using (var reader = new StringReader(searchArea))
            {
                string? line;
                int count = 0;
                while ((line = reader.ReadLine()) != null && count < 5) // Read up to 5 lines after the header
                {
                    linesList.Add(line);
                    count++;
                }
            }
            var candidateAddressLine = string.Join(" ", linesList.Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)));
            
            var unstructuredAddressMatch = UnstructuredAddressLineRegex.Match(candidateAddressLine);

            if (unstructuredAddressMatch.Success)
            {
                string? cep = NormalizeSpaces(unstructuredAddressMatch.Groups[1].Value);
                string? addressRemainder = NormalizeSpaces(unstructuredAddressMatch.Groups[2].Value);
                
                string? bairro = null;
                string? logradouro = null;
                string? cidade = null;
                string? uf = null;

                // Further parse the addressRemainder to extract Logradouro, Bairro, Cidade, UF
                // Example: "RUA LIBERO BADARO 1201 - PAULICEIA SAO BERNARDO DO CAMPO/SP"
                var addressComponentsMatch = Regex.Match(addressRemainder, 
                    @"^(.*?)(?:\s*-\s*([^\r\n]+?))?\s*(?:([A-Z\u00C0-\u00FF].*?)\/([A-Z]{2}))?$", RegexOptions.IgnoreCase);

                if (addressComponentsMatch.Success)
                {
                    logradouro = NormalizeSpaces(addressComponentsMatch.Groups[1].Value);
                    bairro = NormalizeSpaces(addressComponentsMatch.Groups[2].Success ? addressComponentsMatch.Groups[2].Value : null);
                    cidade = NormalizeSpaces(addressComponentsMatch.Groups[3].Success ? addressComponentsMatch.Groups[3].Value : null);
                    uf = NormalizeSpaces(addressComponentsMatch.Groups[4].Success ? addressComponentsMatch.Groups[4].Value : null);
                }
                
                enderecosSugeridos.Add(new EnderecoSugerido(uf, cidade, bairro, logradouro, null, modalidadeEntrega, cep));
            }
        }
        return enderecosSugeridos;
    }

    private static string ToAscii(string s)
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

    private static string NormalizeSpaces(string s) =>
        Regex.Replace(s ?? string.Empty, @"[ \t\r\n]+", " ").Trim();

    private static string CollapseSpacesAndHyphens(string s) =>
        Regex.Replace(s ?? string.Empty, @"[\s\-]+", string.Empty);

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

    private static Extras ParseExtrasFromText(string text, string fullText, string? dataOpIso, bool fromObservacoesBlock)
    {
        Extras result = new Extras();
        if (string.IsNullOrWhiteSpace(text)) return result;

        string norm = text;
        norm = RemoveAcentos(norm);
        norm = norm.ToUpperInvariant();
        norm = Regex.Replace(norm, @"[\r\n\t]+", " ");
        norm = Regex.Replace(norm, @"\s{2,}", " ").Trim();

        var rawLines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var md = DestacadorRegex.Match(norm);
        if (md.Success)
        {
            string group = md.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(group) && md.Groups[2].Success) group = md.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(group))
            {
                group = group.Trim().Replace(" ", "").Replace("/", "/").ToUpperInvariant();
                if (group == "M/F" || group == "MF") result.Destacador = "MF";
                else if (group == "M") result.Destacador = "M";
                else if (group == "F") result.Destacador = "F";
            }
        }

        var mm = ModalidadeEntregaRegex.Match(norm);
        if (mm.Success)
        {
            string val = mm.Groups[1].Value.Trim().ToUpperInvariant();
            if (val.Contains("RETIRADA") || norm.Contains("RETIRA") || norm.Contains("VEM BUSCAR")) result.ModalidadeEntrega = "RETIRADA";
            else if (val.Contains("ENTREGAR") || norm.Contains("ENTREGA")) result.ModalidadeEntrega = "A ENTREGAR";
        }
        else
        {
            if (norm.Contains("RETIRA") || norm.Contains("RETIRADA") || norm.Contains("VEM BUSCAR")) result.ModalidadeEntrega = "RETIRADA";
            else if (norm.Contains("ENTREGA")) result.ModalidadeEntrega = "A ENTREGAR";
        }

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
            var mdte = DataEntregaRegex.Match(norm);
            if (mdte.Success)
            {
                string raw = mdte.Groups[1].Value.Trim();
                var iso = ToIsoDate(AjustaAnoSeNecessario(raw, dataOpIso, fullText));
                if (iso != null) result.DataEntregaIso = iso;
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
                var extractedDate = ExtractEntregaDateFromLines(rawLines, dataOpIso, fullText);
                if (!string.IsNullOrEmpty(extractedDate)) result.DataEntregaIso = extractedDate;
            }

            if (result.HoraEntrega == null)
            {
                var extractedHora = ExtractEntregaTimeFromLines(rawLines);
                if (!string.IsNullOrEmpty(extractedHora)) result.HoraEntrega = extractedHora;
            }
        }

        string normFull = RemoveAcentos(fullText).ToUpperInvariant();
        if (norm.Contains("PERTINAX") || normFull.Contains("PERTINAX")) result.Pertinax = true;
        if (norm.Contains("POLIESTER") || normFull.Contains("POLIESTER") || normFull.Contains("POLIÉSTER")) result.Poliester = true;
        if (norm.Contains("PAPEL CALIBRADO") || normFull.Contains("PAPEL CALIBRADO") || normFull.Contains("CALIBRADO")) result.PapelCalibrado = true;

        return result;
    }

    private static bool HasVinco(IReadOnlyCollection<string> materiais, string? normalizedMatBlock)
    {
        if (materiais is { Count: > 0 })
        {
            foreach (var material in materiais)
            {
                if (string.IsNullOrWhiteSpace(material)) continue;
                var normalized = RemoveAcentos(material).ToUpperInvariant();
                if (VincoTokenRegex.IsMatch(normalized)) return true;
            }
        }
        if (!string.IsNullOrWhiteSpace(normalizedMatBlock) && VincoTokenRegex.IsMatch(normalizedMatBlock)) return true;
        return false;
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
        if (Regex.IsMatch(rawDate, @"\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}") || Regex.IsMatch(rawDate, @"\d{4}[\/\-]\d{1,2}[\/\-]\d{1,2}"))
            return rawDate;

        int year = DateTime.Now.Year;
        if (!string.IsNullOrWhiteSpace(dataOpIso))
        {
            var parts = dataOpIso.Split('-');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var y) && y >= 2000 && y <= 2100) year = y;
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
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string TrimObservacoesBlock(string block)
    {
        if (string.IsNullOrWhiteSpace(block)) return block;
        var match = ObservacaoStopRegex.Match(block);
        if (match.Success && match.Index > 0) return block[..match.Index].TrimEnd();
        return block.TrimEnd();
    }

    private static string ExtractObservacoesPreBlock(string fullText, int observacaoIndex)
    {
        if (string.IsNullOrEmpty(fullText) || observacaoIndex <= 0) return string.Empty;
        int windowStart = Math.Max(0, observacaoIndex - 600);
        int length = observacaoIndex - windowStart;
        if (length <= 0) return string.Empty;
        var segment = fullText.Substring(windowStart, length);
        var lines = segment.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return string.Empty;
        var collected = new List<string>();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (ObservacaoBackwardStopRegex.IsMatch(line)) break;
            collected.Add(line);
            if (collected.Count >= 8) break;
        }
        collected.Reverse();
        return string.Join("\n", collected);
    }

    private static string BuildObservacoesBlock(string preBlock, string trimmedBlock, string rawBlock)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(preBlock)) parts.Add(preBlock.Trim());
        if (!string.IsNullOrWhiteSpace(trimmedBlock)) parts.Add(trimmedBlock.Trim());
        if (parts.Count > 0) return string.Join("\n", parts);
        return rawBlock;
    }

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

    private static string? ExtractEntregaDateFromLines(IReadOnlyList<string> rawLines, string? dataOpIso, string fullText)
    {
        if (rawLines.Count == 0) return null;
        var normalized = rawLines.Select(l => RemoveAcentos(l).ToUpperInvariant()).ToArray();
        string? bestIso = null;
        int bestScore = int.MinValue;
        int bestIndex = -1;
        for (int i = 0; i < rawLines.Count; i++)
        {
            string original = rawLines[i];
            if (string.IsNullOrWhiteSpace(original)) continue;
            var match = FallbackEntregaDateRegex.Match(original);
            if (!match.Success) continue;
            var iso = ToIsoDate(AjustaAnoSeNecessario(match.Groups[1].Value, dataOpIso, fullText));
            if (iso == null) continue;
            string currentNorm = normalized[i];
            string prevNorm = i > 0 ? normalized[i - 1] : string.Empty;
            string nextNorm = i + 1 < normalized.Length ? normalized[i + 1] : string.Empty;
            int score = 10;
            if (LooksLikeHeader(prevNorm) || LooksLikeHeader(currentNorm)) score -= 30;
            if (HasLogisticsKeyword(currentNorm)) score += 30;
            if (HasLogisticsKeyword(prevNorm) || HasLogisticsKeyword(nextNorm)) score += 20;
            if (HasTimeKeyword(prevNorm) || HasTimeKeyword(nextNorm)) score += 5;
            if (score > bestScore || (score == bestScore && i > bestIndex))
            {
                bestScore = score;
                bestIndex = i;
                bestIso = iso;
            }
        }
        if (bestIso == null) return null;
        return bestScore >= 10 ? bestIso : null;
    }

    private static string? ExtractEntregaTimeFromLines(IReadOnlyList<string> rawLines)
    {
        if (rawLines.Count == 0) return null;
        var normalized = rawLines.Select(l => RemoveAcentos(l).ToUpperInvariant()).ToArray();
        string? bestHora = null;
        int bestScore = int.MinValue;
        int bestIndex = -1;
        for (int i = 0; i < rawLines.Count; i++)
        {
            string original = rawLines[i];
            if (string.IsNullOrWhiteSpace(original)) continue;
            string currentNorm = normalized[i];
            string prevNorm = i > 0 ? normalized[i - 1] : string.Empty;
            string nextNorm = i + 1 < normalized.Length ? normalized[i + 1] : string.Empty;
            void Consider(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate)) return;
                int score = 10;
                if (HasTimeKeyword(currentNorm)) score += 25;
                if (HasTimeKeyword(prevNorm) || HasTimeKeyword(nextNorm)) score += 10;
                if (HasLogisticsKeyword(currentNorm)) score += 20;
                if (HasLogisticsKeyword(prevNorm) || HasLogisticsKeyword(nextNorm)) score += 15;
                if (LooksLikeHeader(currentNorm) || LooksLikeHeader(prevNorm)) score -= 30;
                if (score > bestScore || (score == bestScore && i > bestIndex))
                {
                    bestScore = score;
                    bestIndex = i;
                    bestHora = candidate;
                }
            }
            var colon = FallbackEntregaTimeColonRegex.Match(original);
            if (colon.Success && TryBuildHour(colon.Groups[1].Value, colon.Groups[2].Value, out var colonFormatted)) Consider(colonFormatted);
            var withLabel = FallbackEntregaTimeWithLabelRegex.Match(currentNorm);
            if (withLabel.Success && TryBuildHour(withLabel.Groups[1].Value, "00", out var labelFormatted)) Consider(labelFormatted);
        }
        if (bestHora == null) return null;
        return bestScore >= 10 ? bestHora : null;
    }

    private static bool HasLogisticsKeyword(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return false;
        return normalized.Contains("ENTREG", StringComparison.Ordinal) || normalized.Contains("RETIRA", StringComparison.Ordinal)
            || normalized.Contains("RETIRADA", StringComparison.Ordinal) || normalized.Contains("RETIRAR", StringComparison.Ordinal)
            || normalized.Contains("COLETA", StringComparison.Ordinal) || normalized.Contains("COLET", StringComparison.Ordinal)
            || normalized.Contains("EXPEDI", StringComparison.Ordinal) || normalized.Contains("ENVIO", StringComparison.Ordinal)
            || normalized.Contains("ENVIAR", StringComparison.Ordinal) || normalized.Contains("PRAZO", StringComparison.Ordinal)
            || normalized.Contains("REQUER", StringComparison.Ordinal) || normalized.Contains("RECOLH", StringComparison.Ordinal)
            || normalized.Contains("BUSCA", StringComparison.Ordinal) || normalized.Contains("BUSCAR", StringComparison.Ordinal);
    }

    private static bool HasTimeKeyword(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return false;
        return normalized.Contains("HORA", StringComparison.Ordinal) || normalized.Contains("HORAS", StringComparison.Ordinal)
            || normalized.Contains("HORARIO", StringComparison.Ordinal) || normalized.Contains("HORARIOS", StringComparison.Ordinal)
            || normalized.Contains("HRS", StringComparison.Ordinal) || normalized.Contains("HS", StringComparison.Ordinal);
    }

    private static bool LooksLikeHeader(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return false;
        var trimmed = normalized.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.StartsWith("DATA", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("DATA EMIS", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("DATA OP", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("EMISSA", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("CRIAC", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("GERAC", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("CADAST", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("ORDEM", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("Nº", StringComparison.Ordinal) || trimmed.Contains("N°", StringComparison.Ordinal)) return true;
        if (trimmed.StartsWith("NR", StringComparison.Ordinal) && trimmed.Contains("OP", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("NUMERO", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("QTDE", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("QUANT", StringComparison.Ordinal)) return true;
        if (trimmed.Contains("CODIGO", StringComparison.Ordinal)) return true;
        return false;
    }
}

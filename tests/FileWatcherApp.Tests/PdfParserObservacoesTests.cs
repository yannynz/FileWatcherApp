using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Xunit;

namespace FileMonitor.Tests;

public class PdfParserObservacoesTests
{
    [Fact]
    public void Parse_SamplePdf_RespeitaDadosDeEntregaDasObservacoes()
    {
        var baseDir = AppContext.BaseDirectory;
        var pdfPath = Path.GetFullPath(Path.Combine(
            baseDir,
            "..", "..", "..", "..", "..",
            "Ordem de Produção nº 119995.pdf"));

        Assert.True(File.Exists(pdfPath), $"PDF não encontrado em '{pdfPath}'");

        using var doc = PdfDocument.Open(pdfPath);
        var allText = string.Join("\n", doc.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));

        var obsPattern = new Regex(
            @"(?im)^\s*Observa[cç][aã]o(?:es)?\s*[:\-]?\s*(.+?)(?=^\s*[A-Z][^\r\n]*:|^\s*\w+:|$)",
            RegexOptions.Singleline);

        var obsMatch = obsPattern.Match(allText);
        Assert.True(obsMatch.Success);

        var rawObs = obsMatch.Groups[1].Value;

        var trimMethod = typeof(PdfParser).GetMethod(
            "TrimObservacoesBlock",
            BindingFlags.NonPublic | BindingFlags.Static);

        var extractPre = typeof(PdfParser).GetMethod(
            "ExtractObservacoesPreBlock",
            BindingFlags.NonPublic | BindingFlags.Static);

        var buildBlock = typeof(PdfParser).GetMethod(
            "BuildObservacoesBlock",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(trimMethod);
        Assert.NotNull(extractPre);
        Assert.NotNull(buildBlock);

        var trimmedObs = trimMethod!.Invoke(null, new object?[] { rawObs }) as string ?? string.Empty;
        var preObs = extractPre!.Invoke(null, new object?[] { allText, obsMatch.Index }) as string ?? string.Empty;
        var combined = buildBlock!.Invoke(null, new object?[] { preObs, trimmedObs, rawObs }) as string ?? string.Empty;

        Assert.Contains("17/09/25", combined, StringComparison.Ordinal);

        var parsed = PdfParser.Parse(pdfPath);

        Assert.Equal("2025-09-17", parsed.DataEntregaIso);
        Assert.Equal("16:00", parsed.HoraEntrega);
        Assert.Equal("RETIRADA", parsed.ModalidadeEntrega);
        Assert.False(parsed.VaiVinco);
    }

    [Fact]
    public void ParseExtrasFromText_ComDataExtraiDeObservacao()
    {
        const string observacoes = "EMBORRACHAMENTO\nRETIRA\n17/09/2025\n16:00";
        const string fullText = "Data: 16/09/2025 12:09\n" + observacoes;

        var parseExtras = typeof(PdfParser).GetMethod(
            "ParseExtrasFromText",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(parseExtras);

        var extras = parseExtras!.Invoke(null, new object?[]
        {
            observacoes,
            fullText,
            "2025-09-15",
            true
        });

        Assert.NotNull(extras);

        var extrasType = extras!.GetType();
        var dataEntrega = extrasType.GetProperty("DataEntregaIso")?.GetValue(extras) as string;
        var horaEntrega = extrasType.GetProperty("HoraEntrega")?.GetValue(extras) as string;
        var modalidade = extrasType.GetProperty("ModalidadeEntrega")?.GetValue(extras) as string;

        Assert.Equal("2025-09-17", dataEntrega);
        Assert.Equal("16:00", horaEntrega);
        Assert.Equal("RETIRADA", modalidade);
    }

    [Fact]
    public void ParseExtrasFromText_SemDataNaoCaiNoCabecalho()
    {
        const string observacoes = "EMBORRACHAMENTO\nRETIRA";
        const string fullText = "Data: 16/09/2025 12:09\n" + observacoes;

        var parseExtras = typeof(PdfParser).GetMethod(
            "ParseExtrasFromText",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(parseExtras);

        var extras = parseExtras!.Invoke(null, new object?[]
        {
            observacoes,
            fullText,
            "2025-09-15",
            true
        });

        Assert.NotNull(extras);

        var extrasType = extras!.GetType();
        var dataEntrega = extrasType.GetProperty("DataEntregaIso")?.GetValue(extras) as string;
        var horaEntrega = extrasType.GetProperty("HoraEntrega")?.GetValue(extras) as string;

        Assert.Null(dataEntrega);
        Assert.Null(horaEntrega);
    }

    [Fact]
    public void TrimObservacoesBlock_RemoveSecoesEstruturadas()
    {
        var method = typeof(PdfParser).GetMethod(
            "TrimObservacoesBlock",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var input = "Linha 1\nLinha 2\nMatéria prima utilizada na produção\nResto ignorado";
        var trimmed = method!.Invoke(null, new object?[] { input }) as string;

        Assert.Equal("Linha 1\nLinha 2", trimmed);
    }

    [Fact]
    public void ParseExtrasFromText_ComCabecalhoNaoConfundeDataEntrega()
    {
        const string observacoes = """
Nº O.P.
119995
Data
15/09/2025
Quantidade
1
Observação
EMBORRACHAMENTO
RETIRA
18/09/2025
16:00 HORAS
""";

        const string fullText = "Data: 15/09/2025 08:15\n" + observacoes;

        var parseExtras = typeof(PdfParser).GetMethod(
            "ParseExtrasFromText",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(parseExtras);

        var extras = parseExtras!.Invoke(null, new object?[]
        {
            observacoes,
            fullText,
            "2025-09-15",
            true
        });

        Assert.NotNull(extras);

        var extrasType = extras!.GetType();
        var dataEntrega = extrasType.GetProperty("DataEntregaIso")?.GetValue(extras) as string;
        var horaEntrega = extrasType.GetProperty("HoraEntrega")?.GetValue(extras) as string;

        Assert.Equal("2025-09-18", dataEntrega);
        Assert.Equal("16:00", horaEntrega);
    }

    [Fact]
    public void ExtractEntregaDateFromLines_IgnoraCabecalhoSemContexto()
    {
        var method = typeof(PdfParser).GetMethod(
            "ExtractEntregaDateFromLines",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var linhas = new List<string>
        {
            "Nº O.P.",
            "119995",
            "Data",
            "15/09/2025",
            "Quantidade",
            "1"
        };

        var result = method!.Invoke(null, new object?[]
        {
            linhas,
            "2025-09-15",
            "Data: 15/09/2025 08:15"
        }) as string;

        Assert.Null(result);
    }

    [Fact]
    public void ExtractEntregaDateFromLines_PriorizaDataComPalavraEntrega()
    {
        var method = typeof(PdfParser).GetMethod(
            "ExtractEntregaDateFromLines",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var linhas = new List<string>
        {
            "Nº O.P.",
            "119995",
            "Data",
            "15/09/2025",
            "Observação",
            "Entrega requerida 20/09/2025",
            "RETIRA"
        };

        var result = method!.Invoke(null, new object?[]
        {
            linhas,
            "2025-09-15",
            "Data: 15/09/2025 08:15\n" + string.Join('\n', linhas)
        }) as string;

        Assert.Equal("2025-09-20", result);
    }

    [Fact]
    public void ExtractEntregaTimeFromLines_IgnoraHorasSemIndicador()
    {
        var method = typeof(PdfParser).GetMethod(
            "ExtractEntregaTimeFromLines",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var linhas = new List<string>
        {
            "Data emissão: 09:30"
        };

        var result = method!.Invoke(null, new object?[]
        {
            linhas
        }) as string;

        Assert.Null(result);
    }

    [Fact]
    public void ExtractEntregaTimeFromLines_PreferenciaPorLinhaComRetira()
    {
        var method = typeof(PdfParser).GetMethod(
            "ExtractEntregaTimeFromLines",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var linhas = new List<string>
        {
            "Horário de criação 08:00",
            "RETIRA 16:30 HORAS"
        };

        var result = method!.Invoke(null, new object?[]
        {
            linhas
        }) as string;

        Assert.Equal("16:30", result);
    }

    [Fact]
    public void HasVinco_IdentificaMaterialComVinco()
    {
        var method = typeof(PdfParser).GetMethod(
            "HasVinco",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var materiais = new List<string> { "VINCO 2PT 23,80" };
        var result = (bool)method!.Invoke(null, new object?[]
        {
            materiais,
            null
        })!;

        Assert.True(result);
    }

    [Fact]
    public void HasVinco_FallbackDetectaTextoNormalizado()
    {
        var method = typeof(PdfParser).GetMethod(
            "HasVinco",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, new object?[]
        {
            new List<string>(),
            "VINCADOR 3PT"
        })!;

        Assert.True(result);
    }

    [Fact]
    public void HasVinco_RetornaFalseQuandoAusente()
    {
        var method = typeof(PdfParser).GetMethod(
            "HasVinco",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, new object?[]
        {
            new List<string> { "CORTE 2PT", "PICOTE" },
            null
        })!;

        Assert.False(result);
    }
}

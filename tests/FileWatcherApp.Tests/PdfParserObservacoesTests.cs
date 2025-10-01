using System;
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
}

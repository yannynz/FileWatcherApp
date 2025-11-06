using FileWatcherApp.Services.FileWatcher;
using Xunit;

namespace FileWatcherApp.Tests;

public class FileWatcherNamingTests
{
    [Theory]
    [InlineData("NR 999999 NOVO ajuste.m.DXF", "999999", "NR 999999.m.DXF")]
    [InlineData("NR999998   RETRABALHO.DXF.FCD", "999998", "NR 999998.DXF.FCD")]
    [InlineData("  nr 123456   algo EXTRA .m.dxf  ", "123456", "NR 123456.m.DXF")]
    [InlineData("NR120000.dxf", "120000", "NR 120000.DXF")]
    public void TrySanitizeDobrasName_NormalizesNoiseAndSuffix(string input, string expectedNr, string expectedSanitized)
    {
        var success = FileWatcherNaming.TrySanitizeDobrasName(input, out var nr, out var sanitized);

        Assert.True(success);
        Assert.Equal(expectedNr, nr);
        Assert.Equal(expectedSanitized, sanitized);
    }

    [Theory]
    [InlineData("arquivo_sem_nr.dxf")]
    [InlineData("arquivo qualquer.m.DXF")]
    [InlineData("NR somente numero 777777")]
    public void TrySanitizeDobrasName_RejectsInvalidFormats(string input)
    {
        var success = FileWatcherNaming.TrySanitizeDobrasName(input, out var nr, out var sanitized);

        Assert.False(success);
        Assert.Equal(string.Empty, nr);
        Assert.Equal(string.Empty, sanitized);
    }

    [Fact]
    public void CleanFileName_NormalizesClientAndColor()
    {
        var result = FileWatcherNaming.CleanFileName("nr123456Cliente VERMELHO.cNc");

        Assert.Equal("NR123456CLIENTE_VERMELHO.CNC", result);
    }

    [Fact]
    public void CleanFileName_BlocksReservedWords()
    {
        var result = FileWatcherNaming.CleanFileName("NR123456MODELO_VERMELHO.cnc");

        Assert.Null(result);
    }

    [Fact]
    public void CleanFileName_ReturnsNullForUnexpectedFormat()
    {
        var result = FileWatcherNaming.CleanFileName("NR123456_CLIENTE.CNC");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("NR 123456 Cliente da Silva Vermelho.cNc", "NR123456CLIENTEDASILVA_VERMELHO.CNC")]
    [InlineData("nr123456 Cliente - verde limao aprova.cnc", "NR123456CLIENTE_VERDE_LIMAO.CNC")]
    [InlineData("CL000789 XY-123 Azul Escuro rev2.dxf", "CL000789XY123_AZUL_ESCURO.CNC")]
    [InlineData("NR987654 3M Preto laser final.cnc", "NR9876543M_PRETO.CNC")]
    [InlineData(" laser nr123456 cliente idiotas azul ", "NR123456CLIENTEIDIOTAS_AZUL.CNC")]
    public void CleanFileName_SupportsNoiseAndColorVariations(string input, string expected)
    {
        var result = FileWatcherNaming.CleanFileName(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("NR 123456.m.DXF", true)]
    [InlineData("nr123456.dxf.fcd", true)]
    [InlineData("NR 123456.DXF", false)]
    [InlineData("qualquer_coisa.txt", false)]
    [InlineData("", false)]
    public void HasDobrasSavedSuffix_RecognizesAllowedExtensions(string input, bool expected)
    {
        var result = FileWatcherNaming.HasDobrasSavedSuffix(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("NR 123456.m.DXF", ".m.DXF")]
    [InlineData("NR123456.DXF.FCD", ".DXF.FCD")]
    [InlineData("NR 777777.DXF", ".DXF")]
    [InlineData("qualquer.txt", "")]
    public void TryGetDobrasSuffix_DetectsKnownSuffixes(string input, string expected)
    {
        var success = FileWatcherNaming.TryGetDobrasSuffix(input, out var suffix);

        if (string.IsNullOrEmpty(expected))
        {
            Assert.False(success);
            Assert.Equal(string.Empty, suffix);
        }
        else
        {
            Assert.True(success);
            Assert.Equal(expected, suffix);
        }
    }
}

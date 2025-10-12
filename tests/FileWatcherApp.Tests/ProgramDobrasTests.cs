using System;
using System.Reflection;
using Xunit;

namespace FileMonitor.Tests;

public class ProgramDobrasTests
{
    private static readonly Type ProgramType = typeof(FileMonitor.Program);

    [Theory]
    [InlineData("NR 999999 NOVO ajuste.m.DXF", "999999", "NR 999999.m.DXF")]
    [InlineData("NR999998   RETRABALHO.DXF.FCD", "999998", "NR 999998.DXF.FCD")]
    [InlineData("  nr 123456   algo EXTRA .m.dxf  ", "123456", "NR 123456.m.DXF")]
    public void TrySanitizeDobrasName_NormalizesNoiseAndSuffix(string input, string expectedNr, string expectedSanitized)
    {
        var (success, nr, sanitized) = InvokeSanitize(input);

        Assert.True(success);
        Assert.Equal(expectedNr, nr);
        Assert.Equal(expectedSanitized, sanitized);
    }

    [Theory]
    [InlineData("NR 123456.txt")]
    [InlineData("arquivo qualquer.m.DXF")]
    [InlineData("NR somente numero 777777")]
    public void TrySanitizeDobrasName_RejeitaFormatosInvalidos(string input)
    {
        var (success, nr, sanitized) = InvokeSanitize(input);

        Assert.False(success);
        Assert.Equal(string.Empty, nr);
        Assert.Equal(string.Empty, sanitized);
    }

    [Fact]
    public void CleanFileName_NormalizaClienteEPriority()
    {
        var result = InvokeClean("nr123456Cliente VERMELHO.cNc");

        Assert.Equal("NR123456CLIENTE_VERMELHO.CNC", result);
    }

    [Fact]
    public void CleanFileName_BloqueiaPalavrasReservadas()
    {
        var result = InvokeClean("NR123456MODELO_VERMELHO.cnc");

        Assert.Null(result);
    }

    [Fact]
    public void CleanFileName_RetornaNullParaFormatoInesperado()
    {
        var result = InvokeClean("NR123456_CLIENTE.CNC");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("NR 123456 Cliente da Silva Vermelho.cNc", "NR123456CLIENTEDASILVA_VERMELHO.CNC")]
    [InlineData("nr123456 Cliente - verde limao aprova.cnc", "NR123456CLIENTE_VERDE_LIMAO.CNC")]
    [InlineData("CL000789 XY-123 Azul Escuro rev2.dxf", "CL000789XY123_AZUL_ESCURO.CNC")]
    [InlineData("NR987654 3M Preto laser final.cnc", "NR9876543M_PRETO.CNC")]
    [InlineData(" laser nr123456 cliente idiotas azul ", "NR123456CLIENTE_AZUL.CNC")]
    public void CleanFileName_SuportaRuidoEVariedadesDeCor(string input, string expected)
    {
        var result = InvokeClean(input);

        Assert.Equal(expected, result);
    }

    private static (bool success, string nr, string sanitized) InvokeSanitize(string input)
    {
        var method = ProgramType.GetMethod(
            "TrySanitizeDobrasName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        object?[] args = { input, null, null };
        bool success = (bool)method!.Invoke(null, args)!;
        string nr = args[1] as string ?? string.Empty;
        string sanitized = args[2] as string ?? string.Empty;
        return (success, nr, sanitized);
    }

    private static string? InvokeClean(string input)
    {
        var method = ProgramType.GetMethod(
            "CleanFileName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return method!.Invoke(null, new object?[] { input }) as string;
    }
}

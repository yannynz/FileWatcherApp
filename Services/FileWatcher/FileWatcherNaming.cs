using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileWatcherApp.Services.FileWatcher;

/// <summary>
/// Provides reusable helpers to normalize DXF-related file names and extract OP identifiers.
/// </summary>
public static class FileWatcherNaming
{
    private const int MaxColorTokenSpan = 3;

    private static readonly Regex OrderHeaderRegex = new(
        @"(?<![A-Z0-9])(NR|CL)\s*(\d{4,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TokenRegex = new(
        @"[A-Z0-9À-Ú]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DobrasNumberRegex = new(
        @"\bNR\s*(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> DobrasSuffixNormalization = new(StringComparer.OrdinalIgnoreCase)
    {
        [".M.DXF"] = ".m.DXF",
        [".DXF.FCD"] = ".DXF.FCD",
        [".DXF"] = ".DXF"
    };

    private static readonly HashSet<string> DobrasSavedSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".M.DXF",
        ".DXF.FCD"
    };

    private static readonly string[] ReservedWords =
    {
        "modelo", "borracha", "regua", "macho", "femea", "bloco" 
    };

    private static readonly HashSet<string> TailNoiseTokens = CreateNormalizedSet(
        "LASER",
        "FINAL",
        "OK",
        "CORTE",
        "TESTE",
        "CNC",
        "DXF"
    );

    private static readonly Dictionary<string, string> ColorNormalization = BuildColorNormalization();

    private static readonly Regex OpPrefixRegex = new(
        @"^(NR|CL)\s*(\d{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Attempts to extract a normalized CNC file name from a noisy input.
    /// </summary>
    public static string? CleanFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var upper = trimmed.ToUpperInvariant();
        var upperNoAccents = RemoveAccents(upper);

        foreach (var reserved in ReservedWords)
        {
            var token = reserved.ToUpperInvariant();
            if (upper.Contains(token, StringComparison.Ordinal) ||
                upperNoAccents.Contains(token, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return TryNormalizeOrderFileName(upper, out var normalized) ? normalized : null;
    }

    /// <summary>
    /// Attempts to normalize Dobras file names to the canonical pattern.
    /// </summary>
    public static bool TrySanitizeDobrasName(string? fileName, out string nr, out string sanitizedName)
    {
        nr = string.Empty;
        sanitizedName = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var trimmed = fileName.Trim();
        var upper = trimmed.ToUpperInvariant();

        if (!TryGetDobrasSuffix(trimmed, out var suffix))
        {
            return false;
        }

        var match = DobrasNumberRegex.Match(upper);
        if (!match.Success)
        {
            return false;
        }

        nr = match.Groups[1].Value;
        sanitizedName = $"NR {nr}{suffix}";
        return true;
    }

    /// <summary>
    /// Checks whether a file name uses one of the persisted Dobras suffixes.
    /// </summary>
    public static bool HasDobrasSavedSuffix(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return TryGetDobrasSuffix(fileName, out var suffix) && DobrasSavedSuffixes.Contains(suffix);
    }

    /// <summary>
    /// Attempts to retrieve the normalized Dobras suffix from a file name.
    /// </summary>
    public static bool TryGetDobrasSuffix(string? fileName, out string normalizedSuffix)
    {
        normalizedSuffix = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        foreach (var kvp in DobrasSuffixNormalization)
        {
            if (fileName.EndsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                normalizedSuffix = kvp.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to derive an OP identifier from a normalized file name.
    /// </summary>
    public static bool TryExtractOpId(string? normalizedName, out string? opId)
    {
        opId = null;
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return false;
        }

        var match = OpPrefixRegex.Match(normalizedName);
        if (!match.Success)
        {
            return false;
        }

        opId = $"{match.Groups[1].Value.ToUpperInvariant()}{match.Groups[2].Value}";
        return true;
    }

    private static bool TryNormalizeOrderFileName(string upperName, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(upperName))
        {
            return false;
        }

        var match = OrderHeaderRegex.Match(upperName);
        if (!match.Success)
        {
            return false;
        }

        var prefix = match.Groups[1].Value.ToUpperInvariant();
        var number = match.Groups[2].Value;

        var tailIndex = match.Index + match.Length;
        if (tailIndex >= upperName.Length)
        {
            return false;
        }

        var tail = upperName.Substring(tailIndex);
        var matches = TokenRegex.Matches(tail);
        if (matches.Count == 0)
        {
            return false;
        }

        var tokens = new List<TokenInfo>(matches.Count);
        foreach (Match tokenMatch in matches)
        {
            if (tokenMatch.Success && tokenMatch.Length > 0)
            {
                tokens.Add(new TokenInfo(tokenMatch.Value));
            }
        }

        if (tokens.Count == 0)
        {
            return false;
        }

        if (!TryExtractColor(tokens, out var color, out var colorStartIndex))
        {
            return false;
        }

        if (colorStartIndex <= 0)
        {
            return false;
        }

        var clientBuilder = new StringBuilder();
        for (var i = 0; i < colorStartIndex; i++)
        {
            var token = tokens[i];
            if (IsClientNoise(token.Normalized))
            {
                continue;
            }
            clientBuilder.Append(token.Raw);
        }

        if (clientBuilder.Length == 0)
        {
            return false;
        }

        normalized = $"{prefix}{number}{clientBuilder}_{color}.CNC";
        return true;
    }

    private static bool TryExtractColor(List<TokenInfo> tokens, out string color, out int colorStartIndex)
    {
        color = string.Empty;
        colorStartIndex = -1;

        if (tokens.Count == 0)
        {
            return false;
        }

        var keyBuilder = new StringBuilder();

        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];

            if (IsTailNoise(token.Normalized))
            {
                continue;
            }

            for (var len = Math.Min(MaxColorTokenSpan, i + 1); len >= 1; len--)
            {
                var start = i - len + 1;
                if (start < 0)
                {
                    continue;
                }

                keyBuilder.Clear();
                for (var j = start; j <= i; j++)
                {
                    keyBuilder.Append(tokens[j].Normalized);
                }

                var key = keyBuilder.ToString();
                if (ColorNormalization.TryGetValue(key, out var canonicalColor))
                {
                    color = canonicalColor;
                    colorStartIndex = start;
                    return true;
                }
            }

            if (IsRevisionLikeToken(token.Normalized) || IsNumericToken(token.Normalized))
            {
                continue;
            }

            return false;
        }

        return false;
    }

    private static Dictionary<string, string> BuildColorNormalization()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string color, string? canonical = null)
        {
            if (string.IsNullOrWhiteSpace(color))
            {
                return;
            }

            var normalized = NormalizeToken(color).Replace("_", "");
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            map[normalized] = (canonical ?? color).Replace(' ', '_').Replace('-', '_').ToUpperInvariant();
        }

        Add("BRANCO");
        Add("BRANCO_FRIO", "BRANCO_FRIO");
        Add("BRANCO_QUEBRADO");
        Add("AZUL");
        Add("AZUL_MARINHO");
        Add("AZUL_ESCURO");
        Add("AZUL_CLARO");
        Add("VERMELHO");
        Add("VERDE");
        Add("VERDE_LIMAO");
        Add("AMARELO");
        Add("PRETO");
        Add("PRETO_FOSCO");
        Add("LARANJA");
        Add("ROXO");
        Add("MAGENTA");
        Add("CINZA");
        Add("CINZA_CLARO");
        Add("CINZA_ESCURO");
        Add("PRATA");
        Add("OURO");
        Add("MARROM");
        Add("ROSA");
        Add("BEGE");
        Add("TRANSPARENTE");
        Add("INCOLOR", "TRANSPARENTE");
        Add("NATURAL");
        Add("SEM_COR", "TRANSPARENTE");
        Add("VIOLETA");

        return map;
    }

    private static HashSet<string> CreateNormalizedSet(params string[] tokens)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            var normalized = NormalizeToken(token);
            if (!string.IsNullOrEmpty(normalized))
            {
                set.Add(normalized);
            }
        }

        return set;
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return RemoveAccents(value).ToUpperInvariant();
    }

    private static string RemoveAccents(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsClientNoise(string normalizedToken)
    {
        if (string.IsNullOrEmpty(normalizedToken))
        {
            return true;
        }

        if (TailNoiseTokens.Contains(normalizedToken))
        {
            return true;
        }

        if (normalizedToken.StartsWith("APROV", StringComparison.Ordinal))
        {
            return true;
        }

        if (IsRevisionLikeToken(normalizedToken))
        {
            return true;
        }

        return false;
    }

    private static bool IsTailNoise(string normalizedToken)
    {
        if (string.IsNullOrEmpty(normalizedToken))
        {
            return true;
        }

        if (TailNoiseTokens.Contains(normalizedToken))
        {
            return true;
        }

        if (normalizedToken.StartsWith("APROV", StringComparison.Ordinal))
        {
            return true;
        }

        if (IsRevisionLikeToken(normalizedToken))
        {
            return true;
        }

        if (IsNumericToken(normalizedToken))
        {
            return true;
        }

        return false;
    }

    private static bool IsNumericToken(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRevisionLikeToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        if (token == "REV" || token.StartsWith("REVISAO", StringComparison.Ordinal))
        {
            return true;
        }

        if (token.StartsWith("REV", StringComparison.Ordinal))
        {
            if (token.Length == 3)
            {
                return true;
            }

            return HasOnlyDigits(token, 3);
        }

        if (token.StartsWith("VERSAO", StringComparison.Ordinal) ||
            token.StartsWith("VERSION", StringComparison.Ordinal))
        {
            return true;
        }

        if ((token[0] == 'V' || token[0] == 'R') && token.Length > 1 && HasOnlyDigits(token, 1))
        {
            return true;
        }

        return false;
    }

    private static bool HasOnlyDigits(string value, int startIndex)
    {
        if (string.IsNullOrEmpty(value) || startIndex >= value.Length)
        {
            return false;
        }

        for (var i = startIndex; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private readonly struct TokenInfo
    {
        public TokenInfo(string raw)
        {
            Raw = raw;
            Normalized = NormalizeToken(raw);
        }

        public string Raw { get; }
        public string Normalized { get; }
    }
}

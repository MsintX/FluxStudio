using System.Globalization;

namespace FluxStudio.Services;

public enum FluxTokenKind
{
    Identifier,
    Keyword,
    Type,
    Number,
    String,
    Character,
    Comment,
    Operator,
    Preprocessor,
    Punctuation
}

public sealed record FluxToken(FluxTokenKind Kind, int Start, int Length, int Line, int Column, string Text)
{
    public int End => Start + Length;
}

public sealed class FluxLexResult
{
    public required string Source { get; init; }
    public required IReadOnlyList<FluxToken> Tokens { get; init; }
    public required IReadOnlyList<FluxDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// Lexer for the Flux language specification draft v0.2. This deliberately does
/// not attempt to infer semantic errors that require the actual Flux interpreter.
/// </summary>
public static class FluxLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "var", "new", "auto", "as", "if", "then", "else", "for", "end",
        "break", "next", "return", "public", "private", "protected", "internal",
        "static", "abstract", "sealed", "override", "virtual", "class", "interface",
        "is", "and", "or", "no", "print", "printf", "printfn", "read", "pause",
        "report", "warning", "error", "to", "throw", "try", "catch", "finally", "with",
        "get", "set", "base", "this", "init", "extends", "implements", "true", "false",
        "Null", "NoThing", "NoType", "NaN", "Infinity"
    };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "int", "float", "double", "bool", "string", "char", "void"
    };

    private static readonly HashSet<string> KnownDirectives = new(StringComparer.Ordinal)
    {
        "$replace", "$open", "$copy"
    };

    private static readonly string[] MultiCharacterOperators =
    {
        "as!", "~=", "++", "--", "==", "!=", "<=", ">=", "->", "<-"
    };

    public static IReadOnlySet<string> ReservedKeywords => Keywords;
    public static IReadOnlySet<string> BuiltInTypes => Types;
    public static IReadOnlySet<string> Directives => KnownDirectives;

    public static FluxLexResult Lex(string source)
    {
        source = Normalize(source);
        var tokens = new List<FluxToken>();
        var diagnostics = new List<FluxDiagnostic>();
        var lineStarts = BuildLineStarts(source);
        int i = 0;

        void Add(FluxTokenKind kind, int start, int end)
        {
            var (line, column) = GetLineColumn(lineStarts, start);
            tokens.Add(new FluxToken(kind, start, end - start, line, column, source[start..end]));
        }

        void Diag(string code, int position, string message, string severity = "error")
        {
            var (line, column) = GetLineColumn(lineStarts, position);
            diagnostics.Add(new FluxDiagnostic
            {
                Severity = severity,
                Code = code,
                Line = line,
                Column = column,
                Message = message
            });
        }

        while (i < source.Length)
        {
            char c = source[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (IsLineCommentStart(source, i))
            {
                int start = i;
                while (i < source.Length && source[i] != '\n') i++;
                Add(FluxTokenKind.Comment, start, i);
                continue;
            }

            if (c == '/' && Has(source, i + 1, '*'))
            {
                int start = i;
                int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    i = source.Length;
                    Add(FluxTokenKind.Comment, start, i);
                    Diag("FLX1001", start, "多行注释未闭合；需要使用 */ 结束。 ");
                }
                else
                {
                    i = close + 2;
                    Add(FluxTokenKind.Comment, start, i);
                }
                continue;
            }

            if (c == '"' && Has(source, i + 1, '"') && Has(source, i + 2, '"'))
            {
                int start = i;
                i += 3;
                bool closed = false;
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        // `\"""` embeds three quotes without terminating the string.
                        if (i + 3 < source.Length && source.AsSpan(i + 1, 3).SequenceEqual("\"\"\""))
                        {
                            i += 4;
                            continue;
                        }
                        int escapeStart = i++;
                        if (!ConsumeEscape(source, ref i, out string? escapeError))
                            Diag("FLX1003", escapeStart, escapeError ?? "无效的转义序列。");
                        continue;
                    }
                    if (source[i] == '"' && Has(source, i + 1, '"') && Has(source, i + 2, '"'))
                    {
                        i += 3;
                        closed = true;
                        break;
                    }
                    i++;
                }
                Add(FluxTokenKind.String, start, i);
                if (!closed) Diag("FLX1002", start, "多行字符串未闭合；需要使用三个双引号结束。");
                continue;
            }

            if (c == '"')
            {
                int start = i++;
                bool closed = false;
                while (i < source.Length && source[i] != '\n')
                {
                    if (source[i] == '"')
                    {
                        i++;
                        closed = true;
                        break;
                    }
                    if (source[i] == '\\')
                    {
                        int escapeStart = i;
                        i++;
                        if (!ConsumeEscape(source, ref i, out string? escapeError))
                            Diag("FLX1003", escapeStart, escapeError ?? "无效的转义序列。");
                        continue;
                    }
                    i++;
                }
                Add(FluxTokenKind.String, start, i);
                if (!closed) Diag("FLX1004", start, "普通字符串未闭合；普通字符串不能跨物理行。");
                continue;
            }

            if (c == '\'')
            {
                int start = i++;
                bool closed = false;
                int scalarCount = 0;
                while (i < source.Length && source[i] != '\n')
                {
                    if (source[i] == '\'')
                    {
                        i++;
                        closed = true;
                        break;
                    }
                    if (source[i] == '\\')
                    {
                        int escapeStart = i;
                        i++;
                        if (!ConsumeEscape(source, ref i, out string? escapeError))
                            Diag("FLX1003", escapeStart, escapeError ?? "无效的转义序列。");
                        scalarCount++;
                        continue;
                    }
                    if (char.IsHighSurrogate(source[i]) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1])) i += 2;
                    else i++;
                    scalarCount++;
                }
                Add(FluxTokenKind.Character, start, i);
                if (!closed) Diag("FLX1005", start, "字符字面量未闭合；需要使用单引号结束。");
                else if (scalarCount != 1) Diag("FLX1006", start, "字符字面量必须且只能包含一个 Unicode 标量值。");
                continue;
            }

            // A standalone backslash at the start of a physical line continues
            // the preceding statement; it is not an invalid character.
            if (c == '\\' && IsFirstNonWhitespaceOnLine(source, i) &&
                (i + 1 >= source.Length || char.IsWhiteSpace(source[i + 1])))
            {
                int start = i++;
                Add(FluxTokenKind.Operator, start, i);
                continue;
            }

            if (c == '$' && IsFirstNonWhitespaceOnLine(source, i))
            {
                int start = i;
                while (i < source.Length && source[i] != '\n') i++;
                Add(FluxTokenKind.Preprocessor, start, i);
                string directive = source[start..].Split(' ', '\t', '\r', '\n')[0];
                string directiveText = source[start..i];
                if (!KnownDirectives.Contains(directive))
                    Diag("FLX1007", start, $"未知的 Flux 预处理指令“{directive}”。支持：$replace、$open、$copy。", "warning");
                else
                {
                    bool validArguments = directive switch
                    {
                        "$replace" => System.Text.RegularExpressions.Regex.IsMatch(directiveText,
                            "^\\$replace\\s+[^\\s\"]+\\s+\"(?:\\\\.|[^\"\\\\])*\"\\s*$"),
                        "$open" or "$copy" => System.Text.RegularExpressions.Regex.IsMatch(directiveText,
                            "^\\$(?:open|copy)\\s+\"(?:\\\\.|[^\"\\\\])*\"\\s*$"),
                        _ => false
                    };
                    if (!validArguments)
                        Diag("FLX1008", start, $"预处理指令 {directive} 的参数不完整或格式不正确。");
                }
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                string word = source[start..i];
                FluxTokenKind kind = Types.Contains(word) ? FluxTokenKind.Type
                    : Keywords.Contains(word) ? FluxTokenKind.Keyword
                    : FluxTokenKind.Identifier;
                Add(kind, start, i);
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i++;
                while (i < source.Length && char.IsDigit(source[i])) i++;
                if (i + 1 < source.Length && source[i] == '.' && char.IsDigit(source[i + 1]))
                {
                    i++;
                    while (i < source.Length && char.IsDigit(source[i])) i++;
                }
                if (i < source.Length && (source[i] == 'e' || source[i] == 'E'))
                {
                    int exponent = i++;
                    if (i < source.Length && (source[i] == '+' || source[i] == '-')) i++;
                    int digits = i;
                    while (i < source.Length && char.IsDigit(source[i])) i++;
                    if (digits == i) i = exponent;
                }
                Add(FluxTokenKind.Number, start, i);
                continue;
            }

            string? matchedOperator = MultiCharacterOperators.FirstOrDefault(op => source.AsSpan(i).StartsWith(op, StringComparison.Ordinal));
            if (matchedOperator is not null)
            {
                int start = i;
                i += matchedOperator.Length;
                Add(FluxTokenKind.Operator, start, i);
                continue;
            }

            if ("+-*/%^=!<>".Contains(c))
            {
                int start = i++;
                Add(FluxTokenKind.Operator, start, i);
                continue;
            }

            if ("()[]{}.,;:".Contains(c))
            {
                int start = i++;
                Add(FluxTokenKind.Punctuation, start, i);
                continue;
            }

            Diag("FLX1009", i, $"无法识别的字符“{c}”。");
            int invalidStart = i++;
            Add(FluxTokenKind.Punctuation, invalidStart, i);
        }

        return new FluxLexResult
        {
            Source = source,
            Tokens = tokens,
            Diagnostics = diagnostics.OrderBy(d => d.Line).ThenBy(d => d.Column).ToArray()
        };
    }

    public static string Normalize(string source) => (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    public static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool Has(string source, int index, char value) => index >= 0 && index < source.Length && source[index] == value;

    private static bool IsLineCommentStart(string source, int index)
    {
        if (source[index] != '*' || !Has(source, index + 1, ' ')) return false;
        int lineStart = source.LastIndexOf('\n', Math.Max(0, index - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        for (int p = lineStart; p < index; p++) if (!char.IsWhiteSpace(source[p])) return false;
        return true;
    }

    private static bool IsFirstNonWhitespaceOnLine(string source, int index)
    {
        int lineStart = source.LastIndexOf('\n', Math.Max(0, index - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        for (int p = lineStart; p < index; p++) if (!char.IsWhiteSpace(source[p])) return false;
        return true;
    }

    private static bool ConsumeEscape(string source, ref int index, out string? error)
    {
        error = null;
        if (index >= source.Length) { error = "转义符后缺少转义字符。"; return false; }
        char c = source[index++];
        if (c is '\\' or '"' or '\'' or 'n' or 'r' or 't' or '0') return true;
        if (c == 'u') return ConsumeUnicodeEscape(source, ref index, 4, out error);
        if (c == 'U') return ConsumeUnicodeEscape(source, ref index, 8, out error);
        error = $"未知转义序列“\\{c}”。";
        return false;
    }

    private static bool ConsumeUnicodeEscape(string source, ref int index, int digits, out string? error)
    {
        error = null;
        if (index + digits > source.Length)
        {
            error = $"Unicode 转义需要 {digits} 位十六进制数字。";
            index = source.Length;
            return false;
        }

        ReadOnlySpan<char> span = source.AsSpan(index, digits);
        if (!uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint scalar))
        {
            error = "Unicode 转义包含无效的十六进制数字。";
            index += digits;
            return false;
        }
        index += digits;
        if (scalar > 0x10FFFF || scalar is >= 0xD800 and <= 0xDFFF)
        {
            error = "Unicode 转义必须是有效的 Unicode 标量值，不能是代理项。";
            return false;
        }
        return true;
    }

    private static int[] BuildLineStarts(string source)
    {
        var result = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++) if (source[i] == '\n') result.Add(i + 1);
        return result.ToArray();
    }

    private static (int Line, int Column) GetLineColumn(int[] lineStarts, int position)
    {
        int found = Array.BinarySearch(lineStarts, position);
        int lineIndex = found >= 0 ? found : ~found - 1;
        return (lineIndex + 1, position - lineStarts[lineIndex] + 1);
    }
}

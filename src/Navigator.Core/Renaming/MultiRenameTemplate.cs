using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Navigator.Core.Renaming;

/// <summary>Case conversion applied to the whole new name after the masks and search &amp; replace.</summary>
public enum RenameCase
{
    Unchanged,
    Lower,
    Upper,
    FirstLetterUpper,
    EachWordUpper,
}

/// <summary>Everything the multi-rename dialog lets the user set (Total Commander's Multi-Rename Tool model).</summary>
public sealed record MultiRenameOptions
{
    public string NameMask { get; init; } = "[N]";

    public string ExtensionMask { get; init; } = "[E]";

    /// <summary>Text to find in the new name; several pairs are separated by "|" when <see cref="UseRegex"/> is off.</summary>
    public string Search { get; init; } = string.Empty;

    public string Replace { get; init; } = string.Empty;

    public bool UseRegex { get; init; }

    public bool MatchCase { get; init; }

    public RenameCase Case { get; init; }

    public long CounterStart { get; init; } = 1;

    public long CounterStep { get; init; } = 1;

    public int CounterDigits { get; init; } = 1;
}

/// <summary>One item to rename: its current name plus the facts the masks can refer to.</summary>
public readonly record struct RenameSource(string Name, bool IsDirectory, string? ParentPath, DateTime LastWriteTime);

/// <summary>A mask or pattern the user typed cannot be used. <see cref="Key"/> is a localization key suffix.</summary>
public sealed class MultiRenameTemplateException(string key, string detail) : FormatException(detail)
{
    public string Key { get; } = key;

    public string Detail { get; } = detail;
}

/// <summary>
/// Total Commander-compatible rename masks, compiled once and applied to many items (the preview recomputes on every
/// keystroke, so parsing never happens per item).
/// <para>
/// Placeholders: [N] name without extension, [E] extension, [P] parent folder, [G] grandparent folder — each with an
/// optional range: [N3] (3rd character), [N2-5], [N2,4] (4 characters from the 2nd), [N3-] (3rd to the end),
/// [N-3] / [N-3-] / [N2--2] (negative positions count from the end). [C] counter (start/step/digits from the dialog,
/// or inline: [C10+5:3]). [Y] [y] [M] [D] [d] [h] [m] [s] [t] modification date and time. [U] [L] [F] [n] switch the
/// case of everything that follows (upper, lower, first letter of each word, unchanged). [[] and []] are literal brackets.
/// </para>
/// </summary>
public sealed class MultiRenameTemplate
{
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

    private readonly Token[] _name;
    private readonly Token[] _extension;
    private readonly Regex? _regex;
    private readonly (string Find, string Replace)[] _pairs;
    private readonly StringComparison _comparison;
    private readonly string _replacement;
    private readonly RenameCase _case;
    private readonly MultiRenameOptions _options;

    private MultiRenameTemplate(MultiRenameOptions options)
    {
        _options = options;
        _name = Parse(options.NameMask ?? string.Empty);
        _extension = Parse(options.ExtensionMask ?? string.Empty);
        _case = options.Case;
        _comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _replacement = options.Replace ?? string.Empty;
        _pairs = [];

        var search = options.Search ?? string.Empty;
        if (search.Length == 0)
            return;

        if (options.UseRegex)
        {
            try
            {
                var flags = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                _regex = new Regex(search, flags, RegexBudget);
            }
            catch (ArgumentException invalid)
            {
                throw new MultiRenameTemplateException("InvalidRegex", invalid.Message);
            }

            return;
        }

        var finds = search.Split('|');
        var replaces = _replacement.Split('|');
        var pairs = new List<(string, string)>(finds.Length);
        for (var i = 0; i < finds.Length; i++)
        {
            if (finds[i].Length == 0)
                continue;

            // One replacement for several search terms applies to all of them, like Total Commander.
            var replace = replaces.Length == 1 ? replaces[0] : i < replaces.Length ? replaces[i] : string.Empty;
            pairs.Add((finds[i], replace));
        }

        _pairs = [.. pairs];
    }

    /// <summary>Parses both masks and the search pattern; throws <see cref="MultiRenameTemplateException"/> when one is invalid.</summary>
    public static MultiRenameTemplate Compile(MultiRenameOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CounterDigits is < 1 or > 18)
            throw new MultiRenameTemplateException("InvalidCounter", options.CounterDigits.ToString(CultureInfo.InvariantCulture));
        return new MultiRenameTemplate(options);
    }

    /// <summary>The new name of <paramref name="source"/>, the <paramref name="index"/>-th item (0-based) of the batch.</summary>
    public string Apply(in RenameSource source, int index)
    {
        var name = source.Name ?? string.Empty;
        var dot = source.IsDirectory ? -1 : name.LastIndexOf('.');
        var stem = dot <= 0 ? name : name[..dot];
        var extension = dot <= 0 ? string.Empty : name[(dot + 1)..];

        var context = new Context(stem, extension, source, index);
        var builder = new StringBuilder(name.Length + 16);
        Render(_name, ref context, builder);
        var newStem = builder.ToString();
        builder.Clear();
        Render(_extension, ref context, builder);
        var newExtension = builder.ToString();

        var result = newExtension.Length == 0 ? newStem : string.Concat(newStem, ".", newExtension);
        result = SearchAndReplace(result);
        return ApplyCase(result, _case);
    }

    private string SearchAndReplace(string text)
    {
        if (_regex is not null)
        {
            try
            {
                return _regex.Replace(text, _replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                return text;
            }
        }

        foreach (var (find, replace) in _pairs)
            text = text.Replace(find, replace, _comparison);
        return text;
    }

    // ── Rendering ──

    private struct Context(string stem, string extension, RenameSource source, int index)
    {
        public readonly string Stem = stem;
        public readonly string Extension = extension;
        public readonly RenameSource Source = source;
        public readonly int Index = index;
        public CaseMode Mode = CaseMode.Original;
        private string? _parent;
        private string? _grandparent;

        public string Parent => _parent ??= FolderName(Source.ParentPath);

        public string Grandparent => _grandparent ??= FolderName(Source.ParentPath is { } p ? Path.GetDirectoryName(p) : null);

        private static string FolderName(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    private enum CaseMode
    {
        Original,
        Upper,
        Lower,
        Words,
    }

    private void Render(Token[] tokens, ref Context context, StringBuilder output)
    {
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Literal:
                    Append(output, token.Text!, context.Mode);
                    break;
                case TokenKind.Name:
                    Append(output, token.Range.Cut(context.Stem), context.Mode);
                    break;
                case TokenKind.Extension:
                    Append(output, token.Range.Cut(context.Extension), context.Mode);
                    break;
                case TokenKind.Parent:
                    Append(output, token.Range.Cut(context.Parent), context.Mode);
                    break;
                case TokenKind.Grandparent:
                    Append(output, token.Range.Cut(context.Grandparent), context.Mode);
                    break;
                case TokenKind.Counter:
                    Append(output, Counter(token, context.Index), context.Mode);
                    break;
                case TokenKind.Date:
                    output.Append(context.Source.LastWriteTime.ToString(token.Text, CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Case:
                    context.Mode = token.Case;
                    break;
            }
        }
    }

    private string Counter(in Token token, int index)
    {
        var start = token.CounterStart ?? _options.CounterStart;
        var step = token.CounterStep ?? _options.CounterStep;
        var digits = token.CounterDigits ?? _options.CounterDigits;
        var value = start + (index * step);
        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        return value < 0 ? "-" + text : text;
    }

    private static void Append(StringBuilder output, string text, CaseMode mode)
    {
        switch (mode)
        {
            case CaseMode.Upper:
                output.Append(text.ToUpperInvariant());
                break;
            case CaseMode.Lower:
                output.Append(text.ToLowerInvariant());
                break;
            case CaseMode.Words:
                foreach (var c in text)
                {
                    var startsWord = output.Length == 0 || !char.IsLetterOrDigit(output[^1]);
                    output.Append(startsWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                }

                break;
            default:
                output.Append(text);
                break;
        }
    }

    internal static string ApplyCase(string text, RenameCase mode)
    {
        switch (mode)
        {
            case RenameCase.Lower:
                return text.ToLowerInvariant();
            case RenameCase.Upper:
                return text.ToUpperInvariant();
            case RenameCase.FirstLetterUpper:
                return text.Length == 0 ? text : string.Concat(char.ToUpperInvariant(text[0]).ToString(), text[1..].ToLowerInvariant());
            case RenameCase.EachWordUpper:
            {
                var builder = new StringBuilder(text.Length);
                Append(builder, text, CaseMode.Words);
                return builder.ToString();
            }

            default:
                return text;
        }
    }

    // ── Parsing ──

    private enum TokenKind
    {
        Literal,
        Name,
        Extension,
        Parent,
        Grandparent,
        Counter,
        Date,
        Case,
    }

    private readonly record struct Token(TokenKind Kind, string? Text = null, CharRange Range = default, CaseMode Case = CaseMode.Original,
        long? CounterStart = null, long? CounterStep = null, int? CounterDigits = null);

    /// <summary>A character range of a placeholder; 1-based, negative positions count from the end. Default = whole text.</summary>
    private readonly record struct CharRange(bool IsSet, int Start, int? End, int? Length, bool ToEnd)
    {
        public string Cut(string text)
        {
            if (!IsSet || text.Length == 0)
                return text;

            var first = Resolve(Start, text.Length);
            int last; // inclusive
            if (Length is { } length)
                last = first + length - 1;
            else if (End is { } end)
                last = Resolve(end, text.Length);
            else if (ToEnd)
                last = text.Length - 1;
            else
                last = first;

            first = Math.Max(first, 0);
            last = Math.Min(last, text.Length - 1);
            return first > last || first >= text.Length ? string.Empty : text.Substring(first, last - first + 1);
        }

        private static int Resolve(int position, int length) => position > 0 ? position - 1 : length + position;
    }

    private static Token[] Parse(string mask)
    {
        var tokens = new List<Token>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < mask.Length)
        {
            var c = mask[i];
            if (c != '[')
            {
                literal.Append(c);
                i++;
                continue;
            }

            // Literal brackets: "[[]" and "[]]".
            if (i + 2 < mask.Length && mask[i + 2] == ']' && mask[i + 1] is '[' or ']')
            {
                literal.Append(mask[i + 1]);
                i += 3;
                continue;
            }

            var close = mask.IndexOf(']', i + 1);
            if (close < 0)
                throw new MultiRenameTemplateException("Unclosed", mask[i..]);

            if (literal.Length > 0)
            {
                tokens.Add(new Token(TokenKind.Literal, literal.ToString()));
                literal.Clear();
            }

            tokens.Add(ParsePlaceholder(mask.AsSpan(i + 1, close - i - 1)));
            i = close + 1;
        }

        if (literal.Length > 0)
            tokens.Add(new Token(TokenKind.Literal, literal.ToString()));
        return [.. tokens];
    }

    private static Token ParsePlaceholder(ReadOnlySpan<char> body)
    {
        var shown = "[" + body.ToString() + "]";
        if (body.Length == 0)
            throw new MultiRenameTemplateException("UnknownPlaceholder", shown);

        var rest = body[1..];
        switch (body[0])
        {
            case 'N':
                return new Token(TokenKind.Name, Range: ParseRange(rest, shown));
            case 'E':
                return new Token(TokenKind.Extension, Range: ParseRange(rest, shown));
            case 'P':
                return new Token(TokenKind.Parent, Range: ParseRange(rest, shown));
            case 'G':
                return new Token(TokenKind.Grandparent, Range: ParseRange(rest, shown));
            case 'C':
                return ParseCounter(rest, shown);
        }

        if (body.Length != 1)
            throw new MultiRenameTemplateException("UnknownPlaceholder", shown);

        return body[0] switch
        {
            'Y' => new Token(TokenKind.Date, "yyyy"),
            'y' => new Token(TokenKind.Date, "yy"),
            'M' => new Token(TokenKind.Date, "MM"),
            'D' => new Token(TokenKind.Date, "dd"),
            'd' => new Token(TokenKind.Date, "yyyy-MM-dd"),
            'h' => new Token(TokenKind.Date, "HH"),
            'm' => new Token(TokenKind.Date, "mm"),
            's' => new Token(TokenKind.Date, "ss"),
            't' => new Token(TokenKind.Date, "HH.mm.ss"),
            'U' => new Token(TokenKind.Case, Case: CaseMode.Upper),
            'L' => new Token(TokenKind.Case, Case: CaseMode.Lower),
            'F' => new Token(TokenKind.Case, Case: CaseMode.Words),
            'n' => new Token(TokenKind.Case, Case: CaseMode.Original),
            _ => throw new MultiRenameTemplateException("UnknownPlaceholder", shown),
        };
    }

    private static CharRange ParseRange(ReadOnlySpan<char> text, string shown)
    {
        if (text.Length == 0)
            return default;

        var position = 0;
        if (!TryReadInt(text, ref position, out var start) || start == 0)
            throw new MultiRenameTemplateException("InvalidRange", shown);

        if (position == text.Length)
            return new CharRange(true, start, null, null, false);

        var separator = text[position++];
        if (separator == ',')
        {
            if (!TryReadInt(text, ref position, out var length) || length < 0 || position != text.Length)
                throw new MultiRenameTemplateException("InvalidRange", shown);
            return new CharRange(true, start, null, length, false);
        }

        if (separator != '-')
            throw new MultiRenameTemplateException("InvalidRange", shown);
        if (position == text.Length)
            return new CharRange(true, start, null, null, true);
        if (!TryReadInt(text, ref position, out var end) || end == 0 || position != text.Length)
            throw new MultiRenameTemplateException("InvalidRange", shown);
        return new CharRange(true, start, end, null, false);
    }

    /// <summary>[C], [C10], [C10+5], [C+5:3], [C:3] — start, step and digits each optional.</summary>
    private static Token ParseCounter(ReadOnlySpan<char> text, string shown)
    {
        long? start = null, step = null;
        int? digits = null;
        var position = 0;
        if (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '-'))
        {
            if (!TryReadLong(text, ref position, out var value))
                throw new MultiRenameTemplateException("InvalidCounter", shown);
            start = value;
        }

        if (position < text.Length && text[position] == '+')
        {
            position++;
            if (!TryReadLong(text, ref position, out var value))
                throw new MultiRenameTemplateException("InvalidCounter", shown);
            step = value;
        }

        if (position < text.Length && text[position] == ':')
        {
            position++;
            if (!TryReadLong(text, ref position, out var value) || value is < 1 or > 18)
                throw new MultiRenameTemplateException("InvalidCounter", shown);
            digits = (int)value;
        }

        if (position != text.Length)
            throw new MultiRenameTemplateException("InvalidCounter", shown);
        return new Token(TokenKind.Counter, CounterStart: start, CounterStep: step, CounterDigits: digits);
    }

    private static bool TryReadInt(ReadOnlySpan<char> text, ref int position, out int value)
    {
        value = 0;
        if (!TryReadLong(text, ref position, out var wide) || wide is < -100_000 or > 100_000)
            return false;
        value = (int)wide;
        return true;
    }

    private static bool TryReadLong(ReadOnlySpan<char> text, ref int position, out long value)
    {
        var begin = position;
        if (position < text.Length && text[position] == '-')
            position++;
        var digitsStart = position;
        while (position < text.Length && char.IsAsciiDigit(text[position]) && position - digitsStart < 18)
            position++;
        if (position == digitsStart)
        {
            position = begin;
            value = 0;
            return false;
        }

        return long.TryParse(text[begin..position], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}

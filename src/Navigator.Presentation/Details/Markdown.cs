using System.Text;

namespace Navigator.Presentation.Details;

[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strikethrough = 8,
    Link = 16,
}

/// <summary>A run of text with one style (links carry their target).</summary>
public sealed record MarkdownSpan(string Text, MarkdownStyle Style, string? Url = null);

public enum MarkdownBlockKind
{
    Heading,
    Paragraph,
    Code,
    Quote,
    ListItem,
    Rule,
    Table,
}

/// <summary>
/// One block of rendered Markdown. <see cref="Spans"/> holds the inline text (headings, paragraphs, quotes, list
/// items); <see cref="Code"/> the verbatim text of code blocks; <see cref="Rows"/> the cells of tables.
/// </summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, IReadOnlyList<MarkdownSpan> Spans)
{
    /// <summary>Heading level (1–6); nesting depth of list items and quotes (0 = top level).</summary>
    public int Level { get; init; }

    /// <summary>"1." for ordered list items, "•" for bullets, "☐"/"☑" for task items.</summary>
    public string? Marker { get; init; }

    public string? Code { get; init; }

    public string? Language { get; init; }

    /// <summary>Table rows; the first row is the header.</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownSpan>>>? Rows { get; init; }
}

/// <summary>
/// A small CommonMark/GFM subset for the preview pane: ATX and setext headings, paragraphs, fenced and indented code,
/// block quotes, bullet/ordered/task lists, thematic breaks and pipe tables, with emphasis, strong, strikethrough,
/// code spans, links, images (as their alt text) and autolinks inline. It never executes or fetches anything (no HTML,
/// no remote images) and runs in linear time over the (already size-capped) preview text.
/// </summary>
public static class MarkdownParser
{
    private const int MaxBlocks = 2000;

    public static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, ParseInline(string.Join(' ', paragraph))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length && blocks.Count < MaxBlocks; i++)
        {
            var line = lines[i].Replace("\t", "    ", StringComparison.Ordinal);
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // Fenced code: ``` or ~~~ until the same fence.
            if (indent < 4 && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal); i++)
                    code.Append(lines[i]).Append('\n');
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, []) { Code = code.ToString().TrimEnd('\n'), Language = language.Length == 0 ? null : language });
                continue;
            }

            // Indented code (not inside a paragraph, where indentation is a continuation).
            if (indent >= 4 && paragraph.Count == 0 && (blocks.Count == 0 || blocks[^1].Kind != MarkdownBlockKind.ListItem))
            {
                var code = new StringBuilder();
                for (; i < lines.Length && (lines[i].Trim().Length == 0 || lines[i].Replace("\t", "    ", StringComparison.Ordinal).StartsWith("    ", StringComparison.Ordinal)); i++)
                    code.Append(lines[i].Length >= 4 ? lines[i].Replace("\t", "    ", StringComparison.Ordinal)[4..] : string.Empty).Append('\n');
                i--;
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, []) { Code = code.ToString().TrimEnd('\n') });
                continue;
            }

            // Setext heading: a paragraph line underlined with === or ---.
            if (paragraph.Count > 0 && indent < 4 && IsUnderline(trimmed, out var setextLevel))
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, ParseInline(string.Join(' ', paragraph))) { Level = setextLevel });
                paragraph.Clear();
                continue;
            }

            if (indent < 4 && IsRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, []));
                continue;
            }

            if (indent < 4 && trimmed[0] == '#')
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                    level++;
                if (level <= 6 && (level == trimmed.Length || trimmed[level] == ' '))
                {
                    FlushParagraph();
                    var content = trimmed[level..].Trim().TrimEnd('#').TrimEnd();
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, ParseInline(content)) { Level = level });
                    continue;
                }
            }

            if (trimmed[0] == '>')
            {
                FlushParagraph();
                var depth = 0;
                var quoted = trimmed;
                while (quoted.StartsWith('>'))
                {
                    depth++;
                    quoted = quoted[1..].TrimStart();
                }

                // Following quote lines without a blank line in between belong to the same block.
                var content = new List<string> { quoted };
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('>'))
                    content.Add(lines[++i].TrimStart().TrimStart('>').TrimStart());
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, ParseInline(string.Join(' ', content.Where(c => c.Length > 0)))) { Level = depth - 1 });
                continue;
            }

            if (TryListItem(trimmed, out var marker, out var itemText))
            {
                FlushParagraph();
                // Lazy continuation lines (indented, not a new item) join the item.
                var content = new StringBuilder(itemText);
                while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && lines[i + 1].StartsWith("  ", StringComparison.Ordinal)
                       && !TryListItem(lines[i + 1].TrimStart(), out _, out _))
                    content.Append(' ').Append(lines[++i].Trim());
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.ListItem, ParseInline(content.ToString())) { Level = Math.Min(indent / 2, 8), Marker = marker });
                continue;
            }

            if (indent < 4 && trimmed.Contains('|', StringComparison.Ordinal) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownSpan>>> { SplitRow(trimmed) };
                for (i += 2; i < lines.Length && lines[i].Trim().Length > 0 && lines[i].Contains('|', StringComparison.Ordinal) && rows.Count < 500; i++)
                    rows.Add(SplitRow(lines[i].Trim()));
                i--;
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Table, []) { Rows = rows });
                continue;
            }

            paragraph.Add(trimmed.TrimEnd());
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>Inline markup of one block.</summary>
    public static IReadOnlyList<MarkdownSpan> ParseInline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<MarkdownSpan>();
        var buffer = new StringBuilder();
        var style = MarkdownStyle.None;

        void Flush()
        {
            if (buffer.Length == 0)
                return;
            spans.Add(new MarkdownSpan(buffer.ToString(), style));
            buffer.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
            {
                buffer.Append(text[++i]);
                continue;
            }

            if (c == '`')
            {
                var ticks = 1;
                while (i + ticks < text.Length && text[i + ticks] == '`')
                    ticks++;
                var fence = new string('`', ticks);
                var end = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    spans.Add(new MarkdownSpan(text[(i + ticks)..end].Trim(), style | MarkdownStyle.Code));
                    i = end + ticks - 1;
                    continue;
                }
            }

            // [text](url) and ![alt](url)
            var isImage = c == '!' && i + 1 < text.Length && text[i + 1] == '[';
            if (c == '[' || isImage)
            {
                var open = isImage ? i + 1 : i;
                var close = text.IndexOf(']', open + 1);
                if (close > open && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var urlEnd = text.IndexOf(')', close + 2);
                    if (urlEnd > close)
                    {
                        Flush();
                        var label = text[(open + 1)..close];
                        var url = text[(close + 2)..urlEnd].Trim().Split(' ')[0].Trim('<', '>');
                        foreach (var inner in ParseInline(label.Length == 0 ? url : label))
                            spans.Add(inner with { Style = inner.Style | style | MarkdownStyle.Link, Url = url });
                        i = urlEnd;
                        continue;
                    }
                }
            }

            // <https://autolink>
            if (c == '<')
            {
                var end = text.IndexOf('>', i + 1);
                if (end > i && text.AsSpan(i + 1, end - i - 1) is var inside && (inside.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || inside.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || inside.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) && !inside.Contains(' '))
                {
                    Flush();
                    var url = inside.ToString();
                    spans.Add(new MarkdownSpan(url, style | MarkdownStyle.Link, url));
                    i = end;
                    continue;
                }
            }

            if (c is '*' or '_' or '~')
            {
                var run = 1;
                while (i + run < text.Length && text[i + run] == c)
                    run++;
                var flag = c == '~' ? (run >= 2 ? MarkdownStyle.Strikethrough : MarkdownStyle.None)
                    : run >= 3 ? MarkdownStyle.Bold | MarkdownStyle.Italic
                    : run == 2 ? MarkdownStyle.Bold
                    : MarkdownStyle.Italic;

                // Intra-word underscores (snake_case) are literal; delimiters must open with a closer ahead or close an open style.
                var intraWord = c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]) && i + run < text.Length && char.IsLetterOrDigit(text[i + run]);
                var delimiter = new string(c, Math.Min(run, 3));
                var closes = (style & flag) == flag && flag != MarkdownStyle.None;
                var opens = !closes && flag != MarkdownStyle.None && i + run < text.Length && !char.IsWhiteSpace(text[i + run])
                            && text.IndexOf(delimiter, i + run, Math.Min(1024, text.Length - i - run), StringComparison.Ordinal) > i + run;
                if (!intraWord && (closes || opens))
                {
                    Flush();
                    style = closes ? style & ~flag : style | flag;
                    i += run - 1;
                    continue;
                }

                buffer.Append(text, i, run);
                i += run - 1;
                continue;
            }

            buffer.Append(c);
        }

        Flush();
        return spans;
    }

    private static bool IsUnderline(string line, out int level)
    {
        level = line[0] == '=' ? 1 : 2;
        var trimmed = line.TrimEnd();
        return trimmed.Length >= 1 && trimmed[0] is '=' or '-' && trimmed.All(ch => ch == trimmed[0]) && (trimmed[0] == '=' || trimmed.Length >= 2);
    }

    private static bool IsRule(string line)
    {
        var compact = line.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Length >= 3 && compact[0] is '-' or '*' or '_' && compact.All(ch => ch == compact[0]);
    }

    private static bool TryListItem(string line, out string marker, out string text)
    {
        marker = string.Empty;
        text = string.Empty;
        if (line.Length >= 2 && line[0] is '-' or '*' or '+' && line[1] == ' ')
        {
            text = line[2..].TrimStart();
            marker = "•";
        }
        else
        {
            var digits = 0;
            while (digits < line.Length && digits < 9 && char.IsAsciiDigit(line[digits]))
                digits++;
            if (digits == 0 || digits + 1 >= line.Length || line[digits] is not ('.' or ')') || line[digits + 1] != ' ')
                return false;
            marker = line[..digits] + ".";
            text = line[(digits + 2)..].TrimStart();
        }

        if (text.StartsWith("[ ] ", StringComparison.Ordinal) || text == "[ ]")
        {
            marker = "☐";
            text = text.Length > 3 ? text[4..] : string.Empty;
        }
        else if (text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) || text.Equals("[x]", StringComparison.OrdinalIgnoreCase))
        {
            marker = "☑";
            text = text.Length > 3 ? text[4..] : string.Empty;
        }

        return true;
    }

    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.Contains('-', StringComparison.Ordinal) || !trimmed.Contains('|', StringComparison.Ordinal) && !trimmed.StartsWith(':'))
            return false;
        return trimmed.All(ch => ch is '|' or '-' or ':' or ' ');
    }

    private static List<IReadOnlyList<MarkdownSpan>> SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
            trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
            trimmed = trimmed[..^1];
        return [.. trimmed.Split('|').Select(cell => ParseInline(cell.Trim()))];
    }
}

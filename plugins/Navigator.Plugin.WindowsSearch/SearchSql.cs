using System.Globalization;
using System.Text;
using Navigator.Plugins;

namespace Navigator.Plugin.WindowsSearch;

/// <summary>
/// Windows Search SQL for a pane search. Columns are always, in this order: path, size, modified, created, attributes
/// (<see cref="IndexQuery"/> binds them by position).
/// <list type="bullet">
/// <item>Scope: <c>SCOPE</c> (the folder and everything below) or <c>DIRECTORY</c> (the folder only).</item>
/// <item>Names: a plain text matches anywhere in the name (<c>LIKE '%text%'</c>); <c>*</c> and <c>?</c> make it a
/// pattern for the whole name, as in Navigator's own search.</item>
/// <item>Contents: <c>CONTAINS(*, '"word*"')</c>, the full-text index (also matches words starting with the text).</item>
/// </list>
/// </summary>
internal static class SearchSql
{
    public const string Columns = "System.ItemPathDisplay, System.Size, System.DateModified, System.DateCreated, System.FileAttributes";

    public static string Build(PluginSearchRequest request, int limit)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = new StringBuilder(256);
        sql.Append("SELECT TOP ").Append(Math.Max(1, limit).ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Columns)
            .Append(" FROM SystemIndex WHERE ")
            .Append(request.Recursive ? "SCOPE='" : "DIRECTORY='").Append(Literal(ScopeUrl(request.Root))).Append('\'');

        if (request.Text.Length > 0)
            sql.Append(" AND System.FileName LIKE '").Append(Literal(LikePattern(request.Text))).Append('\'');

        if (request.ContentText is { Length: > 0 } content)
            sql.Append(" AND CONTAINS(*, '\"").Append(Literal(FullTextTerm(content))).Append("*\"')");

        return sql.ToString();
    }

    /// <summary>A query that only asks whether the index holds anything below <paramref name="root"/>.</summary>
    public static string Probe(string root)
        => "SELECT TOP 1 " + Columns + " FROM SystemIndex WHERE SCOPE='" + Literal(ScopeUrl(root)) + "'";

    /// <summary>"C:\Users\me" → "file:C:/Users/me/" (the form the index stores; the slash keeps "me2" out of scope).</summary>
    internal static string ScopeUrl(string root)
    {
        var path = root.Replace('\\', '/');
        if (!path.EndsWith('/'))
            path += "/";
        return "file:" + path;
    }

    /// <summary>LIKE pattern: '*' and '?' are wildcards; LIKE's own '%', '_' and '[' are matched literally.</summary>
    internal static string LikePattern(string text)
    {
        var wildcard = text.Contains('*', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal);
        var pattern = new StringBuilder(text.Length + 4);
        if (!wildcard)
            pattern.Append('%');
        foreach (var c in text)
        {
            switch (c)
            {
                case '*' when wildcard:
                    pattern.Append('%');
                    break;
                case '?' when wildcard:
                    pattern.Append('_');
                    break;
                case '%' or '_' or '[':
                    pattern.Append('[').Append(c).Append(']');
                    break;
                default:
                    pattern.Append(c);
                    break;
            }
        }

        if (!wildcard)
            pattern.Append('%');
        return pattern.ToString();
    }

    /// <summary>A phrase for CONTAINS: double quotes cannot be escaped there, so they are dropped.</summary>
    internal static string FullTextTerm(string text) => text.Replace("\"", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>A SQL string literal's content: single quotes doubled.</summary>
    internal static string Literal(string text) => text.Replace("'", "''", StringComparison.Ordinal);
}

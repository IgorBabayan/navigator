using Navigator.Core.IO;

namespace Navigator.Plugins;

/// <summary>A search the user started in a pane (the search box or the omnibar).</summary>
/// <param name="Root">Local folder searched.</param>
/// <param name="Text">What to look for in names: a substring, or a pattern with <c>*</c> and <c>?</c>.</param>
/// <param name="ContentText">Text to look for inside files; null for a name search.</param>
/// <param name="Recursive">Include subfolders.</param>
/// <param name="IncludeHidden">Include hidden and system items.</param>
/// <param name="MaxResults">Stop after this many results.</param>
public sealed record PluginSearchRequest(string Root, string Text, string? ContentText, bool Recursive, bool IncludeHidden, int MaxResults);

/// <summary>
/// Answers pane searches from an index instead of walking folders (plugin API 1.3), such as the Windows Search index.
/// The first provider that accepts a request serves it; otherwise Navigator's own search runs. A provider that fails
/// before it produced any result is replaced by Navigator's own search for that request.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Stable id, unique across plugins.</summary>
    string Id { get; }

    /// <summary>
    /// Whether this provider can answer <paramref name="request"/> completely (e.g. the folder is indexed). Called off the
    /// UI thread before every search; answer from a cache when possible. Waited for at most a second.
    /// </summary>
    ValueTask<bool> CanSearchAsync(PluginSearchRequest request, CancellationToken cancellationToken);

    /// <summary>Results in batches; local entries (<see cref="FileEntry.IsRemote"/> false). Runs off the UI thread.</summary>
    IAsyncEnumerable<IReadOnlyList<FileEntry>> SearchAsync(PluginSearchRequest request, CancellationToken cancellationToken);
}

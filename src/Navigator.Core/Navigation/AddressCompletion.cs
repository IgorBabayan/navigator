using Navigator.Core.IO;
using Navigator.Core.Sorting;

namespace Navigator.Core.Navigation;

/// <summary>
/// The part of a typed address that is already a folder (<see cref="ParentText"/>, exactly as typed, including its
/// trailing separator) and the partial child name being typed after it (<see cref="Prefix"/>).
/// </summary>
public readonly record struct AddressInput(string ParentText, string Prefix);

/// <summary>
/// Address bar autocompletion: splits typed input into "folder + partial name" and lists the sub-folders of that
/// folder whose names start with the partial name. Enumeration is bounded and never follows links.
/// </summary>
public static class AddressCompletion
{
    /// <summary>Upper bound of directory entries inspected per request, so huge folders cannot stall completion.</summary>
    public const int MaxScannedEntries = 20_000;

    /// <summary>
    /// Splits typed input at its last separator: "C:\Us" → ("C:\", "Us"), "~/Doc" → ("~/", "Doc"), "/" → ("/", ""),
    /// "Doc" → ("", "Doc"). A bare Windows drive ("C:") is treated as its root ("C:\", "").
    /// </summary>
    public static AddressInput Split(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.TrimStart();
        if (text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && OperatingSystem.IsWindows())
            return new AddressInput(text + Path.DirectorySeparatorChar, string.Empty);

        var last = text.AsSpan().LastIndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return last < 0
            ? new AddressInput(string.Empty, text)
            : new AddressInput(text[..(last + 1)], text[(last + 1)..]);
    }

    /// <summary>
    /// Resolves the folder part of typed input to an absolute directory: absolute paths, "~", environment variables,
    /// file:// URIs and paths relative to <paramref name="currentDirectory"/>. Returns null when it cannot be resolved.
    /// </summary>
    public static string? ResolveParent(AddressInput input, string homeDirectory, string? currentDirectory)
    {
        if (input.ParentText.Length == 0)
            return currentDirectory;

        var resolved = PathUtil.ExpandUserInput(input.ParentText, homeDirectory);
        if (resolved is null && currentDirectory is not null)
        {
            try
            {
                resolved = PathUtil.ExpandUserInput(Path.Combine(currentDirectory, input.ParentText), homeDirectory);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Names of sub-folders of <paramref name="directory"/> starting with <paramref name="prefix"/> (case-insensitive),
    /// in natural order. Inaccessible or missing folders yield an empty list. Symbolic links to folders are listed
    /// (they are valid navigation targets) but never traversed.
    /// </summary>
    public static IReadOnlyList<string> FindFolders(string directory, string prefix, bool includeHidden, int maxResults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(prefix);
        if (maxResults <= 0)
            return [];

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = includeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };

        var names = new List<string>();
        var scanned = 0;
        try
        {
            foreach (var path in Directory.EnumerateDirectories(directory, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaxScannedEntries)
                    break;

                var name = Path.GetFileName(path);
                if (!includeHidden && name.StartsWith('.') && !OperatingSystem.IsWindows())
                    continue;
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // Completion is best effort: a missing, offline or protected folder simply has no suggestions.
        }

        names.Sort(NaturalStringComparer.Instance);
        if (names.Count > maxResults)
            names.RemoveRange(maxResults, names.Count - maxResults);
        return names;
    }

    /// <summary>
    /// Adds <paramref name="entry"/> to the front of a most-recent-first history, removing an older duplicate
    /// (compared with the platform's path rules) and trimming the list to <paramref name="capacity"/>.
    /// </summary>
    public static void Remember(List<string> history, string entry, int capacity)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(entry) || capacity <= 0)
            return;

        history.RemoveAll(h => PathUtil.Comparer.Equals(h, entry));
        history.Insert(0, entry);
        if (history.Count > capacity)
            history.RemoveRange(capacity, history.Count - capacity);
    }
}

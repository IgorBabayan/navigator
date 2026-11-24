using System.IO.Enumeration;

namespace Navigator.Core.IO;

public readonly record struct FolderStatistics(long TotalBytes, int FileCount, int FolderCount, bool IsPartial);

/// <summary>Recursive folder size (Properties, "View size", optional size column). Never follows links.</summary>
public static class FolderStatisticsCalculator
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static FolderStatistics Calculate(string path, IProgress<FolderStatistics>? progress = null, CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        int files = 0, folders = 0;
        var partial = false;
        var lastReport = Environment.TickCount64;

        try
        {
            var enumerable = new FileSystemEnumerable<(bool IsDirectory, long Length)>(
                path,
                static (ref FileSystemEntry e) => (e.IsDirectory, e.IsDirectory || (e.Attributes & FileAttributes.ReparsePoint) != 0 ? 0 : e.Length),
                Options)
            {
                ShouldRecursePredicate = static (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
            };

            foreach (var (isDirectory, length) in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isDirectory)
                {
                    folders++;
                }
                else
                {
                    files++;
                    bytes += length;
                }

                if (progress is not null && Environment.TickCount64 - lastReport > 150)
                {
                    progress.Report(new FolderStatistics(bytes, files, folders, IsPartial: true));
                    lastReport = Environment.TickCount64;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            partial = true;
        }

        return new FolderStatistics(bytes, files, folders, partial);
    }
}

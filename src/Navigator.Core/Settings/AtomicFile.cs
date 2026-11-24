using System.Text;

namespace Navigator.Core.Settings;

/// <summary>
/// Crash-safe small-file writes: write a temporary sibling, flush it to disk, keep the previous
/// version as ".bak", then atomically rename over the target. A power loss leaves either the old
/// or the new file, never a truncated one.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, bool keepBackup = true)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (keepBackup && File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

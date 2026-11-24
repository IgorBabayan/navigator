using System.Diagnostics;
using System.Globalization;
using Navigator.Core.IO;
using Navigator.Core.Search;
using Navigator.Core.Sorting;

// Usage: dotnet run -c Release --project tools/Navigator.PerfProbe [-- <runs>]
// Creates flat folders of empty files under the temp directory (reused between runs) and prints the
// median of N runs (default 5) for each measured path, plus the process working set.
var runs = args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 5;

Console.WriteLine($".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {Environment.ProcessorCount} logical CPUs · median of {runs} runs");
foreach (var count in new[] { 10_000, 100_000 })
{
    var folder = Path.Combine(Path.GetTempPath(), "navigator-probe", count.ToString(CultureInfo.InvariantCulture));
    if (!Directory.Exists(folder) || Directory.GetFiles(folder).Length != count)
    {
        Directory.CreateDirectory(folder);
        for (var i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(folder, $"file {i} report.txt"), []);
    }

    DirectoryEnumerator.List(folder, EnumerationFilter.Default); // warm-up (JIT + OS directory cache)
    List<double> first = [], full = [], sort = [], search = [];
    for (var run = 0; run < runs; run++)
    {
        var watch = Stopwatch.StartNew();
        await foreach (var _ in DirectoryEnumerator.EnumerateBatchesAsync(folder, EnumerationFilter.Default))
        {
            first.Add(watch.Elapsed.TotalMilliseconds);
            break;
        }

        watch.Restart();
        var entries = DirectoryEnumerator.List(folder, EnumerationFilter.Default);
        full.Add(watch.Elapsed.TotalMilliseconds);

        watch.Restart();
        EntrySorter.Sort(entries, SortOptions.Default);
        sort.Add(watch.Elapsed.TotalMilliseconds);

        watch.Restart();
        var found = 0;
        await foreach (var batch in FileSearchEngine.SearchAsync(new SearchQuery { Root = folder, Text = "99", MaxResults = int.MaxValue }))
            found += batch.Count;
        search.Add(watch.Elapsed.TotalMilliseconds);
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{count,7} entries | first batch {Median(first),7:F2} ms | full listing {Median(full),7:F1} ms | natural sort {Median(sort),7:F1} ms | search {Median(search),7:F1} ms"));
}

Console.WriteLine($"Process working set after probe: {Environment.WorkingSet / 1024 / 1024} MB");

static double Median(List<double> values)
{
    values.Sort();
    return values[values.Count / 2];
}

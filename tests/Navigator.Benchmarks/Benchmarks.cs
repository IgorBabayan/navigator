using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Navigator.Core.Caching;
using Navigator.Core.IO;
using Navigator.Core.Search;
using Navigator.Core.Sorting;

namespace Navigator.Benchmarks;

public static class Program
{
    // dotnet run -c Release --project tests/Navigator.Benchmarks -- --filter '*'
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>Creates a flat folder with N files once per benchmark class (in the temp directory).</summary>
public abstract class FolderFixture
{
    protected string Folder { get; private set; } = string.Empty;

    [Params(10_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void CreateFolder()
    {
        Folder = Path.Combine(Path.GetTempPath(), "navigator-bench", Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(Folder) && Directory.GetFiles(Folder).Length == Count)
            return;
        Directory.CreateDirectory(Folder);
        for (var i = 0; i < Count; i++)
            File.WriteAllBytes(Path.Combine(Folder, $"file {i} report.txt"), []);
    }
}

[MemoryDiagnoser]
public class EnumerationBenchmarks : FolderFixture
{
    [Benchmark(Description = "First visible batch")]
    public async Task<int> FirstBatch()
    {
        await foreach (var batch in DirectoryEnumerator.EnumerateBatchesAsync(Folder, EnumerationFilter.Default))
            return batch.Count;
        return 0;
    }

    [Benchmark(Description = "Full listing")]
    public int FullListing() => DirectoryEnumerator.List(Folder, EnumerationFilter.Default).Count;

    [Benchmark(Description = "Full listing + natural sort")]
    public int ListAndSort()
    {
        var entries = DirectoryEnumerator.List(Folder, EnumerationFilter.Default);
        EntrySorter.Sort(entries, SortOptions.Default);
        return entries.Count;
    }

    [Benchmark(Description = "Search (substring, recursive)")]
    public async Task<int> Search()
    {
        var found = 0;
        await foreach (var batch in FileSearchEngine.SearchAsync(new SearchQuery { Root = Folder, Text = "99", MaxResults = int.MaxValue }))
            found += batch.Count;
        return found;
    }
}

[MemoryDiagnoser]
public class ComparerBenchmarks
{
    private readonly string[] _names = [.. Enumerable.Range(0, 10_000).Select(i => $"IMG_{i % 977:0000}_{i}.jpg")];

    [Benchmark]
    public string[] NaturalSort10K()
    {
        var copy = (string[])_names.Clone();
        Array.Sort(copy, NaturalStringComparer.Instance);
        return copy;
    }
}

[MemoryDiagnoser]
public class CacheBenchmarks
{
    private readonly LruCache<int, int> _cache = new(capacity: 4096);

    [Benchmark]
    public int SetAndHit()
    {
        var sum = 0;
        for (var i = 0; i < 10_000; i++)
        {
            _cache.Set(i & 8191, i);
            if (_cache.TryGet(i & 4095, out var value))
                sum += value;
        }

        return sum;
    }
}

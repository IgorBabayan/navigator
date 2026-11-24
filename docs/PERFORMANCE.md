# Performance

Performance is Navigator's top technical priority. This document lists the targets, the techniques used,
the numbers that were **actually measured**, and what is still unmeasured. No figure here is estimated.

## Targets

| Metric | Target |
|---|---|
| Cold start to interactive window | < 800 ms on a mid-range SSD machine |
| First items visible after navigating | < 100 ms for any folder size |
| Folder with 100,000 entries | fully listed and sorted without blocking the UI; scrolling at display refresh rate |
| Idle CPU | ~0 % (no polling except the 3 s volume fingerprint while the drive list is visible) |
| Idle memory (one window, home page) | < 150 MB working set |
| Animations | no dropped frames at 60 Hz on integrated GPUs; compositor-only properties |

## Techniques

| Area | Technique | Where |
|---|---|---|
| Listing | `FileSystemEnumerable` with a struct transform (no `FileInfo` allocation per entry); batched channel; small first batch (96) | `DirectoryEnumerator` |
| UI updates | sorted snapshots built off the UI thread, published at most every 250 ms; `RangeObservableCollection` resets instead of per-item notifications | `PaneViewModel.View.cs` |
| Virtualization | one `VirtualizingStackPanel` over row models for every layout; tile layouts chunked into rows | `Rows/*`, `TileRowViewModel` |
| Live changes | coalesced watcher events; binary-search insert/remove | `DirectoryMonitor`, `EntrySorter.FindInsertIndex` |
| Sorting | allocation-free natural comparer over spans | `NaturalStringComparer` |
| Lazy work | display strings on first access; thumbnails only for realized rows, cancelled on recycle; folder sizes opt-in | `FileItemViewModel`, `RowThumbnails` |
| Scheduling | priority worker pool: Visible > Normal > Background | `BackgroundWorkScheduler` |
| Caching | cost-bounded LRU with request de-duplication (thumbnails, icons, folder sizes) | `LruCache` |
| Revisits | recently visited folder listings (count- and entry-bounded) shown at once, re-enumerated in the background; unchanged entries keep their instances, so rows are not rebuilt | `DirectoryListingCache`, `PaneViewModel.RevalidateCachedListingAsync` |
| Thumbnails on disk | PNG copies of thumbnails of images > 256 KB, size-bounded, oldest evicted, scanned lazily on the first write | `DiskCache`, `ThumbnailProvider` |
| Diagnostics | status bar indicator fed by Avalonia's metrics (`Avalonia.Diagnostic.Meter`) through a `MeterListener` that exists only while the indicator is on; never forces frames | `PerformanceMonitor`, `PerformanceIndicator` |
| Startup | ReadyToRun, compiled bindings, JSON source generation, no reflection-based DI scanning, update checks deferred until after the first frame | `Navigator.App.csproj`, `CompositionRoot` |
| Copy engine | 1 MiB buffers, preallocation, progress sampled by polling instead of per-chunk events | `SafeFileCopier`, `FileOperation` |
| Memory | bounded history (128), recently closed tabs (25), per-folder preferences (500), log files (7 × 5 MB) | |

## Measured results

### Core engine (tools/Navigator.PerfProbe)

Environment: Ubuntu 24.04.5 container, Intel Xeon @ 2.10 GHz, 2 logical CPUs, 8 GB RAM, .NET 10.0.15,
Release build, overlay file system, warm OS cache, empty files named `file {i} report.txt`.
Each cell is the median of 5 runs; two independent invocations on 2026-11-24 are shown.

| Entries | First batch | Full listing | Natural sort | Recursive name search |
|---|---|---|---|---|
| 10,000 | 0.98 / 0.49 ms | 22.7 / 17.8 ms | 26.4 / 26.1 ms | 5.2 / 5.1 ms |
| 100,000 | 0.61 / 0.61 ms | 188.0 / 191.7 ms | 61.4 / 70.3 ms | 39.3 / 44.9 ms |

Probe process working set after both sizes: 106 / 103 MB (this is the probe holding 100,000 entries in
memory, **not** the UI application).

Reading the numbers: the first batch arrives in under 1 ms regardless of folder size, which is what the UI
shows first. Full listing of 100,000 entries takes ~0.19 s on this slow, shared container and runs in the
background. The 10,000-entry sort is slower than the 100,000-entry sort because it includes JIT
tiering of the comparer on the first size measured — BenchmarkDotNet (below) removes such effects.

Reproduce:

```bash
dotnet run -c Release --project tools/Navigator.PerfProbe -- 5
```

### BenchmarkDotNet suite (tests/Navigator.Benchmarks)

Enumeration (first batch, full listing, listing + sort, search) at 10,000/100,000 entries, comparer and
LRU cache micro-benchmarks. **Not run yet**: the development environment could not restore NuGet packages.
Run on real hardware with:

```bash
dotnet run -c Release --project tests/Navigator.Benchmarks -- --filter '*'
```

## Not yet measured

| Metric | Why | How to measure |
|---|---|---|
| Startup time | Avalonia app could not be built in the development environment | `Program.StartupTimer` logs "first frame after N ms" at Information level |
| UI memory (idle, 100k folder) | same | Task Manager / `ps -o rss` after opening `navigator-probe/100000` |
| Scrolling frame rate, animation smoothness | same | Settings → Performance → "Show performance diagnostics" (status bar; hover for details) or OS frame tools |
| Copy throughput vs OS file manager | needs real disks | copy a 10 GB mixed tree on the same volume and across volumes |
| Windows and macOS numbers | no hardware in the development environment | run the probe and benchmark suite |

Record new measurements here with date, hardware, OS, build configuration and the exact command.

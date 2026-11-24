# Third-party notices

Navigator is licensed under the MIT License (`LICENSE`). It uses the following third-party components,
obtained as NuGet packages (not vendored). Release packages include the runtime components marked "shipped".

| Component | Version | License | Shipped | Project |
|---|---|---|---|---|
| .NET runtime and libraries | 10.0 | MIT | yes (self-contained builds) | https://github.com/dotnet/runtime |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.3 | MIT | yes | https://github.com/AvaloniaUI/Avalonia |
| Avalonia native dependencies (SkiaSharp, HarfBuzzSharp, MicroCom) | as resolved by Avalonia | MIT | yes | https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | yes | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | MIT | yes | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging (+ Abstractions) | 10.0.0 | MIT | yes | https://github.com/dotnet/runtime |
| Avalonia.Headless | 12.1.3 | MIT | no (tests) | https://github.com/AvaloniaUI/Avalonia |
| MSTest | 3.10.0 | MIT | no (tests) | https://github.com/microsoft/testfx |
| BenchmarkDotNet | 0.15.2 | MIT | no (benchmarks) | https://github.com/dotnet/BenchmarkDotNet |

Build-time tools (not shipped): Inno Setup (Inno Setup License), appimagetool (MIT), Apple Xcode tools.
The AppImage runtime embedded by appimagetool is MIT-licensed (https://github.com/AppImage/type2-runtime).

Code adapted from **ED Assistant** (https://github.com/IgorBabayan/ed-assistant) is by the same author and
used with permission; see `docs/SOURCE_ORIGINALITY.md`.

The full license texts of the MIT-licensed packages are distributed with the packages on nuget.org. Before
each release, verify this table against `Directory.Packages.props` and the resolved transitive packages
(`dotnet list package --include-transitive`).

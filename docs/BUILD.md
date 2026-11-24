# Building Navigator

## Requirements

- .NET SDK 10.0.100 or later feature band (`global.json`, `rollForward: latestFeature`)
- Access to nuget.org (Avalonia, CommunityToolkit.Mvvm, Microsoft.Extensions.*, MSTest, BenchmarkDotNet)
- Linux: X11 or XWayland session to run the UI; `libfontconfig1`, `libice6`, `libsm6` (Avalonia native deps)
- Packaging only: Inno Setup 6 (Windows), `curl` + FUSE-less `appimagetool --appimage-extract-and-run`
  (Linux), Xcode command line tools (`sips`, `iconutil`, `codesign`, `hdiutil`, `ditto`) on macOS

## Commands

```bash
dotnet restore Navigator.slnx
dotnet build Navigator.slnx -c Debug
dotnet run --project src/Navigator.App                       # start the app
dotnet test tests/Navigator.UnitTests -c Release
dotnet test tests/Navigator.IntegrationTests -c Release
dotnet format Navigator.slnx --verify-no-changes             # style gate used by CI
dotnet build Navigator.slnx -c Release -p:ContinuousIntegrationBuild=true   # warnings as errors
```

Isolated data folder for experiments: `NAVIGATOR_DATA_DIR=/tmp/nav-data dotnet run --project src/Navigator.App`.

## Publishing (what CI does)

```bash
RID=linux-x64   # win-x64 win-arm64 linux-x64 linux-arm64 osx-arm64 osx-x64
VERSION=1.0.0
dotnet publish src/Navigator.App/Navigator.App.csproj -c Release -r $RID --self-contained true \
  -p:AppVersion=$VERSION -p:UpdateRepository=IgorBabayan/navigator -p:UpdatePublicKey=<base64> \
  -o publish/$RID
```

Then package:

| Platform | Command | Output (in `dist/`) |
|---|---|---|
| Windows | `pwsh installer/windows/package.ps1 -Version $VERSION -Rid win-x64 -PublishDir publish/win-x64 -OutputDir dist` | `Navigator-$VERSION-win-x64-setup.exe`, `Navigator-$VERSION-win-x64-portable.zip` |
| Linux | `installer/linux/package.sh $VERSION linux-x64 x86_64 publish/linux-x64 dist` | `Navigator-$VERSION-linux-x64.AppImage`, `Navigator-$VERSION-linux-x64.tar.gz` |
| macOS | `installer/macos/package.sh $VERSION osx-arm64 publish/osx-arm64 dist` | `Navigator-$VERSION-osx-arm64.dmg`, `Navigator-$VERSION-osx-arm64.zip` |

Release metadata:

```bash
TOOL="dotnet run --project tools/Navigator.ReleaseTool --"
$TOOL manifest  --version $VERSION --commit $(git rev-parse HEAD) --channel stable --assets dist --out dist/update-manifest.json
$TOOL sign      --manifest dist/update-manifest.json --out dist/update-manifest.json.sig   # needs NAVIGATOR_UPDATE_SIGNING_KEY
$TOOL checksums --assets dist --out dist/SHA256SUMS.txt
$TOOL verify    --manifest dist/update-manifest.json --assets dist
```

The full command list is at the top of `tools/Navigator.ReleaseTool/Program.cs`.

## Build properties

| Property | Default | Meaning |
|---|---|---|
| `AppVersion` | `0.0.0-dev` | sets `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion` |
| `UpdateRepository` | empty | `owner/repo` for the updater; empty disables updates |
| `UpdatePublicKey` | empty | base64 SPKI public key; when set, manifests must be signed |
| `ContinuousIntegrationBuild` | false | deterministic paths, warnings as errors |

## Offline / restricted environments

Projects `Navigator.Core`, `Navigator.Platform`, `Navigator.Updater`, `tools/Navigator.ReleaseTool` and
`tools/Navigator.PerfProbe` depend only on the .NET SDK and build without NuGet access. Everything else
needs packages from nuget.org.

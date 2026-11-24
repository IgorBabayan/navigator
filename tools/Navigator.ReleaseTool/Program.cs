using System.Text;
using Navigator.Core.Versioning;
using Navigator.ReleaseTool;
using Navigator.Updater;

// Navigator release tool. Used only by .github/workflows/release.yml; see docs/RELEASE.md.
//   next-version  --version-file version.json --tags-file tags.txt [--bump auto|patch|minor|major] [--labels "a,b"]
//   is-newest     --version X --tags-file tags.txt            -> prints true/false (should this release be "Latest"?)
//   manifest      --version X --commit SHA --channel stable --assets dist [--notes notes.md] --out update-manifest.json
//   checksums     --assets dist --out SHA256SUMS.txt
//   sign          --manifest update-manifest.json --out update-manifest.json.sig   (key in NAVIGATOR_UPDATE_SIGNING_KEY)
//   verify        --manifest update-manifest.json --assets dist [--require win-x64,linux-x64] [--public-key KEY --signature FILE]
//   keygen
var command = args.FirstOrDefault();
var options = ParseOptions(args.Skip(1).ToArray());

try
{
    switch (command)
    {
        case "next-version":
        {
            var (major, minor) = ReleaseLogic.ReadSeries(File.ReadAllText(Required(options, "version-file")));
            var tags = ReadLines(options.GetValueOrDefault("tags-file"));
            var bump = options.TryGetValue("bump", out var explicitBump) && explicitBump.Length > 0 && !explicitBump.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? Enum.Parse<VersionBump>(explicitBump, ignoreCase: true)
                : ReleaseLogic.BumpFromLabels((options.GetValueOrDefault("labels") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries));
            Console.WriteLine(ReleaseLogic.ComputeNextVersion(major, minor, tags, bump));
            return 0;
        }

        case "is-newest":
        {
            var version = SemVersion.Parse(Required(options, "version"));
            var highest = ReleaseLogic.HighestStable(ReadLines(options.GetValueOrDefault("tags-file")));
            Console.WriteLine(highest is null || version >= highest ? "true" : "false");
            return 0;
        }

        case "manifest":
        {
            var notesPath = options.GetValueOrDefault("notes");
            var notes = notesPath is not null && File.Exists(notesPath) ? File.ReadAllText(notesPath) : null;
            var manifest = await ReleaseLogic.BuildManifestAsync(
                Required(options, "version"), Required(options, "commit"), options.GetValueOrDefault("channel") ?? "stable",
                Required(options, "assets"), notes, DateTimeOffset.UtcNow);
            File.WriteAllText(Required(options, "out"), manifest.Serialize(), new UTF8Encoding(false));
            Console.WriteLine($"Manifest with {manifest.Assets.Count} assets written.");
            return 0;
        }

        case "checksums":
            File.WriteAllText(Required(options, "out"), await ReleaseLogic.BuildChecksumsAsync(Required(options, "assets")), new UTF8Encoding(false));
            return 0;

        case "sign":
        {
            var key = Environment.GetEnvironmentVariable("NAVIGATOR_UPDATE_SIGNING_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.Error.WriteLine("NAVIGATOR_UPDATE_SIGNING_KEY is not set; the manifest stays unsigned.");
                return 0;
            }

            var bytes = File.ReadAllBytes(Required(options, "manifest"));
            File.WriteAllText(Required(options, "out"), ManifestSignature.Sign(bytes, key.Trim()));
            return 0;
        }

        case "verify":
        {
            var manifestPath = Required(options, "manifest");
            var bytes = File.ReadAllBytes(manifestPath);
            var manifest = UpdateManifest.Parse(bytes);
            var required = (options.GetValueOrDefault("require") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var problems = (await ReleaseLogic.VerifyAsync(manifest, Required(options, "assets"), required)).ToList();

            if (options.TryGetValue("public-key", out var publicKey) && publicKey.Length > 0)
            {
                var signaturePath = Required(options, "signature");
                if (!File.Exists(signaturePath) || !ManifestSignature.Verify(bytes, File.ReadAllText(signaturePath), publicKey))
                    problems.Add("manifest signature does not verify");
            }

            foreach (var problem in problems)
                Console.Error.WriteLine($"::error::{problem}");
            return problems.Count == 0 ? 0 : 1;
        }

        case "keygen":
        {
            var (privateKey, publicKey) = ManifestSignature.CreateKeyPair();
            Console.WriteLine("Private key (store as the NAVIGATOR_UPDATE_SIGNING_KEY secret):");
            Console.WriteLine(privateKey);
            Console.WriteLine("Public key (store as the NAVIGATOR_UPDATE_PUBLIC_KEY variable):");
            Console.WriteLine(publicKey);
            return 0;
        }

        default:
            Console.Error.WriteLine("Commands: next-version, is-newest, manifest, checksums, sign, verify, keygen");
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException or KeyNotFoundException)
{
    Console.Error.WriteLine($"::error::{ex.Message}");
    return 1;
}

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < arguments.Length; i++)
    {
        if (!arguments[i].StartsWith("--", StringComparison.Ordinal))
            continue;
        var key = arguments[i][2..];
        result[key] = i + 1 < arguments.Length && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[++i] : string.Empty;
    }

    return result;
}

static string Required(Dictionary<string, string> options, string key)
    => options.TryGetValue(key, out var value) && value.Length > 0 ? value : throw new ArgumentException($"--{key} is required.");

static IEnumerable<string> ReadLines(string? path)
    => path is not null && File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0) : [];

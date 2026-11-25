using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Navigator.Core.Platform;
using Navigator.Core.Settings;

namespace Navigator.Platform.Secrets;

/// <summary>Names used as Keychain / Secret Service attributes are restricted, so they can never inject commands.</summary>
internal static class SecretNames
{
    public static void Validate(string service, string account)
    {
        if (!IsValid(service) || !IsValid(account))
            throw new ArgumentException("Secret names may contain letters, digits, '.', '-', '_' and '@' only.");
    }

    private static bool IsValid(string value)
        => value.Length is > 0 and <= 200 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@');
}

/// <summary>
/// Windows: each secret is a small file encrypted with DPAPI for the current user (CryptProtectData), so only this user on
/// this machine can read it. Files are named by a hash of the service and account and written atomically.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsDpapiSecretStore(string directory) : ISecretStore
{
    private const uint UiForbidden = 0x1;
    private static readonly byte[] Entropy = "Navigator.Secrets.v1"u8.ToArray();

    public bool IsPersistent => true;

    public Task<string?> ReadAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        var path = FileFor(service, account);
        try
        {
            if (!File.Exists(path))
                return Task.FromResult<string?>(null);
            var plain = Transform(File.ReadAllBytes(path), protect: false);
            try
            {
                return Task.FromResult<string?>(plain is null ? null : Encoding.UTF8.GetString(plain));
            }
            finally
            {
                if (plain is not null)
                    CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<bool> WriteAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        ArgumentNullException.ThrowIfNull(secret);
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var encrypted = Transform(plain, protect: true);
            if (encrypted is null)
                return Task.FromResult(false);
            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllBytes(FileFor(service, account), encrypted, keepBackup: false);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        try
        {
            File.Delete(FileFor(service, account));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be deleted stays encrypted; it is overwritten by the next sign-in.
        }

        return Task.CompletedTask;
    }

    private string FileFor(string service, string account)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(service + "\n" + account));
        return Path.Combine(directory, Convert.ToHexStringLower(hash.AsSpan(0, 16)) + ".bin");
    }

    private static byte[]? Transform(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (nint)input };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            var ok = protect
                ? CryptProtectData(&inBlob, 0, &entropyBlob, 0, 0, UiForbidden, out var outBlob)
                : CryptUnprotectData(&inBlob, 0, &entropyBlob, 0, 0, UiForbidden, out outBlob);
            if (!ok)
                return null;
            try
            {
                return new ReadOnlySpan<byte>((void*)outBlob.Data, outBlob.Size).ToArray();
            }
            finally
            {
                if (!protect)
                    new Span<byte>((void*)outBlob.Data, outBlob.Size).Clear();
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(DataBlob* input, nint description, DataBlob* entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(DataBlob* input, nint description, DataBlob* entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

/// <summary>
/// macOS: generic passwords in the login Keychain through /usr/bin/security. The secret is written through the tool's
/// standard input (interactive mode), never as an argument, and hex-encoded so no character needs quoting.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainSecretStore : ISecretStore
{
    private const string Tool = "/usr/bin/security";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60); // the Keychain may ask the user to unlock it

    public bool IsPersistent => File.Exists(Tool);

    public async Task<string?> ReadAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        var result = await ProcessRunner.RunAsync(Tool, ["find-generic-password", "-s", service, "-a", account, "-w"], null, Timeout, cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 } found)
            return null;
        var hex = found.Output.Trim();
        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<bool> WriteAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        ArgumentNullException.ThrowIfNull(secret);
        var hex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(secret));
        var command = $"add-generic-password -U -s \"{service}\" -a \"{account}\" -l \"Navigator\" -w \"{hex}\"\n";
        await ProcessRunner.RunAsync(Tool, ["-i"], command, Timeout, cancellationToken).ConfigureAwait(false);

        // Interactive mode does not report a failed command in its exit code: read the item back.
        return string.Equals(await ReadAsync(service, account, cancellationToken).ConfigureAwait(false), secret, StringComparison.Ordinal);
    }

    public Task DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        return ProcessRunner.RunAsync(Tool, ["delete-generic-password", "-s", service, "-a", account], null, Timeout, cancellationToken);
    }
}

/// <summary>
/// Linux: the freedesktop Secret Service (GNOME Keyring, KWallet, KeePassXC) through <c>secret-tool</c> (libsecret).
/// Secrets go through standard input. Without secret-tool or a session bus nothing is stored.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class SecretServiceStore : ISecretStore
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60); // the keyring may ask the user to unlock it
    private readonly Lazy<string?> _tool = new(() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
        && !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/nonexistent", "bus"))
            ? null
            : ProcessRunner.FindOnPath("secret-tool"));

    public bool IsPersistent => _tool.Value is not null;

    public async Task<string?> ReadAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        if (_tool.Value is not { } tool)
            return null;
        var result = await ProcessRunner.RunAsync(tool, ["lookup", "service", service, "account", account], null, Timeout, cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0, Output.Length: > 0 } found ? found.Output.TrimEnd('\n') : null;
    }

    public async Task<bool> WriteAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        ArgumentNullException.ThrowIfNull(secret);
        if (_tool.Value is not { } tool || secret.Contains('\n', StringComparison.Ordinal))
            return false;
        var result = await ProcessRunner.RunAsync(tool, ["store", "--label=Navigator (" + service + ")", "service", service, "account", account], secret, Timeout, cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    public async Task DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        if (_tool.Value is { } tool)
            await ProcessRunner.RunAsync(tool, ["clear", "service", service, "account", account], null, Timeout, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>No credential store: nothing survives a restart.</summary>
internal sealed class UnsupportedSecretStore : ISecretStore
{
    public bool IsPersistent => false;

    public Task<string?> ReadAsync(string service, string account, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<bool> WriteAsync(string service, string account, string secret, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task DeleteAsync(string service, string account, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

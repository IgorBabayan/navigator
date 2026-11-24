using System.Collections.Concurrent;

namespace Navigator.Plugins.Remote;

/// <summary>
/// Passwords and passphrases for the running session only: asked once per server, kept in memory, never written to
/// disk, dropped after a failed sign-in. Prompts are serialized so parallel requests ask only once.
/// </summary>
public sealed class CredentialCache : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _prompt = new(1, 1);

    public bool TryGet(string key, out string secret) => _secrets.TryGetValue(key, out secret!);

    /// <summary>
    /// The remembered secret for <paramref name="key"/>, or asks for it.
    /// Throws <see cref="RemoteFileSystemException"/> (<see cref="RemoteErrorKind.Cancelled"/>) when the user cancels.
    /// </summary>
    public async Task<string> GetOrPromptAsync(string key, IPluginUi ui, string title, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ui);
        if (_secrets.TryGetValue(key, out var known))
            return known;

        await _prompt.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_secrets.TryGetValue(key, out known))
                return known;
            var entered = await ui.PromptTextAsync(title, label, isSecret: true).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (entered is null)
                throw new RemoteFileSystemException(RemoteErrorKind.Cancelled, "Sign-in was cancelled.");
            _secrets[key] = entered;
            return entered;
        }
        finally
        {
            _prompt.Release();
        }
    }

    public void Forget(string key) => _secrets.TryRemove(key, out _);

    public void Clear() => _secrets.Clear();

    public void Dispose()
    {
        _secrets.Clear();
        _prompt.Dispose();
    }
}

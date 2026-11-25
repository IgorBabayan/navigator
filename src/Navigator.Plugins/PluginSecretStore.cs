using System.Collections.Concurrent;
using Navigator.Core.Platform;

namespace Navigator.Plugins;

/// <summary>
/// One plugin's view of the OS credential store: names are scoped by the plugin id ("Navigator.{pluginId}"), and values
/// are also kept in memory so repeated reads cost no helper process and a store that is missing (or refuses a write)
/// still works for the running session.
/// </summary>
internal sealed class PluginSecretStore(string pluginId, ISecretStore store) : IPluginSecretStore
{
    private readonly string _service = "Navigator." + pluginId;
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);

    public bool IsPersistent => store.IsPersistent;

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        if (_memory.TryGetValue(name, out var known))
            return known;
        var stored = await store.ReadAsync(_service, name, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
            _memory[name] = stored;
        return stored;
    }

    public async Task<bool> SetAsync(string name, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _memory[name] = secret;
        return store.IsPersistent && await store.WriteAsync(_service, name, secret, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        _memory.TryRemove(name, out _);
        await store.DeleteAsync(_service, name, cancellationToken).ConfigureAwait(false);
    }
}

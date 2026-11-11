using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Keeps long upstream URLs out of HLS lines while retaining their exact query strings.
/// </summary>
internal sealed class RelayUrlCache
{
    private const int MaxEntries = 8192;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Url, long Access)> _entries = new(StringComparer.Ordinal);
    private long _access;

    public string Store(string sessionId, string url)
    {
        var reference = "ref." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\n" + url)));
        var cacheKey = sessionId + ":" + reference;
        lock (_gate)
        {
            if (!_entries.ContainsKey(cacheKey) && _entries.Count >= MaxEntries)
            {
                _entries.Remove(_entries.MinBy(static entry => entry.Value.Access).Key);
            }

            _entries[cacheKey] = (url, ++_access);
        }

        return reference;
    }

    public string Resolve(string sessionId, string reference)
    {
        var cacheKey = sessionId + ":" + reference;
        lock (_gate)
        {
            if (!_entries.TryGetValue(cacheKey, out var entry))
            {
                throw new FormatException("Unknown or expired relay URL reference. Reopen stream.");
            }

            _entries[cacheKey] = (entry.Url, ++_access);
            return entry.Url;
        }
    }
}

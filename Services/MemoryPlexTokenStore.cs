using System.Collections.Concurrent;

namespace SpotifyPlaylistWebApp.Services;

/// <summary>
/// Stores Plex tokens purely in memory (RAM-based, per application instance).
/// Thread-safe and lightweight; suitable for temporary in-session storage.
/// </summary>
public class MemoryPlexTokenStore : IPlexTokenStore
{
    // Concurrent dictionary for thread-safe token access
    private static readonly ConcurrentDictionary<string, string> _tokens = new();

    /// <summary>
    /// Saves a Plex token in memory for a given user key.
    /// </summary>
    public Task SaveAsync(string userKey, string plexToken, CancellationToken ct = default)
    {
        _tokens[userKey] = plexToken;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads the Plex token for a given user key, or returns null if not found.
    /// </summary>
    public Task<string?> LoadAsync(string userKey, CancellationToken ct = default)
    {
        _tokens.TryGetValue(userKey, out var token);
        return Task.FromResult(token);
    }

    /// <summary>
    /// Deletes the Plex token associated with the given user key.
    /// </summary>
    public Task DeleteAsync(string userKey, CancellationToken ct = default)
    {
        _tokens.TryRemove(userKey, out _);
        return Task.CompletedTask;
    }
}
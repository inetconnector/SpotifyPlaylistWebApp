using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace SpotifyPlaylistWebApp.Services;

/// <summary>
///     Implementation of IPlexTokenStore using IDistributedCache.
///     Stores Plex tokens temporarily in a distributed memory cache.
/// </summary>
public class PlexTokenStore : IPlexTokenStore
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromDays(14),
        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(90)
    };

    private readonly IDistributedCache _cache;

    public PlexTokenStore(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task SaveAsync(string userKey, string plexToken, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(plexToken ?? string.Empty);
        await _cache.SetAsync($"plex:{userKey}", bytes, CacheOptions, ct);
    }

    public async Task<string?> LoadAsync(string userKey, CancellationToken ct = default)
    {
        var bytes = await _cache.GetAsync($"plex:{userKey}", ct);
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    public async Task DeleteAsync(string userKey, CancellationToken ct = default)
    {
        await _cache.RemoveAsync($"plex:{userKey}", ct);
    }
}
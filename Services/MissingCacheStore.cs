using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyPlaylistWebApp.Services;

/// <summary>
///     Persistent store for missing tracks per Plex server.
///     File path: wwwroot/data/{plexServerId}/missing_cache.json
/// </summary>
public sealed class MissingCacheStore
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;

    public MissingCacheStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(env.WebRootPath ?? "wwwroot", "data");
        Directory.CreateDirectory(_root);
    }

    private string GetServerDir(string plexServerId)
    {
        var dir = Path.Combine(_root, plexServerId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string GetFile(string plexServerId)
    {
        return Path.Combine(GetServerDir(plexServerId), "missing_cache.json");
    }

    public Dictionary<string, CacheEntry> Load(string plexServerId)
    {
        try
        {
            var file = GetFile(plexServerId);
            if (!File.Exists(file)) return new Dictionary<string, CacheEntry>();
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, _json) ??
                   new Dictionary<string, CacheEntry>();
        }
        catch
        {
            return new Dictionary<string, CacheEntry>();
        }
    }

    public void Save(string plexServerId, Dictionary<string, CacheEntry> data)
    {
        var file = GetFile(plexServerId);
        var json = JsonSerializer.Serialize(data, _json);
        File.WriteAllText(file, json);
    }

    public void RemoveEntry(string plexServerId, string playlistName)
    {
        var map = Load(plexServerId);
        if (map.Remove(playlistName))
            Save(plexServerId, map);
    }

    public void CleanupOlderThanDays(string plexServerId, int days = 1)
    {
        var file = GetFile(plexServerId);
        if (!File.Exists(file)) return;
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
        if (age.TotalDays >= days) File.Delete(file);
    }

    public sealed class CacheEntry
    {
        public List<string> Items { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }
}
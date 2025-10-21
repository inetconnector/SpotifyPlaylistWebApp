using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using SpotifyAPI.Web;

namespace SpotifyPlaylistWebApp.Services;

public class PlexService
{
    private readonly HttpClient _http;

    public PlexService(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add("Accept", "application/xml");
        _http.DefaultRequestHeaders.Add("User-Agent", "SpotifyToPlex/1.0");
        _http.DefaultRequestHeaders.Add("X-Plex-Product", "SpotifyToPlex");
        _http.DefaultRequestHeaders.Add("X-Plex-Device", "WebApp");
    }

    // ✅ Token-Prüfung
    public async Task<bool> ValidateTokenAsync(string plexToken)
    {
        try
        {
            var res = await _http.GetAsync($"https://plex.tv/api/v2/user?X-Plex-Token={plexToken}");
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Plex ValidateToken] {ex.Message}");
            return false;
        }
    }

    // ✅ PIN anfordern
    public async Task<(int PinId, string Code, string ClientId)> CreatePinAsync()
    {
        var clientId = Guid.NewGuid().ToString();

        using var c = new HttpClient();
        c.DefaultRequestHeaders.Add("X-Plex-Product", "SpotifyToPlex");
        c.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", clientId);
        c.DefaultRequestHeaders.Add("X-Plex-Device-Name", "SpotifyPlaylistWebApp");
        c.DefaultRequestHeaders.Add("Accept", "application/json");

        var res = await c.PostAsync("https://plex.tv/api/v2/pins?strong=true", null);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.GetProperty("id").GetInt32();
        var code = root.GetProperty("code").GetString() ?? throw new Exception("Fehler: Kein Plex-Code erhalten.");

        Console.WriteLine($"[Plex CreatePin] Code={code}, ID={id}, Client={clientId}");
        return (id, code, clientId);
    }

    // ✅ Polling für Plex-PIN
    public async Task<string?> PollPinAsync(int pinId, string clientId)
    {
        using var c = new HttpClient();
        c.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", clientId);
        c.DefaultRequestHeaders.Add("Accept", "application/json");

        var url = $"https://plex.tv/api/v2/pins/{pinId}?includeClient=true&X-Plex-Client-Identifier={clientId}";

        for (var attempt = 1; attempt <= 10; attempt++)
            try
            {
                var res = await c.GetAsync(url);

                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Plex Poll] Attempt {attempt} → {res.StatusCode}");
                    if (res.StatusCode == HttpStatusCode.NotFound)
                    {
                        await Task.Delay(2000);
                        continue;
                    }

                    return null;
                }

                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("authToken", out var tokenProp) &&
                    tokenProp.ValueKind == JsonValueKind.String)
                {
                    var token = tokenProp.GetString();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        Console.WriteLine("[Plex Poll] Success \u2192 Token received.");
                        return token;
                    }
                }

                Console.WriteLine($"[Plex Poll] No token yet (attempt {attempt})");
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plex Poll] Exception: {ex.Message}");
                await Task.Delay(2000);
            }

        return null;
    }

    // ✅ Server-Discovery
    public async Task<(string BaseUrl, string MachineId)> DiscoverServerAsync(string plexToken)
    {
        var xml = await _http.GetStringAsync($"https://plex.tv/api/resources?includeHttps=1&X-Plex-Token={plexToken}");
        var doc = XDocument.Parse(xml);

        var server = doc.Descendants("Device")
            .FirstOrDefault(d =>
                ((string?)d.Attribute("provides") ?? "").Contains("server", StringComparison.OrdinalIgnoreCase));

        if (server == null)
            throw new Exception("Kein Plex-Server im Konto gefunden.");

        var connection = server.Elements("Connection")
                             .FirstOrDefault(c =>
                                 c.Attribute("local")?.Value == "0" && c.Attribute("protocol")?.Value == "https")
                         ?? server.Elements("Connection").FirstOrDefault();

        if (connection == null)
            throw new Exception("Keine gültige Server-Verbindung gefunden.");

        var baseUrl = connection.Attribute("uri")?.Value ?? throw new Exception("Keine Server-URI gefunden.");
        var machineId = server.Attribute("machineIdentifier")?.Value ?? "unknown";

        Console.WriteLine($"[Plex Discover] Server={baseUrl}, Machine={machineId}");
        return (baseUrl, machineId);
    }

    // ✅ Playlist-Liste abrufen
    public async Task<List<(string Title, string RatingKey)>> GetPlexPlaylistsAsync(string plexBaseUrl,
        string plexToken)
    {
        var xml = await _http.GetStringAsync($"{plexBaseUrl}/playlists/all?X-Plex-Token={plexToken}");
        var doc = XDocument.Parse(xml);

        return doc.Descendants("Playlist")
            .Where(x => (string?)x.Attribute("playlistType") == "audio")
            .Select(x => ((string?)x.Attribute("title") ?? "Untitled", (string?)x.Attribute("ratingKey") ?? ""))
            .ToList();
    }

    // ✅ Spotify Playlist Tracks abrufen
    public async Task<List<(string Title, string Artist)>> GetSpotifyPlaylistTracksAsync(SpotifyClient spotify,
        string playlistId)
    {
        var tracks = new List<(string Title, string Artist)>();
        var page = await spotify.Playlists.GetItems(playlistId);

        while (true)
        {
            foreach (var item in page.Items)
                if (item.Track is FullTrack t)
                {
                    var artist = t.Artists.FirstOrDefault()?.Name ?? "";
                    tracks.Add((t.Name, artist));
                }

            if (string.IsNullOrEmpty(page.Next)) break;
            page = await spotify.NextPage(page);
        }

        return tracks;
    }

    // ✅ Plex-Titel suchen
    public async Task<List<(string Title, string Artist, string? RatingKey)>> SearchTracksOnPlexAsync(
        string plexBaseUrl, string plexToken, List<(string Title, string Artist)> tracks)
    {
        var results = new List<(string Title, string Artist, string? RatingKey)>();

        foreach (var (title, artist) in tracks)
        {
            var q = Uri.EscapeDataString($"{artist} {title}");
            var url = $"{plexBaseUrl}/search?query={q}&X-Plex-Token={plexToken}";

            try
            {
                var xml = await _http.GetStringAsync(url);
                var doc = XDocument.Parse(xml);
                var key = doc.Descendants("Track").FirstOrDefault()?.Attribute("ratingKey")?.Value;

                if (key == null)
                {
                    // fallback ohne Artist
                    var q2 = Uri.EscapeDataString(title);
                    var xml2 = await _http.GetStringAsync($"{plexBaseUrl}/search?query={q2}&X-Plex-Token={plexToken}");
                    var doc2 = XDocument.Parse(xml2);
                    key = doc2.Descendants("Track").FirstOrDefault()?.Attribute("ratingKey")?.Value;
                }

                results.Add((title, artist, key));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plex Search] {artist} - {title}: {ex.Message}");
                results.Add((title, artist, null));
            }
        }

        return results;
    }

    // ✅ Neue Plex-Playlist erstellen
    public async Task<string> CreatePlaylistAsync(string plexBaseUrl, string plexToken, string name)
    {
        var url =
            $"{plexBaseUrl}/playlists?type=audio&title={Uri.EscapeDataString(name)}&smart=0&X-Plex-Token={plexToken}";
        var res = await _http.PostAsync(url, null);
        res.EnsureSuccessStatusCode();

        var xml = await res.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);

        var key = doc.Descendants("Playlist").FirstOrDefault()?.Attribute("ratingKey")?.Value;
        if (string.IsNullOrEmpty(key))
            throw new Exception("Playlist konnte nicht erstellt werden.");

        return key;
    }

    // ✅ Tracks hinzufügen
    public async Task AddTracksToPlaylistAsync(string plexBaseUrl, string plexToken, string playlistKey,
        IEnumerable<string> ratingKeys)
    {
        foreach (var key in ratingKeys)
        {
            var uri = $"server://local/com.plexapp.plugins.library/library/metadata/{key}";
            var url =
                $"{plexBaseUrl}/playlists/{playlistKey}/items?uri={Uri.EscapeDataString(uri)}&X-Plex-Token={plexToken}";
            var res = await _http.PutAsync(url, null);
            res.EnsureSuccessStatusCode();
            await Task.Delay(50);
        }
    }

    // ✅ Alle Spotify-Playlists laden
    public async Task<List<(string Id, string Name)>> GetAllSpotifyPlaylistsAsync(SpotifyClient spotify)
    {
        var result = new List<(string Id, string Name)>();
        var page = await spotify.Playlists.CurrentUsers(new PlaylistCurrentUsersRequest { Limit = 50 });

        while (true)
        {
            foreach (var p in page.Items)
                if (p != null && !string.IsNullOrEmpty(p.Id))
                    result.Add((p.Id, p.Name));

            if (string.IsNullOrEmpty(page.Next)) break;
            page = await spotify.NextPage(page);
        }

        return result;
    }

    // ✅ Export einer Spotify-Playlist
    public async Task<(string ExportedName, int AddedCount, List<string> Missing)> ExportOnePlaylistAsync(
        SpotifyClient spotify, string spotifyPlaylistId, string? explicitExportName,
        string plexBaseUrl, string plexToken)
    {
        // Load previously missing tracks from local cache
        var cachePath = Path.Combine(AppContext.BaseDirectory, "data", "plex_missing_cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        var cache = new Dictionary<string, List<string>>();
        if (File.Exists(cachePath))
            try
            {
                var json = await File.ReadAllTextAsync(cachePath);
                cache = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                        ?? new Dictionary<string, List<string>>();
            }
            catch
            {
                cache = new Dictionary<string, List<string>>();
            }

        var name = !string.IsNullOrWhiteSpace(explicitExportName)
            ? explicitExportName.Trim()
            : $"Spotify - {(await spotify.Playlists.Get(spotifyPlaylistId)).Name}";

        var tracks = await GetSpotifyPlaylistTracksAsync(spotify, spotifyPlaylistId);

        // Check if we had previous missing entries
        var oldMissing = cache.ContainsKey(name) ? cache[name] : new List<string>();

        // Remove previously missing from search if they are still missing
        var searchCandidates = tracks
            .Where(t => !oldMissing.Contains($"{t.Artist} — {t.Title}"))
            .ToList();

        Console.WriteLine(
            $"[Plex Export] Searching {searchCandidates.Count} new tracks, reusing {oldMissing.Count} previous missing entries.");

        // Search in Plex
        var matches = await SearchTracksOnPlexAsync(plexBaseUrl, plexToken, searchCandidates);

        var foundKeys = matches
            .Where(m => m.RatingKey != null)
            .Select(m => m.RatingKey!)
            .ToList();

        var newlyMissing = matches
            .Where(m => m.RatingKey == null)
            .Select(m => $"{m.Artist} — {m.Title}")
            .ToList();

        // Combine old + new missing, then remove duplicates
        var combinedMissing = oldMissing
            .Concat(newlyMissing)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // Create or find existing Plex playlist
        var existingPlaylists = await GetPlexPlaylistsAsync(plexBaseUrl, plexToken);
        var existing = existingPlaylists.FirstOrDefault(p => p.Title.Equals(name, StringComparison.OrdinalIgnoreCase));

        string playlistKey;
        if (!string.IsNullOrEmpty(existing.RatingKey))
        {
            playlistKey = existing.RatingKey;
            Console.WriteLine($"[Plex Export] Extending existing playlist '{name}'");
        }
        else
        {
            playlistKey = await CreatePlaylistAsync(plexBaseUrl, plexToken, name);
            Console.WriteLine($"[Plex Export] Created new playlist '{name}'");
        }

        if (foundKeys.Any()) await AddTracksToPlaylistAsync(plexBaseUrl, plexToken, playlistKey, foundKeys);

        // Update cache
        cache[name] = combinedMissing;
        await File.WriteAllTextAsync(cachePath,
            JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"[Plex Export] {name}: +{foundKeys.Count} added, {combinedMissing.Count} still missing.");

        return (name, foundKeys.Count, combinedMissing);
    }
}
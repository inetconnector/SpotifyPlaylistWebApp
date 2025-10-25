using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using SpotifyAPI.Web;

namespace SpotifyPlaylistWebApp.Services;

/// <summary>
///     Handles Spotify → Plex playlist export and synchronization.
///     All tokens and missing-track cache are stored in memory only.
/// </summary>
public class PlexService
{
    // ============================================================
    // 🔸 Request PIN for Plex authentication
    // ============================================================
    private const string PLEX_CLIENT_ID = "inetconnector-spotify-to-plex";
    private const string PLEX_PRODUCT_NAME = "SpotifyToPlex";
    private const string PLEX_DEVICE = "WebApp";
    private const string PLEX_PLATFORM = "InetConnector Server";
    private const string PLEX_VERSION = "1.0";

    // 🔹 In-memory cache for missing songs (instead of writing JSON files)
    //     Stored as lines: "Artist — Album — Title"  (Album optional)
    private static readonly Dictionary<string, List<string>> _missingCache = new();

    private readonly HttpClient _http;

    public PlexService(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add("Accept", "application/xml");
        _http.DefaultRequestHeaders.Add("User-Agent", "SpotifyToPlex/1.0");
        _http.DefaultRequestHeaders.Add("X-Plex-Product", "SpotifyToPlex");
        _http.DefaultRequestHeaders.Add("X-Plex-Device", "WebApp");
    }

    // ============================================================
    // 🔸 Public helpers for the missing-cache (so du brauchst keine Reflection mehr)
    // ============================================================
    public static void UpdateMissingCache(string playlistName, IEnumerable<(string Artist, string Title, string? Album)> missingTuples)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return;

        var materialized = (missingTuples ?? Enumerable.Empty<(string Artist, string Title, string? Album)>())
            .Select(m => FormatMissingEntry(m.Artist, m.Title, m.Album))
            .Distinct()
            .ToList();

        _missingCache[playlistName] = materialized;
    }

    public static IReadOnlyDictionary<string, List<string>> GetMissingCacheSnapshot()
        => new Dictionary<string, List<string>>(_missingCache);

    private static string FormatMissingEntry(string artist, string title, string? album)
    {
        artist = artist?.Trim() ?? "";
        title = title?.Trim() ?? "";
        album = album?.Trim() ?? "";

        return string.IsNullOrEmpty(album)
            ? $"{artist} — {title}"
            : $"{artist} — {album} — {title}";
    }

    // ✅ Validate Plex token
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

    public async Task<(int PinId, string Code, string ClientId)> CreatePinAsync()
    {
        using var c = new HttpClient();
        c.DefaultRequestHeaders.Add("X-Plex-Product", PLEX_PRODUCT_NAME);
        c.DefaultRequestHeaders.Add("X-Plex-Device", PLEX_DEVICE);
        c.DefaultRequestHeaders.Add("X-Plex-Platform", PLEX_PLATFORM);
        c.DefaultRequestHeaders.Add("X-Plex-Version", PLEX_VERSION);
        c.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", PLEX_CLIENT_ID);
        c.DefaultRequestHeaders.Add("Accept", "application/json");

        var res = await c.PostAsync("https://plex.tv/api/v2/pins?strong=true", null);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetInt32();
        var code = doc.RootElement.GetProperty("code").GetString() ??
                   throw new Exception("Error: No Plex code received.");

        Console.WriteLine($"[Plex CreatePin] Code={code}, ID={id}, Client={PLEX_CLIENT_ID}");
        return (id, code, PLEX_CLIENT_ID);
    }

    // ============================================================
    // 🔸 Poll for Plex token
    // ============================================================
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
                        Console.WriteLine("[Plex Poll] Success → Token received.");
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

    // ============================================================
    // 🔸 Discover Plex servers linked to the user
    // ============================================================
    public async Task<(string BaseUrl, string MachineId)> DiscoverServerAsync(string plexToken)
    {
        var xml = await _http.GetStringAsync($"https://plex.tv/api/resources?includeHttps=1&X-Plex-Token={plexToken}");
        var doc = XDocument.Parse(xml);

        var server = doc.Descendants("Device")
            .FirstOrDefault(d =>
                ((string?)d.Attribute("provides") ?? "").Contains("server", StringComparison.OrdinalIgnoreCase));

        if (server == null)
            throw new Exception("No Plex server found in this account.");

        var connection = server.Elements("Connection")
                             .FirstOrDefault(c =>
                                 c.Attribute("local")?.Value == "0" && c.Attribute("protocol")?.Value == "https")
                         ?? server.Elements("Connection").FirstOrDefault();

        if (connection == null)
            throw new Exception("No valid Plex server connection found.");

        var baseUrl = connection.Attribute("uri")?.Value ?? throw new Exception("No server URI found.");
        var machineId = server.Attribute("machineIdentifier")?.Value ?? "unknown";

        Console.WriteLine($"[Plex Discover] Server={baseUrl}, Machine={machineId}");
        return (baseUrl, machineId);
    }

    // ============================================================
    // 🔸 Fetch Plex playlists
    // ============================================================
    public async Task<List<(string Title, string RatingKey)>> GetPlexPlaylistsAsync(string plexBaseUrl, string plexToken)
    {
        var xml = await _http.GetStringAsync($"{plexBaseUrl}/playlists/all?X-Plex-Token={plexToken}");
        if (string.IsNullOrWhiteSpace(xml))
            return new List<(string, string)>();

        var doc = XDocument.Parse(xml, LoadOptions.None);

        var playlists = doc.Descendants("Playlist")
            .Where(x => ((string?)x.Attribute("playlistType") ?? "")
                .Equals("audio", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                var rawTitle = (string?)x.Attribute("title")
                               ?? (string?)x.Attribute("titleSort")
                               ?? (string?)x.Element("title")
                               ?? "<NoName>";

                var cleanTitle = new string(rawTitle.Where(ch => !char.IsControl(ch)).ToArray())
                    .Replace("\uFE0F", "").Trim();

                var key = (string?)x.Attribute("ratingKey") ?? "";
                return (Title: cleanTitle, RatingKey: key);
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .Distinct()
            .ToList();

        Console.WriteLine("==== PLEX PLAYLISTS (audio) ====");
        foreach (var (t, k) in playlists)
            Console.WriteLine($"  {t} ({k})");
        Console.WriteLine("================================");

        return playlists;
    }

    // ============================================================
    // 🔸 Fetch all tracks from a Spotify playlist (Title + Artist)
    // ============================================================
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

    // ============================================================
    // 🔸 Music-Library Section ermitteln
    // ============================================================
    public async Task<string> GetMusicSectionKeyAsync(string plexBaseUrl, string plexToken)
    {
        var xml = await _http.GetStringAsync($"{plexBaseUrl}/library/sections?X-Plex-Token={plexToken}");
        var doc = XDocument.Parse(xml);

        var dir = doc.Descendants("Directory")
            .FirstOrDefault(d =>
                string.Equals((string?)d.Attribute("type"), "artist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals((string?)d.Attribute("type"), "audio", StringComparison.OrdinalIgnoreCase));

        var key = (string?)dir?.Attribute("key");
        if (string.IsNullOrWhiteSpace(key))
            throw new Exception("Kein Music-Library-Section (artist/audio) gefunden.");

        Console.WriteLine($"[Plex Library] SectionKey={key}");
        return key!;
    }

    // ============================================================
    // 🔸 Verbesserte Track-Suche (Section + Fuzzy + Fallback)
    // ============================================================
    public async Task<List<(string Title, string Artist, string? RatingKey)>> SearchTracksOnPlexAsync(
        string plexBaseUrl, string plexToken, List<(string Title, string Artist)> tracks)
    {
        var results = new List<(string Title, string Artist, string? RatingKey)>();
        string sectionKey = await GetMusicSectionKeyAsync(plexBaseUrl, plexToken);

        foreach (var (titleRaw, artistRaw) in tracks)
        {
            var title = titleRaw ?? "";
            var artist = artistRaw ?? "";
            string? ratingKey = null;

            // 1️⃣ Exakte Filtersuche innerhalb des Music-Sections
            try
            {
                var url1 =
                    $"{plexBaseUrl}/library/sections/{sectionKey}/all" +
                    $"?type=10&track.title={Uri.EscapeDataString(title)}" +
                    $"&artist={Uri.EscapeDataString(artist)}&X-Plex-Token={plexToken}";

                var xml1 = await _http.GetStringAsync(url1);
                var doc1 = XDocument.Parse(xml1);
                ratingKey = doc1.Descendants("Track").FirstOrDefault()?.Attribute("ratingKey")?.Value;
            }
            catch { /* ignorieren */ }

            // 2️⃣ Falls nichts gefunden → freie Section-Suche mit Fuzzy-Matching
            if (ratingKey == null)
            {
                try
                {
                    var q = Uri.EscapeDataString($"{artist} {title}");
                    var url2 = $"{plexBaseUrl}/library/sections/{sectionKey}/search?type=10&query={q}&X-Plex-Token={plexToken}";
                    var xml2 = await _http.GetStringAsync(url2);
                    var doc2 = XDocument.Parse(xml2);

                    var wantedT = Normalize(title);
                    var wantedA = Normalize(artist);

                    var candidates = doc2.Descendants("Track")
                        .Select(t => new
                        {
                            Key = (string?)t.Attribute("ratingKey"),
                            T = Normalize((string?)t.Attribute("title") ?? ""),
                            A = Normalize((string?)t.Attribute("grandparentTitle") ?? (string?)t.Attribute("parentTitle") ?? "")
                        })
                        .Select(c => new
                        {
                            c.Key,
                            c.T,
                            c.A,
                            score = Levenshtein(c.T, wantedT) + Levenshtein(c.A, wantedA)
                        })
                        .OrderBy(x => x.score)
                        .Take(3)
                        .ToList();

                    var best = candidates.FirstOrDefault();
                    if (best != null && best.score < 6)
                        ratingKey = best.Key;
                }
                catch { /* ignorieren */ }
            }

            // 3️⃣ Fallback – globale Suche (wenn alles andere versagt)
            if (ratingKey == null)
            {
                try
                {
                    var q = Uri.EscapeDataString($"{artist} {title}");
                    var url3 = $"{plexBaseUrl}/search?query={q}&type=10&X-Plex-Token={plexToken}";
                    var xml3 = await _http.GetStringAsync(url3);
                    var doc3 = XDocument.Parse(xml3);
                    ratingKey = doc3.Descendants("Track").FirstOrDefault()?.Attribute("ratingKey")?.Value;
                }
                catch { /* ignorieren */ }
            }

            results.Add((titleRaw, artistRaw, ratingKey));
        }

        return results;
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.ToLowerInvariant();

        // Inhalt in Klammern / [] entfernen
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[\(\[].*?[\)\]]\s*", " ");

        // "feat." / "ft." entfernen
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+(feat\.|ft\.)\s+.*$", "");

        // Unicode-Dashes / Sonderzeichen vereinheitlichen
        s = s.Replace('–', '-').Replace('—', '-').Replace('’', '\'');
        s = new string(s.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch == '-' || ch == '\'').ToArray());

        // Whitespace normalisieren
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var v0 = new int[b.Length + 1];
        var v1 = new int[b.Length + 1];
        for (int i = 0; i < v0.Length; i++) v0[i] = i;

        for (int i = 0; i < a.Length; i++)
        {
            v1[0] = i + 1;
            for (int j = 0; j < b.Length; j++)
            {
                int cost = a[i] == b[j] ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }
            Array.Copy(v1, v0, v0.Length);
        }
        return v1[b.Length];
    }

    // ============================================================
    // 🔸 Create new Plex playlist
    // ============================================================
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
            throw new Exception("Failed to create Plex playlist.");

        return key;
    }

    // ============================================================
    // 🔸 Add tracks to existing Plex playlist
    // ============================================================
    public async Task AddTracksToPlaylistAsync(string plexBaseUrl, string plexToken, string playlistKey,
        IEnumerable<string> ratingKeys, string? machineId = null)
    {
        var serverId = string.IsNullOrWhiteSpace(machineId) ? "local" : machineId;

        foreach (var key in ratingKeys)
        {
            var uri = $"server://{serverId}/com.plexapp.plugins.library/library/metadata/{key}";
            var url = $"{plexBaseUrl}/playlists/{playlistKey}/items?uri={Uri.EscapeDataString(uri)}&X-Plex-Token={plexToken}";

            using var req = new HttpRequestMessage(HttpMethod.Put, url);
            req.Headers.ConnectionClose = true;

            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                var msg = await res.Content.ReadAsStringAsync();
                Console.WriteLine($"[Plex AddTrack] Error {res.StatusCode}: {msg}");
            }

            await Task.Delay(200); // leichtes Delay für Plex API Stabilität
        }
    }

    // ============================================================
    // 🔸 Generate CSV of missing tracks (sortiert: Artist → Album → Title)
    // ============================================================
    public static byte[] GenerateMissingCsv(List<string> missing)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.UTF8);

        writer.WriteLine("Artist;Album;Title");

        foreach (var entry in missing
                     .Select(m =>
                     {
                         // Erwartetes Format: "Artist — Title" oder "Artist — Album — Title"
                         var parts = m.Split('—').Select(p => p.Trim()).ToArray();
                         string artist = parts.Length > 0 ? parts[0] : "";
                         string album = parts.Length > 2 ? parts[1] : "";
                         string title = parts.Length > 1 ? parts.Last() : "";
                         return new { artist, album, title };
                     })
                     .OrderBy(x => x.artist)
                     .ThenBy(x => x.album)
                     .ThenBy(x => x.title))
        {
            writer.WriteLine($"{entry.artist};{entry.album};{entry.title}");
        }

        writer.Flush();
        return ms.ToArray();
    }

    // ============================================================
    // 🔸 Load all Spotify playlists for export
    // ============================================================
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

    // ============================================================
    // 🔸 Export one Spotify playlist to Plex (with memory cache)
    // ============================================================
    public async Task<(string ExportedName, int AddedCount, List<string> Missing)> ExportOnePlaylistAsync(
        SpotifyClient spotify, string spotifyPlaylistId, string? explicitExportName,
        string plexBaseUrl, string plexToken)
    {
        var name = !string.IsNullOrWhiteSpace(explicitExportName)
            ? explicitExportName.Trim()
            : $"Spotify - {(await spotify.Playlists.Get(spotifyPlaylistId)).Name}";

        // Load missing entries from memory
        _missingCache.TryGetValue(name, out var oldMissing);
        oldMissing ??= new List<string>();

        var tracks = await GetSpotifyPlaylistTracksAsync(spotify, spotifyPlaylistId);
        var searchCandidates = tracks.Where(t => !oldMissing.Contains($"{t.Artist} — {t.Title}")).ToList();

        Console.WriteLine(
            $"[Plex Export] Searching {searchCandidates.Count} new tracks, reusing {oldMissing.Count} missing.");

        var matches = await SearchTracksOnPlexAsync(plexBaseUrl, plexToken, searchCandidates);

        var foundKeys = matches.Where(m => m.RatingKey != null).Select(m => m.RatingKey!).ToList();
        var newlyMissing = matches.Where(m => m.RatingKey == null).Select(m => $"{m.Artist} — {m.Title}").ToList();

        // Merge old + new missing and deduplicate
        var combinedMissing = oldMissing.Concat(newlyMissing).Distinct().OrderBy(x => x).ToList();

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

        if (foundKeys.Any())
            await AddTracksToPlaylistAsync(plexBaseUrl, plexToken, playlistKey, foundKeys);

        // Save to memory cache
        _missingCache[name] = combinedMissing;

        Console.WriteLine($"[Plex Export] {name}: +{foundKeys.Count} added, {combinedMissing.Count} still missing.");
        return (name, foundKeys.Count, combinedMissing);
    }

    // ============================================================
    // 🔸 Get tracks (XML → readable list) from Plex playlist
    // ============================================================
    public async Task<List<(string Title, string Artist)>> GetPlaylistTracksAsync(
        string plexBaseUrl, string plexToken, string ratingKey)
    {
        var xml = await _http.GetStringAsync($"{plexBaseUrl}/playlists/{ratingKey}/items?X-Plex-Token={plexToken}");
        var doc = XDocument.Parse(xml);

        return doc.Descendants("Track")
            .Select(x => (
                (string?)x.Attribute("title") ?? "Untitled",
                (string?)x.Attribute("grandparentTitle") ?? "Unknown"
            ))
            .ToList();
    }

    // ============================================================
    // 🔸 Raw XML (used for AJAX return)
    // ============================================================
    public async Task<List<object>> GetPlaylistTracksXmlAsync(string plexBaseUrl, string plexToken, string ratingKey)
    {
        var tracks = await GetPlaylistTracksAsync(plexBaseUrl, plexToken, ratingKey);
        return tracks.Select(t => new { title = t.Title, artist = t.Artist }).ToList<object>();
    }

    // ============================================================
    // 🔸 Delete a Plex playlist
    // ============================================================
    public async Task DeletePlaylistAsync(string plexBaseUrl, string plexToken, string ratingKey)
    {
        var url = $"{plexBaseUrl}/playlists/{ratingKey}?X-Plex-Token={plexToken}";
        var res = await _http.DeleteAsync(url);
        res.EnsureSuccessStatusCode();
        Console.WriteLine($"[Plex Delete] Playlist {ratingKey} deleted.");
    }

    public async Task<string> GetRawPlaylistsXmlAsync(string plexBaseUrl, string plexToken)
    {
        var xml = await _http.GetStringAsync($"{plexBaseUrl}/playlists/all?X-Plex-Token={plexToken}");
        return xml;
    }
}

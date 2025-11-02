using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
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
    private static readonly Dictionary<string, (List<string> Items, DateTime Created)> _missingCache = new();


    // ============================================================
    // 🔸 SSE (Server-Sent Events) Support for live export feedback
    // ============================================================
    private static readonly ConcurrentDictionary<string, StreamWriter> _sseStreams = new();
    private readonly MissingCacheStore _cacheStore;

    private readonly HttpClient _http;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PlexService(IStringLocalizer<SharedResource> localizer,
        IWebHostEnvironment env,
        IHttpClientFactory httpClientFactory)
    {
        _localizer = localizer;
        _http = httpClientFactory.CreateClient();
        _http.DefaultRequestHeaders.Add("Accept", "application/xml");
        _http.DefaultRequestHeaders.Add("User-Agent", "SpotifyToPlex/1.0");
        _http.DefaultRequestHeaders.Add("X-Plex-Product", "SpotifyToPlex");
        _http.DefaultRequestHeaders.Add("X-Plex-Device", "WebApp");

        _cacheStore = new MissingCacheStore(env);
    }

    /// <summary>
    ///     Sends a single SSE message (text line) to the given export session.
    /// </summary>
    internal async Task SendSseAsync(string exportId, string message)
    {
        if (string.IsNullOrEmpty(exportId))
            return;

        if (_sseStreams.TryGetValue(exportId, out var writer))
            try
            {
                await writer.WriteAsync($"data: {message}\n\n");
                await writer.FlushAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SSE] ❌ Failed to send message: {ex.Message}");
            }
    }

    /// <summary>
    ///     Registers a new SSE stream for a given export session.
    /// </summary>
    public static void RegisterSseStream(string exportId, StreamWriter writer)
    {
        _sseStreams[exportId] = writer;
    }

    /// <summary>
    ///     Unregisters and closes an SSE stream after export completes.
    /// </summary>
    public static void UnregisterSseStream(string exportId)
    {
        if (_sseStreams.TryRemove(exportId, out var writer))
            try
            {
                writer.Dispose();
            }
            catch
            {
                /* ignored */
            }
    }


    // ============================================================
    // 🔸 Missing cache: load/save per Plex server id (wwwroot/data/<serverId>)
    // ============================================================
    public void LoadMissingCacheForServer(string plexServerId)
    {
        var disk = _cacheStore.Load(plexServerId);
        _missingCache.Clear();
        foreach (var kv in disk)
            _missingCache[kv.Key] = (kv.Value.Items ?? new List<string>(), kv.Value.Updated);
        Console.WriteLine(
            $"[Plex Cache] Loaded missing cache for server {plexServerId}: {_missingCache.Count} playlists.");
    }

    public void SaveMissingCacheForServer(string plexServerId)
    {
        var map = _missingCache.ToDictionary(k => k.Key, v => new MissingCacheStore.CacheEntry
        {
            Items = v.Value.Items,
            Created = v.Value.Created,
            Updated = DateTime.UtcNow
        });
        _cacheStore.Save(plexServerId, map);
        Console.WriteLine($"[Plex Cache] Saved missing cache for server {plexServerId}.");
    }

    public static IReadOnlyDictionary<string, (List<string> Items, DateTime Created)> GetMissingCacheSnapshot()
    {
        return new Dictionary<string, (List<string>, DateTime)>(_missingCache);
    }

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

    public async Task<(string BaseUrl, string MachineId)> DiscoverServerAsync(string plexToken)
    {
        var xml = await _http.GetStringAsync($"https://plex.tv/api/resources?includeHttps=1&X-Plex-Token={plexToken}");
        var doc = XDocument.Parse(xml);

        var server = doc.Descendants("Device")
            .FirstOrDefault(d => ((string?)d.Attribute("provides") ?? "")
                .Contains("server", StringComparison.OrdinalIgnoreCase));

        if (server == null)
            throw new Exception("No Plex server found in this account.");

        var connection = server.Elements("Connection")
                             .FirstOrDefault(c =>
                                 c.Attribute("local")?.Value == "0" && c.Attribute("protocol")?.Value == "https")
                         ?? server.Elements("Connection").FirstOrDefault();

        if (connection == null)
            throw new Exception("No valid Plex server connection found.");

        var baseUrl = connection.Attribute("uri")?.Value ?? throw new Exception("No server URI found.");
        var machineId = server.Attribute("machineIdentifier")?.Value;

        // 🩹 Wenn Plex keine Machine-ID liefert, hole sie direkt vom Server
        if (string.IsNullOrWhiteSpace(machineId) || machineId.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            try
            {
                var xmlInfo = await _http.GetStringAsync($"{baseUrl}/?X-Plex-Token={plexToken}");
                var docInfo = XDocument.Parse(xmlInfo);
                machineId = docInfo.Root?.Attribute("machineIdentifier")?.Value ?? "unknown";

                Console.WriteLine($"[Plex Discover] MachineIdentifier fetched from server: {machineId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plex Discover] ⚠️ Failed to fetch local machineIdentifier: {ex.Message}");

                // Versuch 2: Fallback aus plex.direct-Host
                var host = new Uri(baseUrl).Host;
                var maybeId = host.Split('.').FirstOrDefault();
                if (!string.IsNullOrEmpty(maybeId) && maybeId.Length > 10)
                {
                    machineId = maybeId;
                    Console.WriteLine($"[Plex Discover] Fallback machineId extracted from baseUrl: {machineId}");
                }
                else
                {
                    machineId = "unknown";
                }
            }

        Console.WriteLine($"[Plex Discover] Server={baseUrl}, Machine={machineId}");
        return (baseUrl, machineId ?? "unknown");
    }


    // ============================================================
    // 🔸 Fetch Plex playlists
    // ============================================================
    public async Task<List<(string Title, string RatingKey)>> GetPlexPlaylistsAsync(string plexBaseUrl,
        string plexToken)
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
            // 🔹 Filter system playlists
            .Where(p =>
                !string.Equals(p.Title, "All Music", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(p.Title, "Recently Added", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(p.Title, "Recently Played", StringComparison.OrdinalIgnoreCase))
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .Distinct()
            .ToList();

        Console.WriteLine("==== PLEX PLAYLISTS (audio) ====");
        foreach (var (t, k) in playlists)
            Console.WriteLine($"  {t} ({k})");
        Console.WriteLine("================================");

        return playlists;
    }

    /// <summary>
    ///     Get all saved ("Liked") tracks of the user.
    /// </summary>
    public async Task<List<(string Title, string Artist)>> GetSpotifyLikedTracksAsync(SpotifyClient spotify)
    {
        var tracks = new List<(string Title, string Artist)>();
        var page = await spotify.Library.GetTracks(new LibraryTracksRequest { Limit = 50 });

        while (true)
        {
            foreach (var item in page.Items)
            {
                var t = item.Track;
                if (t == null) continue;

                var artist = t.Artists.FirstOrDefault()?.Name ?? "";
                tracks.Add((t.Name, artist));
            }

            if (string.IsNullOrEmpty(page.Next)) break;
            page = await spotify.NextPage(page);
        }

        return tracks;
    }


    // ============================================================
    // 🔸 Fetch all tracks from a Spotify playlist (Title + Artist)
    // ============================================================
    public async Task<List<(string Title, string Artist)>> GetSpotifyPlaylistTracksAsync(SpotifyClient spotify,
        string playlistId)
    {
        // ✅ Handle pseudo-ID for liked songs
        if (playlistId == "#LikedSongs#")
            return await GetSpotifyLikedTracksAsync(spotify);

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
    // 🔸 Verbesserte Track-Suche (mit SectionKey + ServerMachineId)
    // ============================================================
    // ============================================================
    // 🔸 Präzise Track-Suche auf Plex (fokussiert auf grandparentTitle)
    // ============================================================
    public async Task<List<(string Title, string Artist, string? RatingKey, string SectionKey, string ServerMachineId)>>
        SearchTracksOnPlexAsync(string plexBaseUrl, string plexToken, List<(string Title, string Artist)> tracks)
    {
        var results =
            new List<(string Title, string Artist, string? RatingKey, string SectionKey, string ServerMachineId)>();

        var sectionKey = await GetMusicSectionKeyAsync(plexBaseUrl, plexToken);
        var (_, defaultMachineId) = await DiscoverServerAsync(plexToken);

        foreach (var (titleRaw, artistRaw) in tracks)
        {
            var title = titleRaw ?? "";
            var artist = artistRaw ?? "";
            string? ratingKey = null;
            var foundSection = sectionKey;
            var foundMachineId = defaultMachineId;

            try
            {
                // 1️⃣ Exakte Suche innerhalb der Musik-Section
                var url1 = $"{plexBaseUrl}/library/sections/{sectionKey}/all" +
                           $"?type=10&track.title={Uri.EscapeDataString(title)}" +
                           $"&artist={Uri.EscapeDataString(artist)}&X-Plex-Token={plexToken}";

                var xml1 = await _http.GetStringAsync(url1);
                var doc1 = XDocument.Parse(xml1);
                var trackNode = doc1.Descendants("Track")
                    .FirstOrDefault(t =>
                        string.Equals(
                            (string?)t.Attribute("grandparentTitle") ?? "",
                            artist,
                            StringComparison.OrdinalIgnoreCase));

                if (trackNode != null)
                {
                    ratingKey = trackNode.Attribute("ratingKey")?.Value;
                    foundSection =
                        trackNode.Ancestors("MediaContainer").FirstOrDefault()?.Attribute("librarySectionID")?.Value ??
                        sectionKey;
                    foundMachineId =
                        trackNode.Ancestors("MediaContainer").FirstOrDefault()?.Attribute("machineIdentifier")?.Value ??
                        defaultMachineId;

                    Console.WriteLine($"[ExactMatch] ✅ {artist} – {title} ({ratingKey})");
                }
            }
            catch
            {
                /* ignore */
            }

            // 2️⃣ Fuzzy-Suche als Fallback (nur grandparentTitle!)
            if (ratingKey == null)
                try
                {
                    var q = Uri.EscapeDataString($"{artist} {title}");
                    var url2 =
                        $"{plexBaseUrl}/library/sections/{sectionKey}/search?type=10&query={q}&X-Plex-Token={plexToken}";
                    var xml2 = await _http.GetStringAsync(url2);
                    var doc2 = XDocument.Parse(xml2);

                    var wantedT = Normalize(title);
                    var wantedA = Normalize(artist);

                    var candidates = doc2.Descendants("Track")
                        .Select(t => new
                        {
                            Key = (string?)t.Attribute("ratingKey"),
                            Title = (string?)t.Attribute("title") ?? "",
                            Artist = (string?)t.Attribute("grandparentTitle") ?? "",
                            Section = (string?)t.Ancestors("MediaContainer").FirstOrDefault()
                                ?.Attribute("librarySectionID"),
                            ServerId = (string?)t.Ancestors("MediaContainer").FirstOrDefault()
                                ?.Attribute("machineIdentifier")
                        })
                        .Where(c => !string.IsNullOrWhiteSpace(c.Artist) &&
                                    !c.Artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase))
                        .Select(c => new
                        {
                            c.Key,
                            c.Title,
                            c.Artist,
                            c.Section,
                            c.ServerId,
                            Score = Levenshtein(Normalize(c.Title), wantedT) + Levenshtein(Normalize(c.Artist), wantedA)
                        })
                        .OrderBy(x => x.Score)
                        .FirstOrDefault();

                    if (candidates != null && candidates.Score < 3)
                    {
                        ratingKey = candidates.Key;
                        foundSection = candidates.Section ?? sectionKey;
                        foundMachineId = candidates.ServerId ?? defaultMachineId;

                        Console.WriteLine(
                            $"[FuzzyMatch] 🎯 {artist} – {title} → {candidates.Artist} – {candidates.Title} (Score={candidates.Score})");
                    }
                }
                catch
                {
                    /* ignore */
                }

            // 3️⃣ Globaler Fallback (wenn nichts gefunden)
            if (ratingKey == null)
                try
                {
                    var q = Uri.EscapeDataString($"{artist} {title}");
                    var url3 = $"{plexBaseUrl}/search?query={q}&type=10&X-Plex-Token={plexToken}";
                    var xml3 = await _http.GetStringAsync(url3);
                    var doc3 = XDocument.Parse(xml3);
                    var track = doc3.Descendants("Track")
                        .FirstOrDefault(t => string.Equals((string?)t.Attribute("grandparentTitle") ?? "", artist,
                            StringComparison.OrdinalIgnoreCase));

                    if (track != null)
                    {
                        ratingKey = track.Attribute("ratingKey")?.Value;
                        foundSection =
                            track.Ancestors("MediaContainer").FirstOrDefault()?.Attribute("librarySectionID")?.Value ??
                            sectionKey;
                        foundMachineId =
                            track.Ancestors("MediaContainer").FirstOrDefault()?.Attribute("machineIdentifier")?.Value ??
                            defaultMachineId;

                        Console.WriteLine($"[Fallback] ✅ {artist} – {title} ({ratingKey})");
                    }
                }
                catch
                {
                    /* ignore */
                }

            results.Add((titleRaw, artistRaw, ratingKey, foundSection, foundMachineId)!);

            if (ratingKey == null)
                Console.WriteLine($"[NoMatch] ❌ {artist} – {title}");
        }

        return results;
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.ToLowerInvariant();

        // Inhalt in Klammern / [] entfernen
        s = Regex.Replace(s, @"\s*[\(\[].*?[\)\]]\s*", " ");

        // "feat." / "ft." entfernen
        s = Regex.Replace(s, @"\s+(feat\.|ft\.)\s+.*$", "");

        // Unicode-Dashes / Sonderzeichen vereinheitlichen
        s = s.Replace('–', '-').Replace('—', '-').Replace('’', '\'');
        s = new string(s.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch == '-' || ch == '\'')
            .ToArray());

        // Whitespace normalisieren
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var v0 = new int[b.Length + 1];
        var v1 = new int[b.Length + 1];
        for (var i = 0; i < v0.Length; i++) v0[i] = i;

        for (var i = 0; i < a.Length; i++)
        {
            v1[0] = i + 1;
            for (var j = 0; j < b.Length; j++)
            {
                var cost = a[i] == b[j] ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }

            Array.Copy(v1, v0, v0.Length);
        }

        return v1[b.Length];
    }

    // ============================================================
    // 🔸 Optimized AddTracksToPlaylistAsync — Batch Upload Version
    // ============================================================
    public async Task<bool> AddTracksToPlaylistAsync(
        string plexBaseUrl,
        string plexToken,
        string playlistKey,
        IEnumerable<string> ratingKeys,
        string machineId,
        string exportId = "")
    {
        if (string.IsNullOrWhiteSpace(machineId))
            throw new ArgumentException("machineId must not be empty.", nameof(machineId));

        var keys = ratingKeys?
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct()
            .ToList() ?? new List<string>();

        if (keys.Count == 0)
        {
            Console.WriteLine("[Plex AddTracks] ⚠️ No track keys provided.");
            return false;
        }

        const int chunkSize = 50; // safe batch size for Plex
        var total = keys.Count;
        var success = true;
        var processed = 0;

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "inetconnector-spotify-to-plex");
        client.DefaultRequestHeaders.Add("X-Plex-Product", "SpotifyToPlex");
        client.DefaultRequestHeaders.Add("X-Plex-Version", "1.0");
        client.DefaultRequestHeaders.Add("X-Plex-Platform", "Web");
        client.DefaultRequestHeaders.Add("X-Plex-Platform-Version", "ASP.NET-Core");
        client.DefaultRequestHeaders.Add("X-Plex-Device", "InetConnector Cloud");
        client.DefaultRequestHeaders.Add("X-Plex-Device-Name", "SpotifyToPlex WebApp");

        for (int i = 0; i < total; i += chunkSize)
        {
            var batch = keys.Skip(i).Take(chunkSize).ToList();
            var uris = batch.Select(k =>
                $"server://{machineId}/com.plexapp.plugins.library/library/metadata/{k}");
            var uriString = string.Join(",", uris);

            var url =
                $"{plexBaseUrl}/playlists/{playlistKey}/items" +
                $"?uri={Uri.EscapeDataString(uriString)}" +
                $"&type=audio&X-Plex-Token={Uri.EscapeDataString(plexToken)}";

            Console.WriteLine($"[Plex AddTracks] ➕ Adding {batch.Count} tracks ({i + 1}–{i + batch.Count} of {total}) …");

            var req = new HttpRequestMessage(HttpMethod.Put, url);
            var res = await client.SendAsync(req);
            var msg = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode || (!msg.Contains("\"leafCountAdded\"") && !msg.Contains("\"size\"")))
            {
                Console.WriteLine($"[Plex AddTracks] ❌ Batch failed ({res.StatusCode}): {msg}");
                success = false;
            }
            else
            {
                Console.WriteLine($"[Plex AddTracks] ✅ Added {batch.Count} tracks to playlist {playlistKey}");
            }

            processed += batch.Count;

            // Optional: progress event back to browser (if using SSE)
            if (!string.IsNullOrEmpty(exportId))
            {
                try
                {
                    await SendSseAsync(exportId, $"progress:{processed}:0:{total}");
                }
                catch { /* ignore SSE errors */ }
            }

            // tiny delay avoids Plex rate-limit throttling
            await Task.Delay(200);
        }

        Console.WriteLine($"[Plex AddTracks] Done. Added {processed}/{total} tracks. Success={success}");
        return success;
    }


    // ============================================================
    // 🔸 Get Plex Server Identity (machineIdentifier, version, claimed)
    // ============================================================
    public async Task<string?> GetServerIdentityAsync(string plexBaseUrl, string plexToken)
    {
        try
        {
            var url = $"{plexBaseUrl}/identity?X-Plex-Token={plexToken}";
            var json = await _http.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.GetProperty("MediaContainer");
            var id = root.GetProperty("machineIdentifier").GetString();

            Console.WriteLine($"[Plex Identity] machineIdentifier={id}");
            return id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Plex Identity] Failed: {ex.Message}");
            return null;
        }
    }


    // ============================================================
    // 🔸 Generate CSV of missing tracks (sortiert: Artist → Album → Title)
    // ============================================================
    public static byte[] GenerateMissingCsv(List<string> missing, string playlistName)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8);

        writer.WriteLine("Playlist;Artist;Album;Title");

        foreach (var entry in missing
                     .Select(m =>
                     {
                         // Erwartetes Format: "Artist — Title" oder "Artist — Album — Title"
                         var parts = m.Split('—').Select(p => p.Trim()).ToArray();
                         var artist = parts.Length > 0 ? parts[0] : "";
                         var album = parts.Length > 2 ? parts[1] : "";
                         var title = parts.Length > 1 ? parts.Last() : "";
                         return new { artist, album, title };
                     })
                     .OrderBy(x => x.artist)
                     .ThenBy(x => x.album)
                     .ThenBy(x => x.title))
            writer.WriteLine($"{entry.artist};{entry.album};{entry.title}");

        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>
    ///     Loads all Spotify playlists of the current user and returns them as SelectListItem objects for dropdowns.
    /// </summary>
    public async Task<List<SelectListItem>> GetAllSpotifyPlaylistsAsync(SpotifyClient spotify)
    {
        var list = new List<SelectListItem>();

        try
        {
            var page = await spotify.Playlists.CurrentUsers(new PlaylistCurrentUsersRequest { Limit = 50 });

            while (true)
            {
                if (page.Items != null)
                    foreach (var pl in page.Items)
                    {
                        if (pl == null || string.IsNullOrEmpty(pl.Id)) continue;

                        list.Add(new SelectListItem
                        {
                            Value = pl.Id,
                            Text = pl.Name ?? "(Unnamed Playlist)"
                        });
                    }

                if (string.IsNullOrEmpty(page.Next))
                    break;

                page = await spotify.NextPage(page);
            }
        }
        catch (APIException apiEx)
        {
            Console.WriteLine($"[PlexService] Spotify API error while loading playlists: {apiEx.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlexService] Unexpected error while loading playlists: {ex}");
        }

        return list;
    }

    // ============================================================
    // 🔸 Export one Spotify playlist to Plex (with memory cache + timestamps)
    // ============================================================
    public async Task<(string ExportedName, int AddedCount, List<string> Missing)> ExportOnePlaylistAsync(
        SpotifyClient spotify,
        string spotifyPlaylistId,
        string? explicitExportName,
        string plexBaseUrl,
        string plexToken)
    {
        var name = !string.IsNullOrWhiteSpace(explicitExportName)
            ? explicitExportName.Trim()
            : $"Spotify - {(await spotify.Playlists.Get(spotifyPlaylistId)).Name}";

        // ============================================================
        // 🧠 Load missing entries (Tuple version: Items + Created)
        // ============================================================
        List<string> oldMissing;
        if (_missingCache.TryGetValue(name, out var cacheEntry))
            oldMissing = cacheEntry.Items ?? new List<string>();
        else
            oldMissing = new List<string>();

        var tracks = await GetSpotifyPlaylistTracksAsync(spotify, spotifyPlaylistId);

        // Filter: überspringe alte Missing-Einträge
        var searchCandidates = tracks
            .Where(t => !oldMissing.Contains($"{t.Artist} — {t.Title}"))
            .ToList();

        Console.WriteLine(
            $"[Plex Export] Searching {searchCandidates.Count} new tracks, reusing {oldMissing.Count} missing.");

        // ============================================================
        // 🔎 Suche in Plex: liefert RatingKey + SectionKey + MachineId
        // ============================================================
        var matches = await SearchTracksOnPlexAsync(plexBaseUrl, plexToken, searchCandidates);

        var found = matches.Where(m => m.RatingKey != null).ToList();
        var foundKeys = found.Select(m => m.RatingKey!).ToList();

        var newlyMissing = matches
            .Where(m => m.RatingKey == null)
            .Select(m => $"{m.Artist} — {m.Title}")
            .ToList();

        // Merge old + new missing and deduplicate
        var combinedMissing = oldMissing.Concat(newlyMissing)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // ============================================================
        // 🎵 Playlist anlegen oder erweitern
        // ============================================================
        var existingPlaylists = await GetPlexPlaylistsAsync(plexBaseUrl, plexToken);
        var existing = existingPlaylists.FirstOrDefault(p =>
            p.Title.Equals(name, StringComparison.OrdinalIgnoreCase));

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

        // ============================================================
        // 🧩 Gruppiere nach MachineId → effizienter Batch-Upload
        // ============================================================
        var (_, defaultMachineId) = await DiscoverServerAsync(plexToken);
        var groupedByServer = found.GroupBy(m => m.ServerMachineId ?? defaultMachineId);

        var addedCount = 0;

        foreach (var group in groupedByServer)
        {
            var groupKeys = group.Select(m => m.RatingKey!).ToList();
            var machineId = group.Key;

            Console.WriteLine($"[Plex Export] Adding {groupKeys.Count} tracks from machine {machineId}...");
            var success = await AddTracksToPlaylistAsync(plexBaseUrl, plexToken, playlistKey, groupKeys, machineId);

            if (success)
            {
                addedCount += groupKeys.Count;
                Console.WriteLine($"[Plex Export] ✅ {groupKeys.Count} added from server {machineId}");
            }
            else
            {
                Console.WriteLine($"[Plex Export] ⚠️ Some tracks failed for server {machineId}");
            }
        }

        // ============================================================
        // 💾 Save to memory cache (with timestamp)
        // ============================================================
        _missingCache[name] = (combinedMissing, DateTime.UtcNow);
        SaveMissingCacheToFile();

        Console.WriteLine($"[Plex Export] {name}: +{addedCount} added, {combinedMissing.Count} still missing.");
        return (name, addedCount, combinedMissing);
    }

    public static void UpdateMissingCache(string playlistName,
        IEnumerable<(string Artist, string Title, string? Album)> missingTuples)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return;

        var materialized = (missingTuples ?? Enumerable.Empty<(string, string, string?)>())
            .Select(m => FormatMissingEntry(m.Artist, m.Title, m.Album))
            .Distinct()
            .ToList();

        _missingCache[playlistName] = (materialized, DateTime.UtcNow);
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
    // ============================================================
    // 🔸 Create new Plex playlist (clean + fixed version)
    // ============================================================
    public async Task<string> CreatePlaylistAsync(string plexBaseUrl, string plexToken, string name)
    {
        // 🧹 Remove any trailing "(...)" section from the name (e.g. "(❗13 missing)")
        name = Regex.Replace(name ?? "", @"\s*\([^)]*\)\s*$", "").Trim();

        // ✅ Append date suffix in format _Spotify_YYYY-MM-DD
        var dateSuffix = $"_Spotify_{DateTime.Now:yyyy-MM-dd}";
        if (!name.EndsWith(dateSuffix, StringComparison.OrdinalIgnoreCase))
            name += dateSuffix;

        // Discover music section for correct URI
        var sectionKey = await GetMusicSectionKeyAsync(plexBaseUrl, plexToken);

        // Fallback URI – Plex requires a valid item reference (dummy item)
        var uri = $"library://{sectionKey}/item/0";

        var url =
            $"{plexBaseUrl}/playlists?" +
            $"type=audio" +
            $"&title={Uri.EscapeDataString(name)}" +
            $"&smart=0" +
            $"&uri={Uri.EscapeDataString(uri)}" +
            $"&X-Plex-Token={plexToken}";

        var res = await _http.PostAsync(url, null);
        if (!res.IsSuccessStatusCode)
        {
            var msg = await res.Content.ReadAsStringAsync();
            throw new Exception($"CreatePlaylist failed: {res.StatusCode} – {msg}");
        }

        var xml = await res.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        var key = doc.Descendants("Playlist").FirstOrDefault()?.Attribute("ratingKey")?.Value;

        if (string.IsNullOrEmpty(key))
            throw new Exception("Failed to parse created playlist key.");

        Console.WriteLine($"[Plex] Created playlist '{name}' key={key}");
        return key;
    }
    // ============================================================
    // 🔸 Auto-clean old missing cache entries (>1 day)
    // ============================================================
    public static void CleanupOldMissingCache(TimeSpan? maxAge = null)
    {
        var threshold = DateTime.UtcNow - (maxAge ?? TimeSpan.FromDays(1));
        var removed = new List<string>();

        // 🧹 Normalize keys on the fly and remove old entries
        foreach (var key in _missingCache.Keys.ToList())
        {
            var normalized = Regex.Replace(key ?? "", @"\s*\([^)]*\)\s*$", "").Trim();

            // Merge duplicate entries under normalized key
            if (normalized != key)
            {
                if (_missingCache.TryGetValue(normalized, out var existing))
                {
                    var combined = existing.Items
                        .Concat(_missingCache[key].Items)
                        .Distinct()
                        .ToList();

                    _missingCache[normalized] = (combined, DateTime.UtcNow);
                }
                else
                {
                    _missingCache[normalized] = _missingCache[key];
                }

                _missingCache.Remove(key);
                removed.Add(key);
            }
            else if (_missingCache[key].Created < threshold)
            {
                _missingCache.Remove(key);
                removed.Add(key);
            }
        }

        if (removed.Count > 0)
        {
            SaveMissingCacheToFile();
            Console.WriteLine($"[Plex Cache] 🧹 Cleaned {removed.Count} old or duplicate entries (>1d): {string.Join(", ", removed)}");
        }
    }

    // ============================================================
    // 🔸 Save missing cache to file
    // ============================================================
    public static void SaveMissingCacheToFile()
    {
        try
        {
            // Normalize all keys before saving
            var normalizedCache = new Dictionary<string, (List<string> Items, DateTime Created)>();
            foreach (var kv in _missingCache)
            {
                var key = Regex.Replace(kv.Key ?? "", @"\s*\([^)]*\)\s*$", "").Trim();
                normalizedCache[key] = kv.Value;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "missing_cache.json");
            File.WriteAllText(path, JsonSerializer.Serialize(normalizedCache, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            Console.WriteLine($"[Plex Cache] Saved missing cache to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Plex Cache] ⚠️ Failed to save missing cache: {ex.Message}");
        }
    }

    // ============================================================
    // 🔸 Remove missing entry (by normalized key)
    // ============================================================
    public void RemoveMissingEntry(string plexServerId, string playlistName)
    {
        // normalize playlist name before removal
        var key = Regex.Replace(playlistName ?? "", @"\s*\([^)]*\)\s*$", "").Trim();

        if (_missingCache.Remove(key))
            SaveMissingCacheForServer(plexServerId);
    }

}
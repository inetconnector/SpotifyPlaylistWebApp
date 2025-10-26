using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SpotifyAPI.Web;
using SpotifyPlaylistWebApp.Services;
using System.Reflection;
using System.Text.Json;
using System.Linq;
namespace SpotifyPlaylistWebApp.Controllers;

[Route("Home")]
[Route("HomePlex")]
public class HomePlexController : Controller
{
    private const string SessionSpotifyTokenKey = "SpotifyAccessToken";
    private readonly IStringLocalizer<SharedResource> _localizer;

    public HomePlexController(IStringLocalizer<SharedResource> localizer)
    {
        _localizer = localizer;
    }

    // ==============================================================
    // 🔸 Main page after Plex + Spotify login
    // ==============================================================
    [HttpGet("SpotifyToPlex")]
    public async Task<IActionResult> SpotifyToPlex([FromServices] PlexService plex)
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);

        if (string.IsNullOrEmpty(plexToken))
            return RedirectToAction("PlexActions");

        if (string.IsNullOrEmpty(spotifyToken))
        {
            HttpContext.Session.SetString("ReturnAfterLogin", "/HomePlex/SpotifyToPlex");
            return RedirectToAction("Login", "Home");
        }

        try
        {
            // ✅ Load all user playlists for selection
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
            var playlists = await plex.GetAllSpotifyPlaylistsAsync(spotify);

            ViewBag.Playlists = playlists;
            return View("PlexActions");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SpotifyToPlex] " + ex);
            TempData["Error"] = _localizer["Plex_StatusLoadError"].Value;
            return RedirectToAction("Index", "Home");
        }
    }

    // ==============================================================
    // 🔸 Polling endpoint for Plex login confirmation
    // ==============================================================
    [HttpGet("CheckPlexAuth")]
    public async Task<IActionResult> CheckPlexAuth([FromServices] PlexService plex)
    {
        var pinId = HttpContext.Session.GetInt32("PlexPinId");
        var clientId = HttpContext.Session.GetString("PlexClientId");

        if (pinId == null || string.IsNullOrEmpty(clientId))
            return Json(new { success = false });

        var token = await plex.PollPinAsync(pinId.Value, clientId);
        if (string.IsNullOrEmpty(token))
            return Json(new { success = false });

        // Save Plex token and continue with Spotify login
        HttpContext.Session.SetString("PlexAuthToken", token);
        HttpContext.Session.SetString("ReturnAfterLogin", "/HomePlex/SpotifyToPlex");

        return RedirectToAction("Login", "Home");
    }

    // ==============================================================
    // 🔸 Disconnect Plex manually
    // ==============================================================
    [HttpPost("DisconnectPlex")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectPlex([FromServices] IPlexTokenStore tokenStore)
    {
        try
        {
            var userKey = HttpContext.Session.Id;
            await tokenStore.DeleteAsync(userKey);

            HttpContext.Session.Remove("PlexAuthToken");
            HttpContext.Session.Remove("PlexBaseUrl");
            HttpContext.Session.Remove("PlexMachineId");

            TempData["Info"] = _localizer["Plex_Removed"].Value;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Plex Disconnect] " + ex);
            TempData["Error"] = _localizer["Plex_DisconnectError"].Value;
        }

        return RedirectToAction("Index", "Home");
    }

    // ==============================================================
    // 🔸 Export one selected playlist to Plex
    // ==============================================================
    [HttpPost("ExportOne")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportOne([FromServices] PlexService plex, string playlistId, string playlistName)
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);

        if (string.IsNullOrEmpty(plexToken) || string.IsNullOrEmpty(spotifyToken))
            return RedirectToAction("SpotifyToPlex");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // timeout 30s
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
            var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);

            var (exportedName, addedCount, missing) =
                await plex.ExportOnePlaylistAsync(spotify, playlistId, playlistName, baseUrl, plexToken);

            TempData["ExportOneResult"] = JsonSerializer.Serialize(new
            {
                exportedName,
                addedCount,
                missingCount = missing.Count
            });

            return RedirectToAction("ExportResult");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ExportOne] ERROR: " + ex);
            TempData["ErrorMessage"] = $"Export-Error: {ex.Message}";
            return RedirectToAction("SpotifyToPlex");
        }
    }
    // ============================================================
    // 🔸 Live Export — localized event messages
    // ============================================================
    [HttpGet("ExportOneLive")]
    public async Task ExportOneLive([FromServices] PlexService plex,
        [FromServices] IStringLocalizer<SharedResource> L,
        string playlistId, string playlistName)
    {
        var missingList = new List<(string Artist, string Title, string? Album)>();

        Response.ContentType = "text/event-stream";
        Response.Headers.Add("Cache-Control", "no-cache");
        await using var writer = new StreamWriter(Response.Body);

        try
        {
            var spotifyToken = HttpContext.Session.GetString("SpotifyAccessToken");
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            if (spotifyToken == null || plexToken == null)
            {
                await writer.WriteLineAsync($"data: ERROR {L["SpotifyToPlex_TokenExpired"]}\n");
                await writer.FlushAsync();
                return;
            }

            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
            var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);
            var tracks = await plex.GetSpotifyPlaylistTracksAsync(spotify, playlistId);

            await writer.WriteLineAsync($"data: {L["SpotifyToPlex_Exporting"]}: '{playlistName}' ({tracks.Count} {L["SpotifyToPlex_Tracks"]})\n");
            await writer.FlushAsync();

            int added = 0, missing = 0, total = tracks.Count;

            // 🎶 Create new Plex playlist for export
            var playlistTitle = $"Spotify_{playlistName}_{DateTime.Now:yyyy-MM-dd_HH-mm}";
            await writer.WriteLineAsync($"data: 🪄 Creating Plex playlist: {playlistTitle}\n");
            await writer.FlushAsync();

            var plexPlaylistKey = await plex.CreatePlaylistAsync(baseUrl, plexToken, playlistTitle);
            if (string.IsNullOrEmpty(plexPlaylistKey))
            {
                await writer.WriteLineAsync($"data: ERROR Could not create Plex playlist.\n");
                await writer.FlushAsync();
                return;
            }

            // 🧩 Add found tracks one by one
            foreach (var (title, artist) in tracks)
            {
                await writer.WriteLineAsync($"data: 🔍 {L["SpotifyToPlex_Searching"]}: {artist} — {title}\n");
                await writer.FlushAsync();

                var found = await plex.SearchTracksOnPlexAsync(baseUrl, plexToken, new() { (title, artist) });
                var match = found.FirstOrDefault();

                if (!string.IsNullOrEmpty(match.RatingKey))
                {
                    added++;
                    // ✅ add this track to the newly created Plex playlist
                    await plex.AddTracksToPlaylistAsync(baseUrl, plexToken, plexPlaylistKey, new[] { match.RatingKey }, machineId);

                    await writer.WriteLineAsync($"data: ✅ {L["SpotifyToPlex_Found"]}: {artist} — {title}\n");
                }
                else
                {
                    missing++;
                    missingList.Add((artist, title, null)); // store missing for cache
                    await writer.WriteLineAsync($"data: ❌ {L["SpotifyToPlex_MissingTrack"]}: {artist} — {title}\n");
                }

                await writer.WriteLineAsync($"data: progress:{added}:{missing}:{total}\n");
                await writer.FlushAsync();
            }

            // ✅ Save missing tracks via public helper (no reflection)
            PlexService.UpdateMissingCache(playlistTitle, missingList);

            await writer.WriteLineAsync($"data: done:{added}:{missing}:{total}\n");
            await writer.FlushAsync();
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync($"data: ERROR {ex.Message}\n");
            await writer.FlushAsync();
        }
    }


    // ==============================================================
    // 🔸 Download Missing Songs CSV
    // - Uses in-memory cache (_missingCache) from PlexService
    // - Exports the last playlist’s missing tracks as UTF-8 CSV
    // - File name pattern: Missing_<PlaylistName>_<Timestamp>.csv
    // ==============================================================
    [HttpGet("DownloadMissing")]
    public IActionResult DownloadMissing([FromServices] PlexService plex)
    {
        try
        {
            // Retrieve internal missing-track cache (static field)
            var field = typeof(PlexService).GetField("_missingCache",
                BindingFlags.NonPublic | BindingFlags.Static);
            var cache = field?.GetValue(null) as Dictionary<string, List<string>>;

            if (cache == null || cache.Count == 0)
                return Content("No missing track cache available yet.", "text/plain");

            // Use last exported playlist entry
            var last = cache.Last();
            var playlistName = last.Key;
            var missingList = last.Value;

            // Generate CSV using helper function
            var csvBytes = PlexService.GenerateMissingCsv(missingList);
            var fileName = $"Missing_{playlistName}_{DateTime.Now:yyyyMMddHHmmss}.csv";

            // Return as downloadable CSV file
            return File(csvBytes, "text/csv", fileName);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DownloadMissing] " + ex);
            return Content($"Error generating CSV: {ex.Message}", "text/plain");
        }
    }

    // ==============================================================
    // 🔸 AJAX: Get all Plex playlists (Title + RatingKey)
    // ==============================================================
    [HttpGet("GetPlexPlaylists")]
    public async Task<IActionResult> GetPlexPlaylists([FromServices] PlexService plex)
    {
        try
        {
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            if (string.IsNullOrEmpty(plexToken))
                return Json(new { success = false, message = "No Plex token." });

            var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);
            var playlists = await plex.GetPlexPlaylistsAsync(baseUrl, plexToken);

            // Umwandeln in serialisierbare Objekte
            var result = playlists.Select(p => new { title = p.Title, ratingKey = p.RatingKey }).ToList();

            return Json(new { success = true, playlists = result });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GetPlexPlaylists] " + ex);
            return Json(new { success = false, message = ex.Message });
        }
    }


    [HttpGet("GetPlexPlaylistsRaw")]
    public async Task<IActionResult> GetPlexPlaylistsRaw([FromServices] PlexService plex)
    {
        try
        {
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            if (string.IsNullOrEmpty(plexToken))
                return Content("No Plex token available.");

            var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);
            var xml = await plex.GetRawPlaylistsXmlAsync(baseUrl, plexToken);

            //return  Content($"{baseUrl}/playlists/all?X-Plex-Token={plexToken}", "application/xml");

            return Content(xml, "application/xml");
        }
        catch (Exception ex)
        {
            return Content($"Error: {ex.Message}");
        }
    }


    // ==============================================================
    // 🔸 AJAX: Get tracks of one Plex playlist
    // ==============================================================
    [HttpGet("GetPlexPlaylistTracks")]
    public async Task<IActionResult> GetPlexPlaylistTracks([FromServices] PlexService plex, string ratingKey)
    {
        try
        {
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            if (string.IsNullOrEmpty(plexToken))
                return Json(new { success = false, message = "No Plex token." });

            var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);
            var xml = await plex.GetPlaylistTracksXmlAsync(baseUrl, plexToken, ratingKey);

            return Json(new { success = true, tracks = xml });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GetPlexPlaylistTracks] " + ex);
            return Json(new { success = false, message = ex.Message });
        }
    }

    // ==============================================================
    // 🔸 AJAX: Delete Plex playlist
    // ==============================================================
    [HttpPost("DeletePlexPlaylist")]
    public async Task<IActionResult> DeletePlexPlaylist([FromServices] PlexService plex, [FromForm] string ratingKey)
    {
        try
        {
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            if (string.IsNullOrEmpty(plexToken))
                return Json(new { success = false, message = "No Plex token." });

            var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);
            await plex.DeletePlaylistAsync(baseUrl, plexToken, ratingKey);

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DeletePlexPlaylist] " + ex);
            return Json(new { success = false, message = ex.Message });
        }
    }

    // ==============================================================
    // 🔸 Result page after single export
    // ==============================================================
    [HttpGet("ExportResult")]
    public IActionResult ExportResult()
    {
        return View("ExportResult");
    }

    // ==============================================================
    // 🔸 Export all Spotify playlists to Plex
    // ==============================================================
    [HttpPost("ExportAll")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportAll([FromServices] PlexService plex)
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);

        if (string.IsNullOrEmpty(plexToken) || string.IsNullOrEmpty(spotifyToken))
            return RedirectToAction("SpotifyToPlex");

        try
        {
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
            var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);
            var playlists = await plex.GetAllSpotifyPlaylistsAsync(spotify);
            var results = new List<object>();
              
            foreach (var item in playlists)
            {
                var id = item.Value;
                var name = item.Text;

                var (exportedName, addedCount, missing) =
                    await plex.ExportOnePlaylistAsync(spotify, id, name, baseUrl, plexToken);

                results.Add(new
                {
                    exportedName,
                    addedCount,
                    missingCount = missing.Count
                });
            }


            TempData["ExportAllResult"] = JsonSerializer.Serialize(results);
            return RedirectToAction("SpotifyToPlex");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ExportAll] " + ex);
            TempData["Error"] = _localizer["Plex_ExportAllFailed"].Value;
            return RedirectToAction("SpotifyToPlex");
        }
    }


    // ==============================================================
    // 🔸 GetPlexPin (AJAX) – Client triggers Plex login
    // ==============================================================
    [HttpGet("GetPlexPin")]
    public async Task<IActionResult> GetPlexPin([FromServices] PlexService plex)
    {
        var (pinId, code, clientId) = await plex.CreatePinAsync();
        var loginUrl =
            $"https://app.plex.tv/auth#?clientID={clientId}&code={code}&context[device][product]=SpotifyToPlex";

        HttpContext.Session.SetInt32("PlexPinId", pinId);
        HttpContext.Session.SetString("PlexClientId", clientId);

        return Json(new { success = true, pinId, code, clientId, loginUrl });
    }

    // ==============================================================
    // 🔸 Save Plex Token via AJAX
    // ==============================================================
    [HttpPost("SavePlexToken")]
    [IgnoreAntiforgeryToken] // AJAX endpoint → no CSRF needed
    public IActionResult SavePlexToken([FromBody] JsonElement json)
    {
        try
        {
            var token = json.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(_localizer["Plex_NoToken"].Value);

            HttpContext.Session.SetString("PlexAuthToken", token);
            Console.WriteLine($"[Plex] Token saved: {token[..8]}...");
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SavePlexToken] " + ex);
            return BadRequest(new { success = false, message = _localizer["Plex_LoginFailed"].Value });
        }
    }

    // ==============================================================
    // 🔸 Redirect helper (legacy)
    // ==============================================================
    [HttpGet("PlexActions")]
    public IActionResult PlexActions()
    {
        return RedirectToAction("SpotifyToPlex");
    }

    // ==============================================================
    // 🔸 AJAX: show playlist content (title + artist)
    // ==============================================================
    [HttpGet("GetPlaylistTracks")]
    public async Task<IActionResult> GetPlaylistTracks(string playlistId)
    {
        try
        {
            var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);
            if (string.IsNullOrEmpty(spotifyToken))
                return Json(new { success = false, message = "Spotify token missing." });

            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
            var tracks = await spotify.Playlists.GetItems(playlistId);

            var list = tracks.Items
                .OfType<PlaylistTrack<IPlayableItem>>()
                .Select(t => new
                {
                    title = (t.Track as FullTrack)?.Name ?? "",
                    artist = string.Join(", ",
                        (t.Track as FullTrack)?.Artists.Select(a => a.Name) ?? new List<string>())
                })
                .ToList();

            return Json(new { success = true, tracks = list });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GetPlaylistTracks] " + ex);
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetPlexPlaylistTracks(string ratingKey)
    {
        try
        {
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            var plexBaseUrl = HttpContext.Session.GetString("PlexBaseUrl") ?? "http://127.0.0.1:32400";

            if (string.IsNullOrEmpty(plexToken))
                return Json(new { success = false, message = "No Plex token in session." });

            if (string.IsNullOrEmpty(ratingKey))
                return Json(new { success = false, message = "Missing playlist ratingKey." });

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-Plex-Token", plexToken);

            var url = $"{plexBaseUrl}/playlists/{ratingKey}/items";
            var resp = await client.GetAsync(url);

            if (!resp.IsSuccessStatusCode)
            {
                return Json(new
                {
                    success = false,
                    message = $"Plex API error {resp.StatusCode} at {url}"
                });
            }

            var xml = await resp.Content.ReadAsStringAsync();
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);

            var tracks = new List<object>();
            foreach (System.Xml.XmlNode node in doc.SelectNodes("//Track"))
            {
                var title = node.Attributes["title"]?.Value ?? "";
                var artist = node.Attributes["grandparentTitle"]?.Value ?? "";
                var album = node.Attributes["parentTitle"]?.Value ?? "";
                tracks.Add(new { title, artist, album });
            }

            return Json(new { success = true, tracks });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }


    // ============================================================
    // 🔸 Simple text log endpoint for Live Export (last-run info)
    // ============================================================
    [HttpGet("Logs")]
    public IActionResult Logs()
    {
        // For now, return the missing-track cache info if available
        if (TempData.ContainsKey("MissingCsvPath"))
        {
            var path = TempData["MissingCsvPath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            {
                var txt = System.IO.File.ReadAllText(path);
                return Content(txt, "text/plain; charset=utf-8");
            }
        }
        return Content("No log available yet.", "text/plain; charset=utf-8");
    }

}

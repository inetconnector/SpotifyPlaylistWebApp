using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SpotifyAPI.Web;
using SpotifyPlaylistWebApp.Services;

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
            // Plex not authenticated → start Plex login
            return RedirectToAction("PlexActions");

        if (string.IsNullOrEmpty(spotifyToken))
        {
            // Spotify not authenticated → redirect to Spotify login
            HttpContext.Session.SetString("ReturnAfterLogin", "/HomePlex/SpotifyToPlex");
            return RedirectToAction("Login", "Home");
        }

        try
        {
            // Load Spotify playlists for selection
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
            var (baseUrl, _) = await plex.DiscoverServerAsync(plexToken);

            var (exportedName, addedCount, missing) =
                await plex.ExportOnePlaylistAsync(spotify, playlistId, playlistName, baseUrl, plexToken);

            TempData["ExportOneResult"] = JsonSerializer.Serialize(new
            {
                exportedName,
                addedCount,
                missingCount = missing.Count
            });
            TempData["LastMissingTracks"] = JsonSerializer.Serialize(missing);


            return RedirectToAction("ExportResult");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ExportOne] ERROR: " + ex);
            TempData["ErrorMessage"] = $"Export-Error: {ex.Message}";
            return RedirectToAction("SpotifyToPlex");
        }
    }

    // ==============================================================
    // 🔸 AJAX: Get all Plex playlists (Title + RatingKey)
    // ==============================================================

    // ==============================================================
    // 🔸 SSE Live Export – Stream progress to client
    // ==============================================================
    [HttpGet("ExportOneLive")]
    public async Task ExportOneLive([FromServices] PlexService plex, string playlistId, string playlistName)
    {
        Response.Headers.Add("Content-Type", "text/event-stream");
        Response.Headers.Add("Cache-Control", "no-cache");

        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);
        if (string.IsNullOrEmpty(plexToken) || string.IsNullOrEmpty(spotifyToken))
        {
            await Response.WriteAsync("data: ERROR: Tokens missing\n\n");
            await Response.Body.FlushAsync();
            return;
        }

        var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
        var (baseUrl, _) = await plex.DiscoverServerAsync(plexToken);
        var tracks = await plex.GetSpotifyPlaylistTracksAsync(spotify, playlistId);

        async Task Send(string msg)
        {
            await Response.WriteAsync($"data: {msg}\n\n");
            await Response.Body.FlushAsync();
        }

        int added = 0, missing = 0, total = tracks.Count;
        await Send($"Starting export for playlist '{playlistName}' ({total} tracks)");

        foreach (var track in tracks)
        {
            await Send($"Searching: {track.Artist} — {track.Title}");
            var results = await plex.SearchTracksOnPlexAsync(baseUrl, plexToken, new List<(string, string)> { track });
            var match = results.FirstOrDefault();

            if (match.RatingKey != null)
            {
                added++;
                await Send($"✅ Found: {track.Artist} — {track.Title}");
                var existingPlaylists = await plex.GetPlexPlaylistsAsync(baseUrl, plexToken);
                var pl = existingPlaylists.FirstOrDefault(p => p.Title.StartsWith(playlistName));
                var key = pl.RatingKey;
                if (string.IsNullOrEmpty(key))
                    key = await plex.CreatePlaylistAsync(baseUrl, plexToken, playlistName);
                await plex.AddTracksToPlaylistAsync(baseUrl, plexToken, key, new[] { match.RatingKey });
            }
            else
            {
                missing++;
                await Send($"❌ Missing: {track.Artist} — {track.Title}");
            }

            await Send($"progress:{added}:{missing}:{total}");
        }

        await Send($"done:{added}:{missing}:{total}");
    }

    // ==============================================================
    // 🔸 Download missing tracks CSV
    // ==============================================================
    [HttpGet("DownloadMissing")]
    public IActionResult DownloadMissing()
    {
        try
        {
            if (!TempData.ContainsKey("LastMissingTracks"))
                return Content("No missing tracks recorded.");

            var json = TempData["LastMissingTracks"]?.ToString();
            if (string.IsNullOrWhiteSpace(json))
                return Content("No missing tracks data available.");

            var missing = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            var csv = PlexService.GenerateMissingCsv(missing);

            return File(csv, "text/csv", "MissingTracks.csv");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DownloadMissing] " + ex);
            return Content("Error creating CSV: " + ex.Message);
        }
    }


    [HttpGet("GetPlexPlaylists")]
    public async Task<IActionResult> GetPlexPlaylists([FromServices] PlexService plex)
    {
        try
        {
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            if (string.IsNullOrEmpty(plexToken))
                return Json(new { success = false, message = "No Plex token." });

            var (baseUrl, _) = await plex.DiscoverServerAsync(plexToken);
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

            var (baseUrl, _) = await plex.DiscoverServerAsync(plexToken);
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

            var (baseUrl, _) = await plex.DiscoverServerAsync(plexToken);
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

            var (baseUrl, _) = await plex.DiscoverServerAsync(plexToken);
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
            var (baseUrl, _) = await plex.DiscoverServerAsync(plexToken);
            var playlists = await plex.GetAllSpotifyPlaylistsAsync(spotify);
            var results = new List<object>();

            foreach (var (id, name) in playlists)
            {
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
}
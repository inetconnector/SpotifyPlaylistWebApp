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
    // 🔸 Main Plex Entry Page (shown after Spotify login)
    // ==============================================================
    [HttpGet("SpotifyToPlex")]
    public async Task<IActionResult> SpotifyToPlex([FromServices] PlexService plex)
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);

        if (string.IsNullOrEmpty(plexToken))
        {
            // Plex not authenticated → start Plex login
            return RedirectToAction("PlexActions");
        }

        if (string.IsNullOrEmpty(spotifyToken))
        {
            // Spotify not authenticated → go to Spotify login
            HttpContext.Session.SetString("ReturnAfterLogin", "/HomePlex/SpotifyToPlex");
            return RedirectToAction("Login", "Home");
        }

        // Both tokens available → load playlists
        try
        {
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
            var playlists = await plex.GetAllSpotifyPlaylistsAsync(spotify);

            ViewBag.Playlists = playlists;
            return View("PlexActions");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SpotifyToPlex] " + ex.Message);
            TempData["Error"] = _localizer["Plex_StatusLoadError"];
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

        // Save Plex token and trigger Spotify login next
        HttpContext.Session.SetString("PlexAuthToken", token);
        HttpContext.Session.SetString("ReturnAfterLogin", "/HomePlex/SpotifyToPlex");

        return RedirectToAction("Login", "Home");
    }

    // ==============================================================
    // 🔸 Disconnect Plex
    // ==============================================================
    [HttpPost("DisconnectPlex")]
    public async Task<IActionResult> DisconnectPlex([FromServices] IPlexTokenStore tokenStore)
    {
        try
        {
            var userKey = HttpContext.Session.Id;
            await tokenStore.DeleteAsync(userKey);

            HttpContext.Session.Remove("PlexAuthToken");
            HttpContext.Session.Remove("PlexBaseUrl");
            HttpContext.Session.Remove("PlexMachineId");

            TempData["Info"] = _localizer["Plex_Removed"];
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Plex Disconnect] " + ex.Message);
            TempData["Error"] = _localizer["Plex_DisconnectError"];
        }

        return RedirectToAction("Index", "Home");
    }

    // ==============================================================
    // 🔸 Export one selected playlist to Plex
    // ==============================================================
    [HttpPost("ExportOne")]
    public async Task<IActionResult> ExportOne([FromServices] PlexService plex, string playlistId, string playlistName)
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);

        if (string.IsNullOrEmpty(plexToken) || string.IsNullOrEmpty(spotifyToken))
            return RedirectToAction("SpotifyToPlex");

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

        return RedirectToAction("SpotifyToPlex");
    }

    // ==============================================================
    // 🔸 Export all playlists to Plex
    // ==============================================================
    [HttpPost("ExportAll")]
    public async Task<IActionResult> ExportAll([FromServices] PlexService plex)
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);
        if (string.IsNullOrEmpty(plexToken) || string.IsNullOrEmpty(spotifyToken))
            return RedirectToAction("SpotifyToPlex");

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
    public IActionResult SavePlexToken([FromBody] JsonElement json)
    {
        try
        {
            var token = json.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(_localizer["Plex_NoToken"]);

            HttpContext.Session.SetString("PlexAuthToken", token);
            Console.WriteLine($"[Plex] Token saved to session: {token[..8]}...");
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Plex SaveToken] " + ex.Message);
            return BadRequest(new { success = false, message = _localizer["Plex_LoginFailed"] });
        }
    }

    [HttpGet("PlexActions")] 
    public IActionResult PlexActions()
    { 
        return RedirectToAction("SpotifyToPlex");;
    }

    // ==============================================================
    // 🔸 AJAX: Show playlist contents (title, artist)
    // ==============================================================
    [HttpGet("GetPlaylistTracks")]
    public async Task<IActionResult> GetPlaylistTracks([FromServices] PlexService plex, string playlistId)
    {
        try
        {
            var spotifyToken = HttpContext.Session.GetString("SpotifyAccessToken");
            if (string.IsNullOrEmpty(spotifyToken))
                return Json(new { success = false, message = "Spotify token missing." });

            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
            var tracks = await spotify.Playlists.GetItems(playlistId);

            var list = tracks.Items
                .OfType<PlaylistTrack<IPlayableItem>>()
                .Select(t => new
                {
                    title = (t.Track as FullTrack)?.Name ?? "",
                    artist = string.Join(", ", (t.Track as FullTrack)?.Artists.Select(a => a.Name) ?? new List<string>())
                }).ToList();

            return Json(new { success = true, tracks = list });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GetPlaylistTracks] " + ex.Message);
            return Json(new { success = false, message = ex.Message });
        }
    }

}

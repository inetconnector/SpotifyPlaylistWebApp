using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SpotifyAPI.Web;
using SpotifyPlaylistWebApp.Services;

namespace SpotifyPlaylistWebApp.Controllers;

[Route("Home")]
public class HomePlexController : Controller
{
    private const string SessionTokenKey = "SpotifyAccessToken";

    [HttpGet("SpotifyToPlex")]
    public IActionResult SpotifyToPlex()
    {
        // Serve the Plex connection view (shows the orange export UI)
        return View("PlexActions");
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

        HttpContext.Session.SetString("PlexAuthToken", token);
        return RedirectToAction("PlexActions");
    }

    // ==============================================================
    // 🔸 Main PlexActions page (orange UI)
    // Loads Spotify playlists (if token available)
    // ==============================================================
    [HttpGet("PlexActions")]
    public IActionResult PlexActions()
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        if (string.IsNullOrEmpty(plexToken))
            return RedirectToAction("SpotifyToPlex");
        return View("PlexActions");
    }


    // ==============================================================
    // 🔸 Export selected playlist
    // ==============================================================
    [HttpPost("ExportOne")]
    public async Task<IActionResult> ExportOne([FromServices] PlexService plex, string playlistId, string playlistName)
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        var spotifyToken = HttpContext.Session.GetString(SessionTokenKey);

        if (string.IsNullOrEmpty(plexToken) || string.IsNullOrEmpty(spotifyToken))
            return RedirectToAction("Index", "Home");

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

        return RedirectToAction("PlexActions");
    }

    // ==============================================================
    // 🔸 Export all Spotify playlists to Plex
    // ==============================================================
    [HttpPost("ExportAll")]
    public async Task<IActionResult> ExportAll([FromServices] PlexService plex)
    {
        var plexToken = HttpContext.Session.GetString("PlexAuthToken");
        if (string.IsNullOrEmpty(plexToken))
            return RedirectToAction("SpotifyToPlex");

        var spotifyToken = HttpContext.Session.GetString(SessionTokenKey);
        if (string.IsNullOrEmpty(spotifyToken))
        {
            HttpContext.Session.SetString("ReturnAfterLogin",
                Url.Action("ExportAll", "HomePlex") ?? "/HomePlex/ExportAll");
            return RedirectToAction("Login", "Home");
        }

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
        return RedirectToAction("PlexActions");
    }

    // ==============================================================
    // 🔸 Disconnect Plex (manual button)
    // ==============================================================
    [HttpPost("DisconnectPlex")]
    public async Task<IActionResult> DisconnectPlex([FromServices] IPlexTokenStore tokenStore)
    {
        var userKey = HttpContext.Session.Id;
        await tokenStore.DeleteAsync(userKey);

        HttpContext.Session.Remove("PlexAuthToken");
        HttpContext.Session.Remove("PlexBaseUrl");
        HttpContext.Session.Remove("PlexMachineId");

        TempData["Info"] = "Plex-Verknüpfung entfernt.";
        return RedirectToAction("Index", "Home");
    }

    // ==============================================================
    // 🔸 New AJAX endpoints for client-side Plex login
    // ==============================================================
    [HttpGet("GetPlexPin")]
    public async Task<IActionResult> GetPlexPin([FromServices] PlexService plex)
    {
        var (pinId, code, clientId) = await plex.CreatePinAsync();
        var loginUrl = $"https://app.plex.tv/auth#?clientID={clientId}&code={code}&context[device][product]=SpotifyToPlex";

        // Store pin ID and clientId for later verification
        HttpContext.Session.SetInt32("PlexPinId", pinId);
        HttpContext.Session.SetString("PlexClientId", clientId);

        return Json(new { success = true, pinId, code, clientId, loginUrl });
    }

    [HttpPost("SavePlexToken")]
    public async Task<IActionResult> SavePlexToken([FromBody] JsonElement json)
    {
        try
        {
            var token = json.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest("No Plex token received.");

            HttpContext.Session.SetString("PlexAuthToken", token);
            Console.WriteLine($"[Plex] Token saved to session: {token[..8]}...");

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Plex SaveToken] " + ex.Message);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }


}
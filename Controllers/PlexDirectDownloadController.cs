using Microsoft.AspNetCore.Mvc;
using SpotifyPlaylistWebApp.Services;

namespace SpotifyPlaylistWebApp.Controllers
{
    [Route("PlexDirect")]
    public sealed class PlexDirectDownloadController : Controller
    { 
        [HttpGet("Urls")]
        public async Task<IActionResult> Urls(
            [FromServices] PlexDirectDownloadService direct,
            [FromServices] PlexService plex,
            [FromQuery] string playlistKey,
            CancellationToken ct)
        {
            var token = HttpContext.Session.GetString("PlexAuthToken");
            var baseUrl = HttpContext.Session.GetString("PlexBaseUrl");

            if (string.IsNullOrEmpty(token))
                return Unauthorized(new { message = "Not logged in to Plex." });

            if (string.IsNullOrEmpty(baseUrl))
            {
                try
                {
                    var (discoveredBaseUrl, machineId) = await plex.DiscoverServerAsync(token);
                    if (string.IsNullOrWhiteSpace(discoveredBaseUrl))
                        return StatusCode(502, new { message = "Could not determine Plex base URL." });

                    baseUrl = discoveredBaseUrl;
                    HttpContext.Session.SetString("PlexBaseUrl", baseUrl);

                    if (!string.IsNullOrWhiteSpace(machineId))
                        HttpContext.Session.SetString("PlexMachineId", machineId);
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { message = $"Failed to discover Plex server: {ex.Message}" });
                }
            }

            if (string.IsNullOrWhiteSpace(playlistKey))
                return BadRequest(new { message = "playlistKey required" });

            var items = await direct.BuildDirectListAsync(baseUrl!, token!, playlistKey, ct);

            return Ok(new
            {
                count = items.Count,
                items = items.Select(x => new
                {
                    title = x.Title,
                    artist = x.Artist,
                    album = x.Album,
                    filename = x.FileName,
                    url = x.PlexUrl,
                    sizeBytes = x.SizeBytes
                })
            });
        }
         
        [HttpGet("DirectDownload")]
        public IActionResult DirectDownload(
            [FromQuery] string playlistKey = "",
            [FromQuery] string playlistName = "")
        {
            ViewData["Title"] = "Plex Direct Download";
             
            ViewBag.PlaylistKey = playlistKey ?? "";
            ViewBag.PlaylistName = playlistName ?? "";

            return View("~/Views/HomePlex/DirectDownload.cshtml");
        }
    }
}

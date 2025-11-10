using Microsoft.AspNetCore.Mvc;

namespace SpotifyPlaylistWebApp.Controllers;

[Route("Plex")]
public sealed class PlexSessionController : Controller
{
    private static readonly Dictionary<string, string> IntentMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["directDownload"] = "/PlexDirect/DirectDownload",
        ["plexActions"] = "/Home/PlexActions"
    };

    [HttpGet("IsLoggedIn")]
    public IActionResult IsLoggedIn()
    {
        var token = HttpContext.Session.GetString("PlexAuthToken");
        var baseUrl = HttpContext.Session.GetString("PlexBaseUrl");
        var ok = !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(baseUrl);
        return Ok(new { ok });
    }

    [HttpPost("SetIntent")]
    public IActionResult SetIntent([FromBody] IntentDto dto)
    {
        var intent = (dto?.Intent ?? "").Trim();
        if (string.IsNullOrEmpty(intent)) return BadRequest(new { message = "intent required" });
        HttpContext.Session.SetString("PlexIntent", intent);
        return Ok(new { ok = true });
    }

    [HttpGet("NextUrl")]
    public IActionResult NextUrl()
    {
        var intent = HttpContext.Session.GetString("PlexIntent");
        var url = intent != null && IntentMap.TryGetValue(intent, out var u) ? u : IntentMap["plexActions"];
        return Ok(new { url });
    }

    [HttpPost("ClearIntent")]
    public IActionResult ClearIntent()
    {
        HttpContext.Session.Remove("PlexIntent");
        return Ok(new { ok = true });
    }

    public sealed class IntentDto
    {
        public string? Intent { get; set; }
    }
}
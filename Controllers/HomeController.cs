using System.Text;
using Microsoft.AspNetCore.Mvc;
using SpotifyAPI.Web;

namespace SpotifyPlaylistWebApp.Controllers;

public class HomeController : Controller
{
    // === Spotify App-Konfiguration ===
    private const string ClientId = "89d26c134b954a28b78b74cfb893b71b";
    private const string RedirectUri = "https://playlists.inetconnector.com/callback/";

    // === Session Keys ===
    private const string SessionTokenKey = "SpotifyAccessToken";
    private const string SessionVerifierKey = "SpotifyVerifier";

    // === Cooldown (4 Minuten) ===
    private static readonly Dictionary<string, DateTime> _lastActions = new();
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(4);
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    // ============================
    // 🔸 Grundseiten
    // ============================

    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Impressum()
    {
        return View();
    }

    public IActionResult Datenschutz()
    {
        return View();
    }

    // ============================
    // 🔸 Spotify Login Flow
    // ============================

    public IActionResult Login()
    {
        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        HttpContext.Session.SetString(SessionVerifierKey, verifier);

        var loginRequest = new LoginRequest(
            new Uri(RedirectUri),
            ClientId,
            LoginRequest.ResponseType.Code)
        {
            CodeChallenge = challenge,
            CodeChallengeMethod = "S256",
            Scope = new[]
            {
                Scopes.UserReadPrivate,
                Scopes.UserTopRead,
                Scopes.UserLibraryRead,
                Scopes.PlaylistModifyPrivate,
                Scopes.PlaylistModifyPublic
            }
        };

        return Redirect(loginRequest.ToUri().ToString());
    }


    [HttpGet("/callback")]
    public async Task<IActionResult> Callback([FromQuery] string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Content("Fehler: Kein Spotify-Code erhalten.");

        try
        {
            var verifier = HttpContext.Session.GetString(SessionVerifierKey);
            if (string.IsNullOrEmpty(verifier))
                return RedirectToAction("Login");

            var tokenResponse = await new OAuthClient().RequestToken(
                new PKCETokenRequest(ClientId, code, new Uri(RedirectUri), verifier));

            if (string.IsNullOrEmpty(tokenResponse.AccessToken))
                return Content("Fehler: Kein Token erhalten.");

            HttpContext.Session.SetString(SessionTokenKey, tokenResponse.AccessToken);
            return RedirectToAction("Dashboard");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify Callback fehlgeschlagen");
            throw;
        }
    } 

    // ============================
    // 🔸 Dashboard
    // ============================

    public async Task<IActionResult> Dashboard()
    {
        var token = HttpContext.Session.GetString(SessionTokenKey);
        if (string.IsNullOrEmpty(token))
            return RedirectToAction("Login");

        try
        {
            // Verwende HttpClient mit Timeout
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var config = SpotifyClientConfig.CreateDefault().WithToken(token);

            var spotify = new SpotifyClient(config);

            try
            {
                var me = await spotify.UserProfile.Current();
                ViewBag.User = me?.DisplayName ?? "Spotify User";
            }
            catch (APIException apiEx)
            {
                _logger.LogError(apiEx, "Spotify API-Fehler beim Laden des Benutzerprofils.");
                throw new Exception($"Spotify API-Fehler: {apiEx.Message}", apiEx);
            }
             
            return View();
        }
        catch (APIUnauthorizedException)
        {
            _logger.LogWarning("Spotify Token ungültig oder abgelaufen.");
            HttpContext.Session.Remove(SessionTokenKey);
            return RedirectToAction("Login");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard-Fehler");
            throw;
        }
    }

    // ============================
    // 🔸 Playlist-Aktionen
    // ============================

    [HttpGet]
    public IActionResult CreateTopMix()
    {
        return StartPlaylistJob(GetToken(), true, false, "Top-Mix", "[TopMix]", false);
    }

    [HttpGet]
    public IActionResult CreateTopList()
    {
        return StartPlaylistJob(GetToken(), false, false, "Top-Liste", "[TopList]", false);
    }

    [HttpGet]
    public IActionResult CreateFavMix()
    {
        return StartPlaylistJob(GetToken(), true, false, "Favoriten-Mix", "[FavMix]", true);
    }

    [HttpGet]
    public IActionResult CreateFavList()
    {
        return StartPlaylistJob(GetToken(), false, false, "Favoriten-Liste", "[FavList]", true);
    }

    private string GetToken()
    {
        return HttpContext.Session.GetString(SessionTokenKey) ?? string.Empty;
    }

    // ============================
    // 🔸 Cooldown & Startlogik
    // ============================

    private IActionResult StartPlaylistJob(
        string token, bool shuffled, bool similar, string namePrefix, string logTag, bool useLikedSongs)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Content(RenderMessage("⚠️ Bitte zuerst einloggen.", "warning"),
                "text/html; charset=utf-8", Encoding.UTF8);

        var userKey = $"{token}_{logTag}";
        lock (_lastActions)
        {
            if (_lastActions.TryGetValue(userKey, out var last)
                && DateTime.UtcNow - last < CooldownDuration)
            {
                var remaining = CooldownDuration - (DateTime.UtcNow - last);
                var minutes = (int)remaining.TotalMinutes;
                var seconds = remaining.Seconds;
                return Content(RenderMessage(
                    $"⏳ Bitte warte {minutes}:{seconds:D2} Minuten, bevor du diese Aktion erneut startest.",
                    "warning"), "text/html; charset=utf-8", Encoding.UTF8);
            }

            _lastActions[userKey] = DateTime.UtcNow;
        }

        Task.Run(async () =>
        {
            try
            {
                await GeneratePlaylistAsync(token, shuffled, similar, namePrefix, useLikedSongs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logTag} Fehler: {ex}");
            }
        });

        return Content(RenderMessage("✅ Playlist-Erstellung gestartet.<br>Bitte in 1–2 Minuten in Spotify® prüfen."),
            "text/html; charset=utf-8", Encoding.UTF8);
    }

    // ============================
    // 🔸 Playlist-Logik
    // ============================

    private static async Task GeneratePlaylistAsync(
        string token, bool shuffled, bool similar, string namePrefix, bool useLikedSongs)
    {
        try
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var config = SpotifyClientConfig.CreateDefault().WithToken(token);

            var spotify = new SpotifyClient(config);
            var me = await spotify.UserProfile.Current();

            var allTracks = new List<FullTrack>();
            const int pageSize = 50;
            var offset = 0;

            if (useLikedSongs)
                while (true)
                {
                    var saved = await spotify.Library.GetTracks(
                        new LibraryTracksRequest { Limit = pageSize, Offset = offset });
                    if (saved.Items == null || saved.Items.Count == 0) break;

                    allTracks.AddRange(saved.Items.Select(t => t.Track).Where(t => t != null)!);
                    offset += pageSize;
                    if (saved.Items.Count < pageSize) break;
                }
            else
                while (true)
                {
                    var req = new PersonalizationTopRequest
                    {
                        TimeRangeParam = PersonalizationTopRequest.TimeRange.LongTerm,
                        Limit = pageSize,
                        Offset = offset
                    };
                    var batch = await spotify.Personalization.GetTopTracks(req);
                    if (batch.Items.Count == 0) break;

                    allTracks.AddRange(batch.Items);
                    offset += pageSize;
                    if (batch.Items.Count < pageSize) break;
                }

            if (allTracks.Count == 0)
            {
                Console.WriteLine($"{namePrefix}: Keine Songs gefunden.");
                return;
            }

            var finalList = shuffled
                ? allTracks.OrderBy(_ => Guid.NewGuid()).ToList()
                : allTracks.ToList();

            var uris = finalList.Select(t => t.Uri).ToList();
            var playlistName = $"{namePrefix} {DateTime.Now:yyyy-MM-dd}";

            var playlist = await spotify.Playlists.Create(me.Id,
                new PlaylistCreateRequest(playlistName) { Public = false });

            for (var i = 0; i < uris.Count; i += 100)
            {
                var chunk = uris.Skip(i).Take(100).ToList();
                if (chunk.Count > 0)
                    await spotify.Playlists.AddItems(playlist.Id!, new PlaylistAddItemsRequest(chunk));
                await Task.Delay(1000);
            }

            var link = playlist.ExternalUrls?.GetValueOrDefault("spotify") ?? "(kein Link verfügbar)";
            Console.WriteLine($"✅ {playlistName} erstellt mit {uris.Count} Songs: {link}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Playlist-Erstellung fehlgeschlagen: {ex}");
        }
    }

    // ============================
    // 🔸 HTML-Rückmeldungen
    // ============================

    private static string RenderMessage(string message, string type = "success")
    {
        var color = type switch
        {
            "warning" => "#facc15",
            "error" => "#ef4444",
            _ => "#10b981"
        };

        return $@"
<!doctype html>
<html lang='de'>
<head>
<meta charset='utf-8'/>
<meta name='viewport' content='width=device-width, initial-scale=1'/>
<title>InetConnector Playlist Generator™</title>
<script src='https://cdn.tailwindcss.com'></script>
<style>
body {{
 background:#0b0f17;color:white;font-family:system-ui,sans-serif;
}}
.glass {{
 background:rgba(255,255,255,0.08);
 backdrop-filter:blur(12px);
 border:1px solid rgba(255,255,255,0.12);
}}
</style>
</head>
<body class='min-h-screen flex items-center justify-center'>
<div class='glass p-8 rounded-3xl shadow-xl text-center max-w-lg'>
<h1 class='text-2xl font-bold mb-4' style='color:{color};'>{message}</h1>
<a href='/Home/Dashboard' class='px-5 py-3 rounded-xl bg-emerald-500 hover:bg-emerald-600 text-white no-underline'>Zurück zum Dashboard</a>
</div>
</body>
</html>";
    }
}
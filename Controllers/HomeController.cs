using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
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

    // === Cooldown-System ===
    private static readonly Dictionary<string, DateTime> _lastActions = new();
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(4);

    // === API-Rate-Limit / Delay ===
    private static readonly TimeSpan ApiDelay = TimeSpan.FromMilliseconds(500);
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<HomeController> _logger;
    private readonly IServiceProvider _services;

    public HomeController(
        ILogger<HomeController> logger,
        IServiceProvider services,
        IStringLocalizer<SharedResource> localizer)
    {
        _logger = logger;
        _services = services;
        _localizer = localizer;
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

        _logger.LogInformation("Spotify Login gestartet, PKCE-Challenge erstellt.");
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
            _logger.LogInformation("Spotify Token erfolgreich empfangen.");
            return RedirectToAction("Dashboard");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify Callback fehlgeschlagen");
            return Content("Ein Fehler ist beim Login aufgetreten. Bitte erneut versuchen.");
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
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token));
            var me = await spotify.UserProfile.Current();
            ViewBag.User = me?.DisplayName ?? "Spotify User";
            ViewBag.UserId = me?.Id;
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
            _logger.LogError(ex, "Fehler beim Laden des Dashboards.");
            throw;
        }
    }

    // ============================
    // 🔸 Playlist-Aktionen
    // ============================

    [HttpGet]
    public IActionResult CreateTopMix()
    {
        return StartPlaylistJob(GetToken(), "Top-Mix", "[TopMix]", false, true);
    }

    [HttpGet]
    public IActionResult CreateTopList()
    {
        return StartPlaylistJob(GetToken(), "Top-Liste", "[TopList]", false, false);
    }

    [HttpGet]
    public IActionResult CreateFavMix()
    {
        return StartPlaylistJob(GetToken(), "Favoriten-Mix", "[FavMix]", true, true);
    }

    [HttpGet]
    public IActionResult CreateFavList()
    {
        return StartPlaylistJob(GetToken(), "Favoriten-Liste", "[FavList]", true, false);
    }

    [HttpGet]
    public IActionResult CreateAlternativeFavorites()
    {
        return StartAlternativeFavoritesJob(GetToken());
    }

    private string GetToken()
    {
        return HttpContext.Session.GetString(SessionTokenKey) ?? string.Empty;
    }

    // ============================
    // 🔸 Cooldown & Startlogik
    // ============================

    private IActionResult StartPlaylistJob(string token, string namePrefix, string logTag, bool useLikedSongs,
        bool shuffled)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Warn("⚠️ Bitte zuerst einloggen.");

        try
        {
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token));

            return RunJob(async () =>
                {
                    var me = await spotify.UserProfile.Current();
                    using var scope = _logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["UserId"] = me.Id,
                        ["Action"] = logTag
                    });

                    await GeneratePlaylistAsync(spotify, me.Id, namePrefix, shuffled, useLikedSongs, _logger);
                },
                async () =>
                {
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token)).UserProfile
                        .Current();
                    return $"{me.Id}_{logTag}";
                },
                namePrefix,
                logTag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Action} konnte nicht gestartet werden.", namePrefix);
            return Error($"Fehler beim Starten von {namePrefix}: {ex.Message}");
        }
    }

    private IActionResult StartAlternativeFavoritesJob(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Warn("⚠️ Bitte zuerst einloggen.");

        try
        {
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token));

            return RunJob(async () =>
                {
                    var me = await spotify.UserProfile.Current();
                    using var scope = _logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["UserId"] = me.Id,
                        ["Action"] = "[AltFavorites]"
                    });

                    await GenerateAlternativeFavoritesAsync(spotify, me.Id); // nutzt _localizer intern
                },
                async () =>
                {
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token)).UserProfile
                        .Current();
                    return $"{me.Id}_[AltFavorites]";
                },
                _localizer["Playlist_AlternativeFavorites"], // Anzeigename in Message
                "[AltFavorites]");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alternative Favorites konnten nicht gestartet werden.");
            return Error($"Fehler beim Starten von '{_localizer["Playlist_AlternativeFavorites"]}': {ex.Message}");
        }
    }

    private IActionResult RunJob(Func<Task> job, Func<Task<string>> computeCooldownKeyAsync, string actionName,
        string logTag)
    {
        // Cooldown prüfen (userbasiert)
        string key;
        try
        {
            key = computeCooldownKeyAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cooldown-Key konnte nicht ermittelt werden.");
            return Error("Interner Fehler bei der Vorbereitung. Bitte erneut versuchen.");
        }

        lock (_lastActions)
        {
            if (_lastActions.TryGetValue(key, out var last) && DateTime.UtcNow - last < CooldownDuration)
            {
                var remaining = CooldownDuration - (DateTime.UtcNow - last);
                return Warn(
                    $"⏳ Bitte warte {remaining.Minutes}:{remaining.Seconds:D2} Minuten, bevor du diese Aktion erneut startest.");
            }

            _lastActions[key] = DateTime.UtcNow;
        }

        // Fire-and-forget Background-Task
        _ = Task.Run(async () =>
        {
            using var scope = _logger.BeginScope($"PlaylistJob:{key}");
            try
            {
                _logger.LogInformation("{LogTag} Job gestartet.", logTag);
                await job();
                _logger.LogInformation("{LogTag} Job erfolgreich abgeschlossen.", logTag);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{LogTag} Fehler im Hintergrundjob.", logTag);
            }
        });

        return OkMessage($"✅ {actionName} gestartet.<br>Bitte in 1–2 Minuten in Spotify® prüfen.");
    }

    // ============================
    // 🔸 Playlist-Logik
    // ============================

    private static async Task GeneratePlaylistAsync(
        SpotifyClient spotify,
        string userId,
        string namePrefix,
        bool shuffled,
        bool useLikedSongs,
        ILogger logger)
    {
        var allTracks = new List<FullTrack>();
        const int pageSize = 50;

        if (useLikedSongs)
            for (var offset = 0;; offset += pageSize)
            {
                var saved = await spotify.Library.GetTracks(new LibraryTracksRequest
                    { Limit = pageSize, Offset = offset });
                if (saved.Items == null || saved.Items.Count == 0) break;

                allTracks.AddRange(saved.Items.Select(i => i.Track).Where(t => t != null)!);
                if (saved.Items.Count < pageSize) break;
                await Task.Delay(ApiDelay);
            }
        else
            for (var offset = 0;; offset += pageSize)
            {
                var top = await spotify.Personalization.GetTopTracks(new PersonalizationTopRequest
                {
                    Limit = pageSize,
                    Offset = offset,
                    TimeRangeParam = PersonalizationTopRequest.TimeRange.LongTerm
                });
                if (top.Items == null || top.Items.Count == 0) break;

                allTracks.AddRange(top.Items);
                if (top.Items.Count < pageSize) break;
                await Task.Delay(ApiDelay);
            }

        if (allTracks.Count == 0)
        {
            logger.LogWarning("{Prefix}: Keine Songs gefunden.", namePrefix);
            return;
        }

        var finalList = shuffled
            ? allTracks.OrderBy(_ => Guid.NewGuid()).ToList()
            : allTracks.ToList();

        var uris = finalList
            .Select(t => t.Uri)
            .ToList();

        var playlistName = $"{namePrefix} {DateTime.Now:yyyy-MM-dd}";
        var playlist = await spotify.Playlists.Create(userId,
            new PlaylistCreateRequest(playlistName) { Public = false });

        for (var i = 0; i < uris.Count; i += 100)
        {
            var chunk = uris.Skip(i).Take(100).ToList();
            if (chunk.Count > 0) await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(chunk));
            await Task.Delay(ApiDelay);
        }

        var link = playlist.ExternalUrls?.GetValueOrDefault("spotify") ?? "(kein Link verfügbar)";
        logger.LogInformation("✅ {Playlist} erstellt mit {Count} Songs: {Link}", playlistName, uris.Count, link);
    }

    // === Alternative Lieblingssongs (mehrsprachiger Titel aus Ressourcen) ===
    private async Task GenerateAlternativeFavoritesAsync(SpotifyClient spotify, string userId)
    {
        try
        {
            var likedTracks = new List<FullTrack>();
            const int pageSize = 50;

            for (var offset = 0; offset < 200; offset += pageSize)
            {
                var liked = await spotify.Library.GetTracks(new LibraryTracksRequest
                    { Limit = pageSize, Offset = offset });
                if (liked.Items == null || liked.Items.Count == 0) break;

                likedTracks.AddRange(liked.Items.Select(i => i.Track).Where(t => t != null)!);
                await Task.Delay(ApiDelay);
            }

            if (likedTracks.Count == 0)
            {
                _logger.LogWarning("AlternativeFavorites: Keine Lieblingssongs gefunden.");
                return;
            }

            var likedUriSet = new HashSet<string>(likedTracks.Select(t => t.Uri), StringComparer.OrdinalIgnoreCase);
            var altUris = new List<string>();

            foreach (var track in likedTracks)
            {
                string? replacementUri = null;

                // 1) Versuch: anderer Track vom selben Album
                try
                {
                    var albumTracks = await spotify.Albums.GetTracks(track.Album.Id);
                    var fromAlbum = albumTracks.Items
                        .Where(t => t?.Uri != null)
                        .FirstOrDefault(t =>
                            !likedUriSet.Contains(t.Uri) &&
                            !string.Equals(t.Id, track.Id, StringComparison.OrdinalIgnoreCase));

                    replacementUri = fromAlbum?.Uri;
                }
                catch
                {
                    // Albumnicht erreichbar -> ignorieren
                }

                // 2) Fallback: Top-Track des Künstlers
                if (replacementUri == null)
                    try
                    {
                        var artistId = track.Artists?.FirstOrDefault()?.Id;
                        if (!string.IsNullOrEmpty(artistId))
                        {
                            // Land: DE — alternativ könnte man Culture ableiten
                            var top = await spotify.Artists.GetTopTracks(artistId, new ArtistsTopTracksRequest("DE"));
                            var fromArtist = top.Tracks
                                .Where(t => t?.Uri != null)
                                .FirstOrDefault(t =>
                                    !likedUriSet.Contains(t.Uri) &&
                                    !string.Equals(t.Id, track.Id, StringComparison.OrdinalIgnoreCase));

                            replacementUri = fromArtist?.Uri;
                        }
                    }
                    catch
                    {
                        // ignorieren
                    }

                altUris.Add(replacementUri ?? track.Uri);
                await Task.Delay(ApiDelay);
            }

            // 🔹 Lokalisierter Playlist-Name aus Ressourcen + Datum
            var baseTitle = _localizer["Playlist_AlternativeFavorites"]; // z. B. "Alternative Lieblingssongs"
            var playlistTitle = $"{baseTitle} {DateTime.Now:yyyy-MM-dd}";

            var playlist = await spotify.Playlists.Create(userId,
                new PlaylistCreateRequest(playlistTitle) { Public = false });

            for (var i = 0; i < altUris.Count; i += 100)
            {
                var chunk = altUris.Skip(i).Take(100).ToList();
                if (chunk.Count > 0) await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(chunk));
                await Task.Delay(ApiDelay);
            }

            _logger.LogInformation("✅ Playlist '{Title}' erstellt mit {Count} Songs.", playlistTitle, altUris.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler in GenerateAlternativeFavoritesAsync");
        }
    }

    // ============================
    // 🔸 HTML Helper
    // ============================

    private ContentResult OkMessage(string msg)
    {
        return HtmlResult(msg, "#10b981");
    }

    private ContentResult Warn(string msg)
    {
        return HtmlResult(msg, "#facc15");
    }

    private ContentResult Error(string msg)
    {
        return HtmlResult(msg, "#ef4444");
    }

    private ContentResult HtmlResult(string message, string color)
    {
        var html = $@"
<!doctype html>
<html lang='de'>
<head>
<meta charset='utf-8'/>
<meta name='viewport' content='width=device-width, initial-scale=1'/>
<title>InetConnector Playlist Generator™</title>
<script src='https://cdn.tailwindcss.com'></script>
<style>
body {{background:#0b0f17;color:white;font-family:system-ui,sans-serif;}}
.glass {{background:rgba(255,255,255,0.08);backdrop-filter:blur(12px);border:1px solid rgba(255,255,255,0.12);}}
</style>
</head>
<body class='min-h-screen flex items-center justify-center'>
<div class='glass p-8 rounded-3xl shadow-xl text-center max-w-lg'>
<h1 class='text-2xl font-bold mb-4' style='color:{color};'>{message}</h1>
<a href='/Home/Dashboard' class='px-5 py-3 rounded-xl bg-emerald-500 hover:bg-emerald-600 text-white no-underline'>Zurück zum Dashboard</a>
</div>
</body>
</html>";
        return Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }
}
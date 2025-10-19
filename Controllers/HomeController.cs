using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using SpotifyAPI.Web;
using System.Globalization;
using System.Text;

namespace SpotifyPlaylistWebApp.Controllers;

public class HomeController : Controller
{
    // === Session Keys ===
    private const string SessionTokenKey = "SpotifyAccessToken";
    private const string SessionVerifierKey = "SpotifyVerifier";

    // === Cooldown-System ===
    private static readonly Dictionary<string, DateTime> _lastActions = new();
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(4);

    // === API-Rate-Limit / Delay ===
    private static readonly TimeSpan ApiDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<HomeController> _logger;
    private readonly IServiceProvider _services;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IConfiguration _config;

    private readonly string _clientId;
    private readonly string _redirectUri;

    public HomeController(
        ILogger<HomeController> logger,
        IServiceProvider services,
        IStringLocalizer<SharedResource> localizer,
        IConfiguration config)
    {
        _logger = logger;
        _services = services;
        _localizer = localizer;
        _config = config;

        _clientId = _config["Spotify:ClientId"]
            ?? Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID")
            ?? throw new InvalidOperationException("Spotify ClientId fehlt.");

        _redirectUri = _config["Spotify:RedirectUri"]
            ?? Environment.GetEnvironmentVariable("SPOTIFY_REDIRECT_URI")
            ?? throw new InvalidOperationException("Spotify RedirectUri fehlt.");
    }

    // ============================
    // 🔸 Grundseiten
    // ============================

    public IActionResult Index() => View();
    public IActionResult Impressum() => View();
    public IActionResult Datenschutz() => View();

    // ============================
    // 🔸 Spotify Login Flow
    // ============================

    public IActionResult Login()
    {
        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        HttpContext.Session.SetString(SessionVerifierKey, verifier);

        var loginRequest = new LoginRequest(
            new Uri(_redirectUri),
            _clientId,
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
                new PKCETokenRequest(_clientId, code, new Uri(_redirectUri), verifier));

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
        catch (APIException apiEx)
        {
            _logger.LogError("Spotify API-Fehler: {StatusCode} - {Message}\n{Body}",
                apiEx.Response.StatusCode, apiEx.Message, apiEx.Response.Body);

            return Error($"""
                          🚨 Spotify API-Fehler ({(int)apiEx.Response.StatusCode}): {apiEx.Message}<br>
                          <small>{apiEx.Response.Body}</small>
                          """);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden des Dashboards.");
            return Error($"Unerwarteter Fehler: {ex.Message}");
        }
    }

    // ============================
    // 🔸 Playlist-Aktionen
    // ============================

    [HttpGet]
    public IActionResult CreateTopMix() =>
        StartPlaylistJob(GetToken(), "Top-Mix", "[TopMix]", false, true);

    [HttpGet]
    public IActionResult CreateTopList() =>
        StartPlaylistJob(GetToken(), "Top-Liste", "[TopList]", false, false);

    [HttpGet]
    public IActionResult CreateFavMix() =>
        StartPlaylistJob(GetToken(), "Favoriten-Mix", "[FavMix]", true, true);

    [HttpGet]
    public IActionResult CreateFavList() =>
        StartPlaylistJob(GetToken(), "Favoriten-Liste", "[FavList]", true, false);

    [HttpGet]
    public IActionResult CreateAlternativeFavorites() =>
        StartAlternativeFavoritesJob(GetToken());

    // === Alias für CloneLikedSongs (gleiche Funktion wie CreateFavList)
    [HttpGet]
    public IActionResult CloneLikedSongs() =>
        StartLikedCloneJob(GetToken());

    // === Empfehlungen (neue Logik mit Spotify-Empfehlungen)
    [HttpGet]
    public IActionResult CreateRecommendations()
    {
        var token = GetToken();
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
                    ["Action"] = "[Recommendations]"
                });

                await GenerateRecommendationsAsync(spotify, me.Id);
            },
                async () =>
                {
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token)).UserProfile.Current();
                    return $"{me.Id}_[Recommendations]";
                },
                _localizer["Playlist_Recommendations"],
                "[Recommendations]");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Empfehlungen konnten nicht gestartet werden.");
            return Error($"Fehler beim Starten von '{_localizer["Playlist_Recommendations"]}': {ex.Message}");
        }
    }

    private string GetToken() =>
        HttpContext.Session.GetString(SessionTokenKey) ?? string.Empty;

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
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token)).UserProfile.Current();
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
    private IActionResult StartLikedCloneJob(string token)
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
                    ["Action"] = "[LikedClone]"
                });

                // 1) Hole alle geliketen Tracks
                var liked = await spotify.Library.GetTracks(new LibraryTracksRequest { Limit = 50 });
                var tracks = new List<string>();
                while (true)
                {
                    tracks.AddRange(liked.Items.Where(i => i.Track != null).Select(i => i.Track.Uri));
                    if (string.IsNullOrEmpty(liked.Next)) break;
                    liked = await spotify.NextPage(liked);
                    await Task.Delay(ApiDelay);
                }

                // 2) Neue Playlist anlegen "Lieblingssongs YYYY-MM-DD"
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var playlistName = $"Lieblingssongs {today}";
                var newPl = await spotify.Playlists.Create(me.Id, new PlaylistCreateRequest(playlistName)
                {
                    Public = false,
                    Description = "Kopie der 'Liked Songs'"
                });

                // 3) In Blöcken hinzufügen
                for (int i = 0; i < tracks.Count; i += 100)
                {
                    var chunk = tracks.Skip(i).Take(100).ToList();
                    if (chunk.Count == 0) break;
                    await spotify.Playlists.AddItems(newPl.Id, new PlaylistAddItemsRequest(chunk));
                    await Task.Delay(ApiDelay);
                }
            },
            async () =>
            {
                var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token)).UserProfile.Current();
                return $"{me.Id}_[LikedClone]";
            },
            "Lieblingssongs",
            "[LikedClone]");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Action} konnte nicht gestartet werden.", "Lieblingssongs");
            return Error($"Fehler beim Starten von Lieblingssongs: {ex.Message}");
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

                await GenerateAlternativeFavoritesAsync(spotify, me.Id);
            },
                async () =>
                {
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token)).UserProfile.Current();
                    return $"{me.Id}_[AltFavorites]";
                },
                _localizer["Playlist_AlternativeFavorites"],
                "[AltFavorites]");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alternative Favorites konnten nicht gestartet werden.");
            return Error($"Fehler beim Starten von '{_localizer["Playlist_AlternativeFavorites"]}': {ex.Message}");
        }
    }

    private IActionResult RunJob(Func<Task> job, Func<Task<string>> computeCooldownKeyAsync, string actionName, string logTag)
    {
        string key;
        try { key = computeCooldownKeyAsync().GetAwaiter().GetResult(); }
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
                return Warn($"⏳ Bitte warte {remaining.Minutes}:{remaining.Seconds:D2} Minuten, bevor du diese Aktion erneut startest.");
            }
            _lastActions[key] = DateTime.UtcNow;
        }

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

    private static async Task GeneratePlaylistAsync(SpotifyClient spotify, string userId, string namePrefix, bool shuffled, bool useLikedSongs, ILogger logger)
    {
        var allTracks = new List<FullTrack>();
        const int pageSize = 50;

        if (useLikedSongs)
        {
            for (var offset = 0; ; offset += pageSize)
            {
                var saved = await spotify.Library.GetTracks(new LibraryTracksRequest { Limit = pageSize, Offset = offset });
                if (saved.Items == null || saved.Items.Count == 0) break;

                allTracks.AddRange(saved.Items.Select(i => i.Track).Where(t => t != null)!);
                if (saved.Items.Count < pageSize) break;
                await Task.Delay(ApiDelay);
            }
        }
        else
        {
            for (var offset = 0; ; offset += pageSize)
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
        }

        if (allTracks.Count == 0)
        {
            logger.LogWarning("{Prefix}: Keine Songs gefunden.", namePrefix);
            return;
        }

        var finalList = shuffled ? allTracks.OrderBy(_ => Guid.NewGuid()).ToList() : allTracks;
        var uris = finalList.Select(t => t.Uri).ToList();

        var playlistName = $"{namePrefix} {DateTime.Now:yyyy-MM-dd}";
        var playlist = await spotify.Playlists.Create(userId, new PlaylistCreateRequest(playlistName) { Public = false });

        for (var i = 0; i < uris.Count; i += 100)
        {
            var chunk = uris.Skip(i).Take(100).ToList();
            if (chunk.Count > 0)
                await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(chunk));
            await Task.Delay(ApiDelay);
        }

        var link = playlist.ExternalUrls?.GetValueOrDefault("spotify") ?? "(kein Link verfügbar)";
        logger.LogInformation("✅ {Playlist} erstellt mit {Count} Songs: {Link}", playlistName, uris.Count, link);
    }

    private async Task GenerateAlternativeFavoritesAsync(SpotifyClient spotify, string userId)
    {
        try
        {
            var likedTracks = new List<FullTrack>();
            const int pageSize = 50;

            for (var offset = 0; offset < 200; offset += pageSize)
            {
                var liked = await spotify.Library.GetTracks(new LibraryTracksRequest { Limit = pageSize, Offset = offset });
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
            var usedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var altUris = new List<string>();

            foreach (var track in likedTracks)
            {
                string? replacementUri = null;
                try
                {
                    var albumTracks = await spotify.Albums.GetTracks(track.Album.Id);
                    var fromAlbum = albumTracks.Items
                        .Where(t => t?.Uri != null)
                        .FirstOrDefault(t => !likedUriSet.Contains(t.Uri) && !usedUris.Contains(t.Uri) && t.Id != track.Id);
                    replacementUri = fromAlbum?.Uri;
                }
                catch { }

                if (replacementUri == null)
                {
                    try
                    {
                        var artistId = track.Artists?.FirstOrDefault()?.Id;
                        if (!string.IsNullOrEmpty(artistId))
                        {
                            var top = await spotify.Artists.GetTopTracks(artistId, new ArtistsTopTracksRequest("DE"));
                            var fromArtist = top.Tracks
                                .FirstOrDefault(t => !likedUriSet.Contains(t.Uri) && !usedUris.Contains(t.Uri) && t.Id != track.Id);
                            replacementUri = fromArtist?.Uri;
                        }
                    }
                    catch { }
                }

                var finalUri = replacementUri ?? track.Uri;
                if (usedUris.Add(finalUri))
                    altUris.Add(finalUri);

                await Task.Delay(ApiDelay);
            }

            var title = $"{_localizer["Playlist_AlternativeFavorites"]} {DateTime.Now:yyyy-MM-dd}";
            var playlist = await spotify.Playlists.Create(userId, new PlaylistCreateRequest(title) { Public = false });

            for (var i = 0; i < altUris.Count; i += 100)
            {
                var chunk = altUris.Skip(i).Take(100).ToList();
                if (chunk.Count > 0)
                    await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(chunk));
                await Task.Delay(ApiDelay);
            }

            _logger.LogInformation("✅ Playlist '{Title}' erstellt mit {Count} Songs.", title, altUris.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler in GenerateAlternativeFavoritesAsync");
        }
    }

    private async Task GenerateRecommendationsAsync(SpotifyClient spotify, string userId)
    {
        try
        {
            // 1️⃣ Fetch top tracks (medium-term = last ~6 months)
            var topTracks = await spotify.Personalization.GetTopTracks(new PersonalizationTopRequest
            {
                Limit = 50,
                TimeRangeParam = PersonalizationTopRequest.TimeRange.MediumTerm
            });

            if (topTracks.Items == null || topTracks.Items.Count == 0)
            {
                _logger.LogWarning("Recommendations: No top tracks found for this user.");
                return;
            }

            // 2️⃣ Pick up to 15 random seed tracks (Spotify allows max 5 seeds, we’ll randomize later)
            var randomSeeds = topTracks.Items
                .OrderBy(_ => Guid.NewGuid())
                .Take(15)
                .Select(t => t.Id)
                .ToList();

            // Randomly pick 5 of them for the request
            var seedSubset = randomSeeds
                .OrderBy(_ => Guid.NewGuid())
                .Take(5)
                .ToList();

            // 3️⃣ Determine Spotify market dynamically based on UI culture
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            string market = "US";

            if (!string.IsNullOrEmpty(culture.Name) && culture.Name.Contains('-'))
                market = culture.Name.Split('-').Last().ToUpperInvariant();
            else if (culture.TwoLetterISOLanguageName.Length == 2)
                market = culture.TwoLetterISOLanguageName.ToUpperInvariant();

            _logger.LogInformation("Generating recommendations using market '{Market}' with {Count} seed tracks.", market, seedSubset.Count);

            // 4️⃣ Request recommendations from Spotify
            var recRequest = new RecommendationsRequest
            {
                Limit = 50,
                Market = market
            }; 
            
            if (seedSubset.Count == 0)
            {
                _logger.LogWarning("No top tracks found to use as seeds. Falling back to liked songs.");
                // optional: fallback to liked songs here
                return;
            }

            // add up to 15 seeds directly to the internal list
            foreach (var id in seedSubset.Take(15))
                recRequest.SeedTracks.Add(id);


            var recs = await spotify.Browse.GetRecommendations(recRequest);

            if (recs.Tracks == null || recs.Tracks.Count == 0)
            {
                _logger.LogWarning("Recommendations: Spotify returned no tracks.");
                return;
            }

            // 5️⃣ Create new playlist with recommendations
            var title = $"Recommended Songs {DateTime.Now:yyyy-MM-dd}";
            var playlist = await spotify.Playlists.Create(userId, new PlaylistCreateRequest(title) { Public = false });

            var uris = recs.Tracks
                .Where(t => !string.IsNullOrEmpty(t.Uri))
                .Select(t => t.Uri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < uris.Count; i += 100)
            {
                var chunk = uris.Skip(i).Take(100).ToList();
                await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(chunk));
                await Task.Delay(ApiDelay);
            }

            _logger.LogInformation("✅ Playlist '{Title}' created with {Count} recommended songs (market {Market}).", title, uris.Count, market);
        }
        catch (APIException apiEx)
        {
            _logger.LogError(apiEx, "Spotify API error while generating recommendations: {Status} - {Message}", apiEx.Response.StatusCode, apiEx.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GenerateRecommendationsAsync.");
        }
    }


    // ============================
    // 🔸 HTML Helper
    // ============================

    private ContentResult OkMessage(string msg) => HtmlResult(msg, "#10b981");
    private ContentResult Warn(string msg) => HtmlResult(msg, "#facc15");
    private ContentResult Error(string msg) => HtmlResult(msg, "#ef4444");

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

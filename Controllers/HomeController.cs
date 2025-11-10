using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SpotifyAPI.Web;
using SpotifyPlaylistWebApp.Services;

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

    private readonly string _clientId;
    private readonly IConfiguration _config;
    private readonly IStringLocalizer<SharedResource> _localizer;

    private readonly ILogger<HomeController> _logger;
    private readonly string _redirectUri;
    private readonly IServiceProvider _services;

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

        _clientId = FirstNonEmpty(
            _config["Spotify:ClientId"],
            Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID"))
            ?? throw new InvalidOperationException("Spotify ClientId fehlt.");

        _redirectUri = FirstNonEmpty(
            _config["Spotify:RedirectUri"],
            Environment.GetEnvironmentVariable("SPOTIFY_REDIRECT_URI"))
            ?? throw new InvalidOperationException("Spotify RedirectUri fehlt.");

        static string? FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }
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

    public IActionResult Terms()
    {
        return View();
    }

    public IActionResult Login()
    {
        // Generate PKCE codes and store verifier in session
        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        HttpContext.Session.SetString(SessionVerifierKey, verifier);

        var loginRequest = new LoginRequest(
            new Uri(_redirectUri), // MUST match Spotify Dashboard Redirect URI exactly
            _clientId,
            LoginRequest.ResponseType.Code)
        {
            CodeChallenge = challenge,
            CodeChallengeMethod = "S256",
            // ✅ Add read scopes so we can fetch user's playlists for Plex page
            Scope = new[]
            {
                Scopes.UserReadPrivate,
                Scopes.UserTopRead,
                Scopes.UserLibraryRead,
                Scopes.PlaylistModifyPrivate,
                Scopes.PlaylistModifyPublic,
                Scopes.PlaylistReadPrivate, // <-- added
                Scopes.PlaylistReadCollaborative // <-- added
            }
        };

        _logger.LogInformation("Spotify login started (PKCE challenge created).");
        return Redirect(loginRequest.ToUri().ToString());
    }

    [HttpGet("/callback")]
    public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string? error = null)
    {
        // Handle Spotify error response explicitly
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Spotify callback error: {Error}", error);
            return Error($"Spotify-Login fehlgeschlagen: {error}");
        }

        if (string.IsNullOrWhiteSpace(code))
            return Content("Fehler: Kein Spotify-Code erhalten.");

        try
        {
            var verifier = HttpContext.Session.GetString(SessionVerifierKey);
            if (string.IsNullOrEmpty(verifier))
            {
                _logger.LogWarning("PKCE verifier missing in session. Restarting login.");
                return RedirectToAction("Login");
            }

            // Exchange code for token using PKCE
            var tokenResponse = await new OAuthClient().RequestToken(
                new PKCETokenRequest(_clientId, code, new Uri(_redirectUri), verifier));

            if (string.IsNullOrEmpty(tokenResponse.AccessToken))
                return Content("Fehler: Kein Token erhalten.");

            // Store access token in session
            HttpContext.Session.SetString(SessionTokenKey, tokenResponse.AccessToken);
            _logger.LogInformation("Spotify token received and stored.");

            // ✅ If we came here from a protected flow (e.g. Plex export), jump back there
            var returnUrl = HttpContext.Session.GetString("ReturnAfterLogin");
            if (!string.IsNullOrEmpty(returnUrl))
            {
                HttpContext.Session.Remove("ReturnAfterLogin");
                return Redirect(returnUrl);
            }

            // Default: go to dashboard (existing behavior)
            return RedirectToAction("Dashboard");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify Callback failed");
            return Content("Ein Fehler ist beim Login aufgetreten. Bitte erneut versuchen.");
        }
    }


    // ============================
    // 🔸 Dashboard
    // ============================

    public async Task<IActionResult> Dashboard([FromServices] PlexService plex)
    {
        var token = HttpContext.Session.GetString(SessionTokenKey);
        if (string.IsNullOrEmpty(token))
            return RedirectToAction("Login");

        try
        {
            // === Create Spotify client ===
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token));
            var me = await spotify.UserProfile.Current();

            ViewBag.User = me?.DisplayName ?? "Spotify User";
            ViewBag.UserId = me?.Id;

            // === Fetch all playlists for the dropdown (source selector) ===
            try
            {
                var playlists = await plex.GetAllSpotifyPlaylistsAsync(spotify);
                ViewBag.Playlists = playlists;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load user playlists for dashboard dropdown.");
                ViewBag.Playlists = new List<SimplePlaylist>();
            }

            return View();
        }
        catch (APIUnauthorizedException)
        {
            _logger.LogWarning("Spotify Token invalid or expired.");
            HttpContext.Session.Remove(SessionTokenKey);
            return RedirectToAction("Login");
        }
        catch (APIException apiEx)
        {
            _logger.LogError("Spotify API error: {StatusCode} - {Message}\n{Body}",
                apiEx.Response.StatusCode, apiEx.Message, apiEx.Response.Body);

            if ((int)apiEx.Response.StatusCode == 403)
            {
                var msg = $"""
                           ℹ️ {_localizer["Spotify403_Info_Line1"]}<br>
                           {_localizer["Spotify403_Info_Line2"]}<br><br>
                           {_localizer["Spotify403_Info_Contact"]} <strong>info@inetconnector.com</strong><br>
                           {_localizer["Spotify403_Info_Action"]}
                           """;
                return Info(msg);
            }

            return Error($"""
                          🚨 Spotify API error ({(int)apiEx.Response.StatusCode}): {apiEx.Message}<br>
                          <small>{apiEx.Response.Body}</small>
                          """);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading dashboard.");
            return Error($"Unexpected error: {ex.Message}");
        }
    }


    private ContentResult Info(string msg)
    {
        return HtmlResult(msg, "#10b981"); // Smaragdgrün (freundlicher Hinweis)
    }
    // ============================
    // 🔸 Playlist-Aktionen
    // ============================

    [HttpGet]
    public IActionResult CreateTopMix()
    {
        var selectedPlaylistName = NormalizePlaylistName(HttpContext.Request.Query["playlistName"].FirstOrDefault());
        var actionName = _localizer["Dashboard_Top_Mix"].Value;
        return StartPlaylistJob(GetToken(), actionName, "[TopMix]", false, true, selectedPlaylistName);
    }

    [HttpGet]
    public IActionResult CreateTopList()
    {
        var selectedPlaylistName = NormalizePlaylistName(HttpContext.Request.Query["playlistName"].FirstOrDefault());
        var actionName = _localizer["Dashboard_Top_Liste"].Value;
        return StartPlaylistJob(GetToken(), actionName, "[TopList]", false, false, selectedPlaylistName);
    }

    [HttpGet]
    public IActionResult CreateFavMix()
    {
        var selectedPlaylistName = NormalizePlaylistName(HttpContext.Request.Query["playlistName"].FirstOrDefault());
        var actionName = _localizer["Dashboard_Favoriten_Mix"].Value;
        return StartPlaylistJob(GetToken(), actionName, "[FavMix]", true, true, selectedPlaylistName);
    }

    [HttpGet]
    public IActionResult CreateFavList()
    {
        var selectedPlaylistName = NormalizePlaylistName(HttpContext.Request.Query["playlistName"].FirstOrDefault());
        var actionName = _localizer["Dashboard_Favoriten_Liste"].Value;
        return StartPlaylistJob(GetToken(), actionName, "[FavList]", true, false, selectedPlaylistName);
    }

    [HttpGet]
    public IActionResult CreateAlternativeFavorites()
    {
        return StartAlternativeFavoritesJob(GetToken());
    }
     
    // === Alias für CloneLikedSongs (gleiche Funktion wie CreateFavList)
    [HttpGet]
    public IActionResult CloneLikedSongs()
    {
        return StartLikedCloneJob();
    }
    private string GetToken()
    {
        return HttpContext.Session.GetString(SessionTokenKey) ?? string.Empty;
    }

    private string? NormalizePlaylistName(string? rawName)
    {
        var trimmed = rawName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        if (string.Equals(trimmed, "LikedSongs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "#LikedSongs#", StringComparison.OrdinalIgnoreCase))
            return null;

        var likedLabel = _localizer["Dashboard_Source_LikedSongs"].Value;
        if (!string.IsNullOrWhiteSpace(likedLabel) &&
            string.Equals(trimmed, likedLabel.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        return trimmed;
    }

    private IActionResult StartPlaylistJob(string token, string namePrefix, string logTag, bool useLikedSongs,
    bool shuffled, string? basePlaylistName = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Warn(_localizer["Common_LoginFirst"].Value);

        try
        {
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token));

            return RunJob(async () =>
            {
                var me = await spotify.UserProfile.Current();
                await GeneratePlaylistAsync(spotify, me.Id, namePrefix, shuffled, useLikedSongs, _logger,
                    basePlaylistName);
            },
                async () =>
                {
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token))
                        .UserProfile.Current();
                    var pl = basePlaylistName ?? (useLikedSongs ? "LikedSongs" : "TopTracks");
                    return $"{me.Id}_{logTag}_{pl}";
                },
                namePrefix,
                logTag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Action} konnte nicht gestartet werden.", namePrefix);
            return Error(string.Format(_localizer["Error_ActionStartFailed"].Value, namePrefix, ex.Message));
        }
    }


    private IActionResult StartAlternativeFavoritesJob(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Warn(_localizer["Common_LoginFirst"].Value);

        try
        {
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token));
            var selectedPlaylistName = NormalizePlaylistName(HttpContext.Request.Query["playlistName"].FirstOrDefault());

            return RunJob(async () =>
            {
                var me = await spotify.UserProfile.Current();
                await GenerateAlternativeFavoritesAsync(spotify, me.Id, selectedPlaylistName);
            },
                async () =>
                {
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token))
                        .UserProfile.Current();
                    var pl = selectedPlaylistName ?? "LikedSongs";
                    return $"{me.Id}_[AltFavorites]_{pl}";
                },
                _localizer["Playlist_AlternativeFavorites"],
                "[AltFavorites]");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alternative Favorites konnten nicht gestartet werden.");
            return Error(string.Format(_localizer["Error_ActionStartFailed"].Value,
                _localizer["Playlist_AlternativeFavorites"].Value,
                ex.Message));
        }
    } 
    private IActionResult StartLikedCloneJob()
    {
        var token = GetToken();
        if (string.IsNullOrWhiteSpace(token))
            return Warn(_localizer["Common_LoginFirst"].Value);

        var likedSongsLabel = _localizer["Playlist_LikedSongs"].Value;

        try
        {
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token));
            var selectedPlaylistName = NormalizePlaylistName(HttpContext.Request.Query["playlistName"].FirstOrDefault()) ?? "LikedSongs";

            return RunJob(async () =>
            {
                var me = await spotify.UserProfile.Current();
                var liked = await spotify.Library.GetTracks(new LibraryTracksRequest { Limit = 50 });
                var tracks = new List<string>();
                while (true)
                {
                    tracks.AddRange(liked.Items.Where(i => i.Track != null).Select(i => i.Track.Uri));
                    if (string.IsNullOrEmpty(liked.Next)) break;
                    liked = await spotify.NextPage(liked);
                    await Task.Delay(ApiDelay);
                }

                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var playlistName = $"{likedSongsLabel} {today}";
                var newPl = await spotify.Playlists.Create(me.Id, new PlaylistCreateRequest(playlistName)
                {
                    Public = false,
                    Description = _localizer["Playlist_CopyOfLikedSongs"]
                });

                for (var i = 0; i < tracks.Count; i += 100)
                {
                    var chunk = tracks.Skip(i).Take(100).ToList();
                    if (chunk.Count == 0) break;
                    await spotify.Playlists.AddItems(newPl.Id, new PlaylistAddItemsRequest(chunk));
                    await Task.Delay(ApiDelay);
                }
            },
                async () =>
                {
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token))
                        .UserProfile.Current();
                    return $"{me.Id}_[LikedClone]_{selectedPlaylistName}";
                },
                likedSongsLabel,
                "[LikedClone]");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Action} konnte nicht gestartet werden.", likedSongsLabel);
            return Error(string.Format(_localizer["Error_ActionStartFailed"].Value,
                likedSongsLabel,
                ex.Message));
        }
    }


    [HttpGet]
    public IActionResult CreateRecommendations()
    {
        var token = GetToken();
        if (string.IsNullOrWhiteSpace(token))
            return Warn(_localizer["Common_LoginFirst"].Value);

        try
        {
            var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token));
            var selectedPlaylistName = NormalizePlaylistName(HttpContext.Request.Query["playlistName"].FirstOrDefault());

            return RunJob(async () =>
            {
                var me = await spotify.UserProfile.Current();
                await GenerateRecommendationsAsync(spotify, me.Id, selectedPlaylistName);
            },
                async () =>
                {
                    var me = await new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(token))
                        .UserProfile.Current();
                    var pl = selectedPlaylistName ?? "TopTracks";
                    return $"{me.Id}_[Recommendations]_{pl}";
                },
                _localizer["Playlist_Recommendations"],
                "[Recommendations]");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Empfehlungen konnten nicht gestartet werden.");
            return Error(string.Format(_localizer["Error_ActionStartFailed"].Value,
                _localizer["Playlist_Recommendations"].Value,
                ex.Message));
        }
    }


    // ============================
    // 🔸 Cooldown & Startlogik
    // ============================


    /// <summary>
    /// Executes a background playlist job with per-playlist cooldown protection.
    /// </summary>
    private IActionResult RunJob(
        Func<Task> job,
        Func<Task<string>> computeCooldownKeyAsync,
        string actionName,
        string logTag)
    {
        string key;
        try
        {
            // 🔹 Build a unique cooldown key (playlist-specific)
            key = computeCooldownKeyAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Cooldown key is empty.");

            // Use a consistent prefix similar to the frontend format
            key = $"cooldown:{actionName}:{Uri.EscapeDataString(key)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute cooldown key.");
            // ⚠️ Localized internal preparation error
            return Error(_localizer["Job_Error_Preparation"].Value);
        }

        lock (_lastActions)
        {
            if (_lastActions.TryGetValue(key, out var last) &&
                DateTime.UtcNow - last < CooldownDuration)
            {
                var remaining = CooldownDuration - (DateTime.UtcNow - last);

                // Format remaining time
                string timeMsg = remaining.TotalMinutes >= 1
                    ? $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2} min"
                    : $"{remaining.Seconds} sec";

                // ⏳ Localized cooldown message
                return Warn(string.Format(_localizer["Job_Warn_Cooldown"].Value, timeMsg));
            }

            // 🕓 Register current action timestamp
            _lastActions[key] = DateTime.UtcNow;
        }

        // 🔧 Start background task asynchronously
        _ = Task.Run(async () =>
        {
            using var scope = _logger.BeginScope($"PlaylistJob:{key}");
            try
            {
                _logger.LogInformation("{LogTag} job started.", logTag);
                await job();
                _logger.LogInformation("{LogTag} job completed successfully.", logTag);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{LogTag} error inside background job.", logTag);
            }
        });

        // ✅ Localized success message
        return OkMessage(string.Format(
            _localizer["Job_Success_Started"].Value,
            actionName
        ));
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
        ILogger logger,
        string? basePlaylistName = null)
    {
        var allTracks = new List<FullTrack>();
        const int pageSize = 50;

        try
        {
            // ────────────────────────────────────────────────
            // 1️⃣ If a specific playlist was selected → use that as source
            // ────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(basePlaylistName))
            {
                var playlists = await spotify.Playlists.CurrentUsers();
                var src = playlists.Items.FirstOrDefault(p =>
                    string.Equals(p.Name, basePlaylistName, StringComparison.OrdinalIgnoreCase));

                if (src != null)
                {
                    var items = await spotify.Playlists.GetItems(src.Id,
                        new PlaylistGetItemsRequest { Limit = pageSize });
                    while (true)
                    {
                        foreach (var i in items.Items)
                            if (i.Track is FullTrack t)
                                allTracks.Add(t);

                        if (string.IsNullOrEmpty(items.Next)) break;
                        items = await spotify.NextPage(items);
                        await Task.Delay(ApiDelay);
                    }

                    logger.LogInformation("GeneratePlaylistAsync: Using base playlist '{Name}' with {Count} tracks.",
                        basePlaylistName, allTracks.Count);
                }
                else
                {
                    logger.LogWarning(
                        "GeneratePlaylistAsync: Playlist '{Name}' not found. Falling back to default source.",
                        basePlaylistName);
                }
            }

            // ────────────────────────────────────────────────
            // 2️⃣ If still empty → fallback to liked or top tracks
            // ────────────────────────────────────────────────
            if (allTracks.Count == 0)
            {
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
            }

            // ────────────────────────────────────────────────
            // 3️⃣ If still empty → abort
            // ────────────────────────────────────────────────
            if (allTracks.Count == 0)
            {
                logger.LogWarning("{Prefix}: Keine Songs gefunden.", namePrefix);
                return;
            }

            // ────────────────────────────────────────────────
            // 4️⃣ Shuffle if requested
            // ────────────────────────────────────────────────
            var finalList = shuffled ? allTracks.OrderBy(_ => Guid.NewGuid()).ToList() : allTracks;
            var uris = finalList.Select(t => t.Uri).ToList();

            // ────────────────────────────────────────────────
            // 5️⃣ Build playlist name
            // ────────────────────────────────────────────────
            string playlistName;
            if (!string.IsNullOrWhiteSpace(basePlaylistName))
                playlistName = $"{basePlaylistName} – {namePrefix} {DateTime.Now:yyyy-MM-dd}";
            else
                playlistName = $"{namePrefix} {DateTime.Now:yyyy-MM-dd}";

            // ────────────────────────────────────────────────
            // 6️⃣ Create playlist
            // ────────────────────────────────────────────────
            var playlist = await spotify.Playlists.Create(userId, new PlaylistCreateRequest(playlistName)
            {
                Public = false,
                Description =
                    $"Automatically generated mix based on {basePlaylistName ?? (useLikedSongs ? "Liked Songs" : "Top Tracks")}"
            });

            // ────────────────────────────────────────────────
            // 7️⃣ Add items in chunks
            // ────────────────────────────────────────────────
            for (var i = 0; i < uris.Count; i += 100)
            {
                var chunk = uris.Skip(i).Take(100).ToList();
                if (chunk.Count > 0)
                    await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(chunk));
                await Task.Delay(ApiDelay);
            }

            var link = playlist.ExternalUrls?.GetValueOrDefault("spotify") ?? "(kein Link verfügbar)";
            logger.LogInformation("✅ Playlist '{Playlist}' created with {Count} songs: {Link}",
                playlistName, uris.Count, link);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Error in GeneratePlaylistAsync (namePrefix={NamePrefix}, base={BasePlaylist})",
                namePrefix, basePlaylistName ?? "null");
        }
    }

    private async Task GenerateAlternativeFavoritesAsync(SpotifyClient spotify, string userId,
        string? basePlaylistName = null)
    {
        try
        {
            var baseTracks = new List<FullTrack>();
            const int pageSize = 50;

            // 🔸 1️⃣ Determine source: liked songs OR specific playlist
            var useLikedSongsSource = string.IsNullOrWhiteSpace(basePlaylistName);

            if (!useLikedSongsSource)
            {
                // --- Use a selected playlist as base
                var allPlaylists = await spotify.Playlists.CurrentUsers();
                var basePlaylist = allPlaylists.Items.FirstOrDefault(p =>
                    string.Equals(p.Name, basePlaylistName, StringComparison.OrdinalIgnoreCase));

                if (basePlaylist == null)
                {
                    _logger.LogWarning("AlternativeFavorites: Playlist '{Name}' not found, fallback to liked songs.",
                        basePlaylistName);
                    useLikedSongsSource = true;
                    basePlaylistName = null;
                }
                else
                {
                    var playlistItems = await spotify.Playlists.GetItems(basePlaylist.Id,
                        new PlaylistGetItemsRequest { Limit = pageSize });
                    while (true)
                    {
                        foreach (var item in playlistItems.Items)
                            if (item.Track is FullTrack track)
                                baseTracks.Add(track);

                        if (string.IsNullOrEmpty(playlistItems.Next)) break;
                        playlistItems = await spotify.NextPage(playlistItems);
                        await Task.Delay(ApiDelay);
                    }

                    if (baseTracks.Count == 0)
                    {
                        _logger.LogWarning(
                            "AlternativeFavorites: Selected playlist '{Name}' contains no tracks, fallback to liked songs.",
                            basePlaylistName);
                        useLikedSongsSource = true;
                        basePlaylistName = null;
                    }
                    else
                    {
                        basePlaylistName = basePlaylist.Name;
                    }
                }
            }

            if (useLikedSongsSource)
            {
                baseTracks.Clear();
                for (var offset = 0;; offset += pageSize)
                {
                    var liked = await spotify.Library.GetTracks(new LibraryTracksRequest
                        { Limit = pageSize, Offset = offset });
                    if (liked.Items == null || liked.Items.Count == 0) break;

                    baseTracks.AddRange(liked.Items.Select(i => i.Track).Where(t => t != null)!);
                    if (liked.Items.Count < pageSize) break;
                    await Task.Delay(ApiDelay);
                }

                if (baseTracks.Count == 0)
                {
                    _logger.LogWarning("AlternativeFavorites: No liked songs found.");
                    return;
                }
            }

            // 🔸 2️⃣ Generate alternatives
            var baseUriSet = new HashSet<string>(baseTracks.Select(t => t.Uri), StringComparer.OrdinalIgnoreCase);
            var usedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var altUris = new List<string>();

            foreach (var track in baseTracks)
            {
                string? replacementUri = null;

                try
                {
                    // Try same album
                    var albumTracks = await spotify.Albums.GetTracks(track.Album.Id);
                    var fromAlbum = albumTracks.Items
                        .FirstOrDefault(t =>
                            t?.Uri != null && !baseUriSet.Contains(t.Uri) &&
                            !usedUris.Contains(t.Uri) && t.Id != track.Id);
                    replacementUri = fromAlbum?.Uri;
                }
                catch
                {
                }

                if (replacementUri == null)
                    try
                    {
                        // Try same artist
                        var artistId = track.Artists?.FirstOrDefault()?.Id;
                        if (!string.IsNullOrEmpty(artistId))
                        {
                            var top = await spotify.Artists.GetTopTracks(artistId, new ArtistsTopTracksRequest("DE"));
                            var fromArtist = top.Tracks.FirstOrDefault(t =>
                                !baseUriSet.Contains(t.Uri) &&
                                !usedUris.Contains(t.Uri) &&
                                t.Id != track.Id);
                            replacementUri = fromArtist?.Uri;
                        }
                    }
                    catch
                    {
                    }

                var finalUri = replacementUri ?? track.Uri;
                if (usedUris.Add(finalUri))
                    altUris.Add(finalUri);

                await Task.Delay(ApiDelay);
            }

            // 🔸 3️⃣ Naming based on source
            var namePrefix = string.IsNullOrWhiteSpace(basePlaylistName)
                ? _localizer["Playlist_AlternativeFavorites"].Value
                : $"{basePlaylistName} – Alternative Mix";

            var title = $"{namePrefix} {DateTime.Now:yyyy-MM-dd}";
            var playlist = await spotify.Playlists.Create(userId, new PlaylistCreateRequest(title)
            {
                Public = false,
                Description = $"Alternative version of {basePlaylistName ?? "Liked Songs"}"
            });

            for (var i = 0; i < altUris.Count; i += 100)
            {
                var chunk = altUris.Skip(i).Take(100).ToList();
                if (chunk.Count > 0)
                    await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(chunk));
                await Task.Delay(ApiDelay);
            }

            _logger.LogInformation("✅ Playlist '{Title}' created from source '{Source}' with {Count} songs.",
                title, basePlaylistName ?? "Liked Songs", altUris.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler in GenerateAlternativeFavoritesAsync");
        }
    }


    private async Task GenerateRecommendationsAsync(SpotifyClient spotify, string userId,
        string? basePlaylistName = null)
    {
        try
        {
            var seedTracks = new List<FullTrack>();
            const int pageSize = 50;

            // 1️⃣ Determine source of seeds
            if (string.IsNullOrWhiteSpace(basePlaylistName))
            {
                // Default: use user's top tracks
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

                seedTracks.AddRange(topTracks.Items);
            }
            else
            {
                // Alternative: use selected playlist as seed base
                var allPlaylists = await spotify.Playlists.CurrentUsers();
                var basePlaylist = allPlaylists.Items.FirstOrDefault(p =>
                    string.Equals(p.Name, basePlaylistName, StringComparison.OrdinalIgnoreCase));

                if (basePlaylist == null)
                {
                    _logger.LogWarning("Recommendations: Playlist '{Name}' not found, fallback to top tracks.",
                        basePlaylistName);
                    return;
                }

                var playlistItems = await spotify.Playlists.GetItems(basePlaylist.Id,
                    new PlaylistGetItemsRequest { Limit = pageSize });
                while (true)
                {
                    foreach (var item in playlistItems.Items)
                        if (item.Track is FullTrack track)
                            seedTracks.Add(track);

                    if (string.IsNullOrEmpty(playlistItems.Next)) break;
                    playlistItems = await spotify.NextPage(playlistItems);
                    await Task.Delay(ApiDelay);
                }

                if (seedTracks.Count == 0)
                {
                    _logger.LogWarning("Recommendations: Selected playlist '{Name}' contains no tracks.",
                        basePlaylistName);
                    return;
                }
            }

            // 2️⃣ Pick up to 5 random seeds
            var seedSubset = seedTracks
                .OrderBy(_ => Guid.NewGuid())
                .Take(5)
                .Select(t => t.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            // 3️⃣ Determine Spotify market
            var culture = CultureInfo.CurrentUICulture;
            var market = !string.IsNullOrEmpty(culture.Name) && culture.Name.Contains('-')
                ? culture.Name.Split('-').Last().ToUpperInvariant()
                : culture.TwoLetterISOLanguageName.ToUpperInvariant();

            _logger.LogInformation("Generating recommendations for '{Base}' with market '{Market}' and {Count} seeds.",
                basePlaylistName ?? "TopTracks", market, seedSubset.Count);

            // 4️⃣ Request recommendations
            var recRequest = new RecommendationsRequest
            {
                Limit = 50,
                Market = market
            };
            foreach (var id in seedSubset)
                recRequest.SeedTracks.Add(id);

            var recs = await spotify.Browse.GetRecommendations(recRequest);
            if (recs.Tracks == null || recs.Tracks.Count == 0)
            {
                _logger.LogWarning("Recommendations: Spotify returned no tracks.");
                return;
            }

            // 5️⃣ Create new playlist
            var titlePrefix = string.IsNullOrWhiteSpace(basePlaylistName)
                ? "Recommended Songs"
                : $"{basePlaylistName} – Recommended Mix";

            var title = $"{titlePrefix} {DateTime.Now:yyyy-MM-dd}";
            var playlist = await spotify.Playlists.Create(userId, new PlaylistCreateRequest(title)
            {
                Public = false,
                Description = $"Recommended songs based on {basePlaylistName ?? "Top Tracks"}"
            });

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

            _logger.LogInformation("✅ Playlist '{Title}' created ({Count} songs) based on '{Base}'.",
                title, uris.Count, basePlaylistName ?? "TopTracks");
        }
        catch (APIException apiEx)
        {
            _logger.LogError(apiEx, "Spotify API error while generating recommendations: {Status} - {Message}",
                apiEx.Response.StatusCode, apiEx.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GenerateRecommendationsAsync.");
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
        var culture = CultureInfo.CurrentUICulture;
        var lang = culture.Name;
        var backToDashboard = _localizer["Common_BackToDashboard"].Value;

        var html = $@"
<!doctype html>
<html lang='{lang}'>
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
<a href='/Home/Dashboard' class='px-5 py-3 rounded-xl bg-emerald-500 hover:bg-emerald-600 text-white no-underline'>{backToDashboard}</a>
</div>
</body>
</html>";
        return Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }
}
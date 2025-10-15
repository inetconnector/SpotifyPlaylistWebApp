using System.Text;
using Microsoft.AspNetCore.Mvc;
using SpotifyAPI.Web;

namespace SpotifyPlaylistWebApp.Controllers;

public class HomeController : Controller
{
    private static readonly string _clientId = "89d26c134b954a28b78b74cfb893b71b";
    private static readonly string _redirectUri = "https://playlists.inetconnector.com/callback/";
    private static string _verifier;
    private static string _accessToken;

    // 🧠 Cooldown-Speicher: pro Benutzer + Aktion
    private static readonly Dictionary<string, DateTime> _lastActions = new();
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(4);

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
        (_verifier, var challenge) = PKCEUtil.GenerateCodes();

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
                Scopes.PlaylistModifyPrivate,
                Scopes.PlaylistModifyPublic
            }
        };

        return Redirect(loginRequest.ToUri().ToString());
    }

    [HttpGet("/callback")]
    public async Task<IActionResult> Callback([FromQuery] string code)
    {
        var tokenResponse = await new OAuthClient().RequestToken(
            new PKCETokenRequest(_clientId, code, new Uri(_redirectUri), _verifier)
        );

        _accessToken = tokenResponse.AccessToken;
        return RedirectToAction("Dashboard");
    }

    // ============================
    // 🔸 Dashboard
    // ============================

    public async Task<IActionResult> Dashboard()
    {
        if (_accessToken == null)
            return RedirectToAction("Index");

        var config = SpotifyClientConfig.CreateDefault().WithToken(_accessToken);
        var spotify = new SpotifyClient(config);
        var me = await spotify.UserProfile.Current();

        ViewBag.User = me.DisplayName;
        return View();
    }

    // ============================
    // 🔸 Playlist-Aktionen
    // ============================

    [HttpGet]
    public IActionResult CreateFavoritesOrdered()
        => StartPlaylistJob(false, false, "Lieblingssongs Original", "[Ordered]");

    [HttpGet]
    public IActionResult CreateFavoritesShuffled()
        => StartPlaylistJob(true, false, "Lieblingssongs Shuffled", "[Shuffled]");

    [HttpGet]
    public IActionResult CreateFavoritesSimilar()
        => StartPlaylistJob(false, true, "Lieblingssongs Similar", "[Similar]");

    // ============================
    // 🔸 Cooldown & Startlogik
    // ============================

    private IActionResult StartPlaylistJob(bool shuffled, bool similar, string namePrefix, string logTag)
    {
        if (_accessToken == null)
            return Content(RenderMessage("⚠️ Bitte zuerst einloggen.", "warning"),
                "text/html; charset=utf-8", Encoding.UTF8);

        var userKey = $"{_accessToken}_{logTag}";

        // ⏳ Cooldown-Prüfung
        lock (_lastActions)
        {
            if (_lastActions.TryGetValue(userKey, out var last)
                && DateTime.UtcNow - last < CooldownDuration)
            {
                var remaining = CooldownDuration - (DateTime.UtcNow - last);
                var minutes = (int)remaining.TotalMinutes;
                var seconds = remaining.Seconds;
                return Content(RenderMessage(
                    $"⏳ Bitte warte {minutes}:{seconds:D2} Minuten, bevor du diese Aktion erneut startest.", "warning"),
                    "text/html; charset=utf-8", Encoding.UTF8);
            }

            _lastActions[userKey] = DateTime.UtcNow;
        }

        // 🧩 Startet Playlist-Erstellung im Hintergrund
        Task.Run(async () =>
        {
            try
            {
                await GenerateFavoritesPlaylistAsync(_accessToken, shuffled, similar, namePrefix);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logTag} Fehler: {ex.Message}");
            }
        });

        return Content(RenderMessage("✅ Playlist-Erstellung gestartet.<br>Bitte in 1–2 Minuten in Spotify® prüfen."),
            "text/html; charset=utf-8", Encoding.UTF8);
    }

    // ============================
    // 🔸 Playlist-Logik
    // ============================

    private static async Task GenerateFavoritesPlaylistAsync(
        string token, bool shuffled, bool similar, string namePrefix)
    {
        var config = SpotifyClientConfig.CreateDefault().WithToken(token);
        var spotify = new SpotifyClient(config);
        var me = await spotify.UserProfile.Current();

        var allTopTracks = new List<FullTrack>();
        var offset = 0;
        const int pageSize = 50;

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

            allTopTracks.AddRange(batch.Items);
            offset += pageSize;
            if (batch.Items.Count < pageSize) break;
        }

        if (allTopTracks.Count == 0)
        {
            Console.WriteLine($"{namePrefix}: Keine Top-Tracks gefunden.");
            return;
        }

        List<string> uris;

        if (similar)
        {
            var similarUris = new List<string>();
            foreach (var t in allTopTracks)
            {
                if (t.Artists == null || t.Artists.Count == 0) continue;
                var artistId = t.Artists.First().Id;
                var trackId = t.Id;

                try
                {
                    var recRequest = new RecommendationsRequest { Limit = 10 };
                    recRequest.SeedArtists.Add(artistId);
                    recRequest.SeedTracks.Add(trackId);
                    var recs = await spotify.Browse.GetRecommendations(recRequest);

                    SimpleTrack? alt = null;
                    var highestPopularity = -1;
                    foreach (var st in recs.Tracks.Where(x => x.Artists.Any(a => a.Id == artistId) && x.Id != trackId))
                        try
                        {
                            var full = await spotify.Tracks.Get(st.Id);
                            if (full.Popularity > highestPopularity)
                            {
                                alt = st;
                                highestPopularity = full.Popularity;
                            }
                        }
                        catch { }

                    if (alt != null)
                        similarUris.Add(alt.Uri);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Similar] Fehler bei {t.Name}: {ex.Message}");
                }
            }
            uris = similarUris.Distinct().ToList();
        }
        else
        {
            var finalList = (shuffled
                ? allTopTracks.OrderBy(_ => Guid.NewGuid()).ToList()
                : allTopTracks.ToList());

            uris = finalList.Select(t => t.Uri).ToList();
        }

        var playlistName = $"{namePrefix} {DateTime.Now:yyyy-MM-dd}";
        var playlist = await spotify.Playlists.Create(me.Id,
            new PlaylistCreateRequest(playlistName) { Public = false });

        for (var i = 0; i < uris.Count; i += 100)
        {
            var chunk = uris.Skip(i).Take(100).ToList();
            if (chunk.Count > 0)
                await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(chunk));
        }

        Console.WriteLine($"✅ {playlistName} erstellt mit {uris.Count} Songs: {playlist.ExternalUrls["spotify"]}");
    }

    // ============================
    // 🔸 HTML für Rückmeldungen
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
                    background: #0b0f17;
                    color: white;
                    font-family: system-ui, sans-serif;
                }}
                .glass {{
                    background: rgba(255,255,255,0.08);
                    backdrop-filter: blur(12px);
                    border: 1px solid rgba(255,255,255,0.12);
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

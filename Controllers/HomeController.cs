using Microsoft.AspNetCore.Mvc;
using SpotifyAPI.Web;

namespace SpotifyPlaylistWebApp.Controllers;

public class HomeController : Controller
{
    private static readonly string _clientId = "89d26c134b954a28b78b74cfb893b71b";
    private static readonly string _redirectUri = "https://spotify.inetconnector.com/callback/";
    private static string _verifier;
    private static string _accessToken;

    public IActionResult Index()
    {
        return View();
    }


    public IActionResult Login()
    {
        (_verifier, var challenge) = PKCEUtil.GenerateCodes();

        var loginRequest = new LoginRequest(
            new Uri(_redirectUri),
            _clientId,
            LoginRequest.ResponseType.Code
        )
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

        var uri = loginRequest.ToUri();
        return Redirect(uri.ToString());
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


    public async Task<IActionResult> Dashboard()
    {
        if (_accessToken == null) return RedirectToAction("Index");

        var config = SpotifyClientConfig.CreateDefault().WithToken(_accessToken);
        var spotify = new SpotifyClient(config);

        var me = await spotify.UserProfile.Current();
        ViewBag.User = me.DisplayName;
        return View();
    }
    public async Task<IActionResult> CreateFavorites()
    {
        var config = SpotifyClientConfig.CreateDefault().WithToken(_accessToken);
        var spotify = new SpotifyClient(config);
        var me = await spotify.UserProfile.Current();

        var allTopTracks = new List<FullTrack>();
        int offset = 0;
        const int limit = 50;

        while (true)
        {
            var req = new PersonalizationTopRequest
            {
                TimeRangeParam = PersonalizationTopRequest.TimeRange.LongTerm,
                Limit = limit,
                Offset = offset
            };

            var batch = await spotify.Personalization.GetTopTracks(req);
            if (batch.Items.Count == 0)
                break;

            allTopTracks.AddRange(batch.Items);
            offset += limit;

            if (batch.Items.Count < limit)
                break;
        }

        if (allTopTracks.Count == 0)
            return Content("⚠️ Keine Top-Tracks gefunden. Bitte Spotify-App häufiger benutzen oder neu einloggen (user-top-read-Token).");

        var uris = allTopTracks.Select(t => t.Uri).ToList();

        var playlist = await spotify.Playlists.Create(me.Id,
            new PlaylistCreateRequest($"Lieblingssongs {DateTime.Now:yyyy-MM-dd}")
            {
                Public = false
            });

        for (int i = 0; i < uris.Count; i += 100)
        {
            var batch = uris.Skip(i).Take(100).ToList();
            await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(batch));
        }

        return Content($"✅ Playlist '{playlist.Name}' erstellt mit {uris.Count} Songs.<br>" +
                       $"<a href='{playlist.ExternalUrls["spotify"]}' target='_blank'>Öffnen in Spotify</a>",
            "text/html");
    }


    //public async Task<IActionResult> CreateFavorites()
    //{
    //    var config = SpotifyClientConfig.CreateDefault().WithToken(_accessToken);
    //    var spotify = new SpotifyClient(config);
    //    var me = await spotify.UserProfile.Current();

    //    var top = await spotify.Personalization.GetTopTracks(new PersonalizationTopRequest
    //    {
    //        TimeRangeParam = PersonalizationTopRequest.TimeRange.ShortTerm,
    //        Limit = 50
    //    });

    //    var uris = top.Items.Select(t => t.Uri).ToList();
    //    var playlist = await spotify.Playlists.Create(me.Id,
    //        new PlaylistCreateRequest("Lieblingssongs " + DateTime.Now.ToString("yyyy-MM-dd"))
    //        {
    //            Public = false
    //        });

    //    await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(uris));
    //    return Content(playlist.ExternalUrls["spotify"]);
    //}

    public async Task<IActionResult> CreateRecommendations()
    {
        var config = SpotifyClientConfig.CreateDefault().WithToken(_accessToken);
        var spotify = new SpotifyClient(config);
        var me = await spotify.UserProfile.Current();

        // 1️⃣ Lieblingssongs abrufen
        var top = await spotify.Personalization.GetTopTracks(new PersonalizationTopRequest
        {
            TimeRangeParam = PersonalizationTopRequest.TimeRange.ShortTerm,
            Limit = 10
        });

        var uris = new List<string>();

        foreach (var t in top.Items)
        {
            if (t.Album == null) continue;

            try
            {
                // 2️⃣ Alle Tracks des Albums abrufen
                var albumTracks = await spotify.Albums.GetTracks(t.Album.Id);

                // 3️⃣ Original-Popularität abrufen
                var originalTrack = await spotify.Tracks.Get(t.Id);
                var originalPopularity = originalTrack.Popularity;

                // 4️⃣ Alle Tracks dieses Albums (außer der Originale)
                var candidates = new List<FullTrack>();
                foreach (var at in albumTracks.Items)
                {
                    if (at.Id == t.Id) continue;
                    var details = await spotify.Tracks.Get(at.Id);
                    candidates.Add(details);
                }

                // 5️⃣ Den ähnlich beliebten Song auswählen
                var similar = candidates
                    .OrderBy(x => Math.Abs(x.Popularity - originalPopularity))
                    .FirstOrDefault();

                if (similar != null)
                    uris.Add(similar.Uri);
            }
            catch (APIException ex)
            {
                Console.WriteLine($"⚠️ Spotify API error for {t.Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler: {ex.Message}");
            }
        }

        if (uris.Count == 0)
            return Content("Keine ähnlichen Songs aus Alben gefunden.");

        // 6️⃣ Playlist erstellen
        var playlist = await spotify.Playlists.Create(me.Id, new PlaylistCreateRequest(
            $"Ähnliche Album-Songs {DateTime.Now:yyyy-MM-dd}")
        {
            Public = false
        });

        await spotify.Playlists.AddItems(playlist.Id, new PlaylistAddItemsRequest(uris));

        return Content($"✅ Playlist erstellt: <a href='{playlist.ExternalUrls["spotify"]}' target='_blank'>{playlist.ExternalUrls["spotify"]}</a>", "text/html");
    }


}
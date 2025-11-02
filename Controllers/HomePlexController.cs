using LukeHagar.PlexAPI.SDK;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using SpotifyAPI.Web;
using SpotifyPlaylistWebApp.Services;
using System.Text.Json;
using System.Xml;

namespace SpotifyPlaylistWebApp.Controllers
{
    [Route("Home")]
    [Route("HomePlex")]
    public class HomePlexController : Controller
    {

        public sealed class TokenDto
        {
            public string? Token { get; set; }
        }

        private const string SessionSpotifyTokenKey = "SpotifyAccessToken";
        private readonly IStringLocalizer<SharedResource> _localizer;

        public HomePlexController(IStringLocalizer<SharedResource> localizer)
        {
            _localizer = localizer;
        }


        [Route("Home")]
        [Route("HomePlex")]
        [HttpPost("/Plex/SaveToken")]
        public IActionResult SaveToken([FromBody] TokenDto dto)
        {
            var token = dto?.Token;
            if (string.IsNullOrWhiteSpace(token)) return BadRequest(new { ok = false });
            HttpContext.Session.SetString("PlexAuthToken", token);
            return Ok(new { ok = true });
        }


        // ===============================================================
        // 🔸 Main page after Plex + Spotify login
        // ===============================================================
        [HttpGet("SpotifyToPlex")]
        public async Task<IActionResult> SpotifyToPlex([FromServices] PlexService plex)
        {
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);

            if (string.IsNullOrEmpty(plexToken))
                return RedirectToAction("PlexActions");

            if (string.IsNullOrEmpty(spotifyToken))
            {
                HttpContext.Session.SetString("ReturnAfterLogin", "/HomePlex/SpotifyToPlex");
                return RedirectToAction("Login", "Home");
            }

            try
            {
                // ✅ Load all user playlists for selection
                var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
                var playlists = await plex.GetAllSpotifyPlaylistsAsync(spotify);

                // ===========================================================
                // 🔹 Add localized "Liked Songs" pseudo-playlist at the top
                // ===========================================================
                var likedLabel = _localizer["Dashboard_Source_LikedSongs"].Value?.Trim();

                // Remove duplicates (if Spotify already returned something similar)
                playlists.RemoveAll(p =>
                    string.Equals(p.Value, "#LikedSongs#", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Text, likedLabel, StringComparison.OrdinalIgnoreCase));

                playlists.Insert(0, new SelectListItem
                {
                    Value = "#LikedSongs#",
                    Text = "♥ " + likedLabel
                });

                ViewBag.Playlists = playlists;

                // Optional: localized page title
                ViewData["Title"] = "Spotify → Plex";

                return View("PlexActions");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SpotifyToPlex] " + ex);
                TempData["Error"] = _localizer["Plex_StatusLoadError"].Value;
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

            // Save Plex token and continue with Spotify login
            HttpContext.Session.SetString("PlexAuthToken", token);
            HttpContext.Session.SetString("ReturnAfterLogin", "/HomePlex/SpotifyToPlex");

            return RedirectToAction("Login", "Home");
        }

        // ==============================================================
        // 🔸 Disconnect Plex manually
        // ==============================================================
        [HttpPost("DisconnectPlex")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DisconnectPlex([FromServices] IPlexTokenStore tokenStore)
        {
            try
            {
                var userKey = HttpContext.Session.Id;
                await tokenStore.DeleteAsync(userKey);

                HttpContext.Session.Remove("PlexAuthToken");
                HttpContext.Session.Remove("PlexBaseUrl");
                HttpContext.Session.Remove("PlexMachineId");

                // vorhandener Key im RESX
                TempData["Info"] = _localizer["PlexActions_Info_Disconnected"].Value;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Plex Disconnect] " + ex);
                TempData["Error"] = _localizer["Plex_DisconnectError"].Value;
            }

            return RedirectToAction("Index", "Home");
        }

        // ============================================================
        // 🔸 ExportOneLive — single playlist via SSE
        // ============================================================
        [HttpGet("ExportOneLive")]
        public async Task ExportOneLive(
            [FromServices] PlexService plex,
            [FromServices] IStringLocalizer<SharedResource> L,
            string playlistId,
            string playlistName)
        {
            var exportId = Guid.NewGuid().ToString();
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");

            await using var writer = new StreamWriter(Response.Body);
            PlexService.RegisterSseStream(exportId, writer);

            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            var (baseUrl, defaultMachineId) = await plex.DiscoverServerAsync(plexToken);
            var missingList = new List<(string Artist, string Title, string? Album)>();

            try
            {
                var spotifyToken = HttpContext.Session.GetString("SpotifyAccessToken");
                if (spotifyToken == null || plexToken == null)
                {
                    await plex.SendSseAsync(exportId, $"❌ {L["SpotifyToPlex_TokenExpired"]}");
                    return;
                }

                var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
                var tracks = await plex.GetSpotifyPlaylistTracksAsync(spotify, playlistId);

                await plex.SendSseAsync(exportId,
                    $"{L["SpotifyToPlex_Exporting"]}: '{playlistName}' ({tracks.Count} {L["SpotifyToPlex_Tracks"]})");

                var existing = await plex.GetPlexPlaylistsAsync(baseUrl, plexToken);
                var old = existing.FirstOrDefault(p =>
                    p.Title.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

                string plexPlaylistKey;

                if (!string.IsNullOrEmpty(old.RatingKey))
                {
                    plexPlaylistKey = old.RatingKey;
                    await plex.SendSseAsync(exportId, $"♻️ {L["SpotifyToPlex_Continuing"]}: {playlistName}");
                }
                else
                {
                    await plex.SendSseAsync(exportId, $"🪄 {L["SpotifyToPlex_CreatingPlaylist"]}: {playlistName}");
                    plexPlaylistKey = await plex.CreatePlaylistAsync(baseUrl, plexToken, playlistName);
                    if (string.IsNullOrEmpty(plexPlaylistKey))
                    {
                        await plex.SendSseAsync(exportId, "❌ ERROR Could not create Plex playlist.");
                        return;
                    }
                }

                int failed = 0, added = 0, missing = 0, total = tracks.Count;

                var oldCache = PlexService.GetMissingCacheSnapshot();
                var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (oldCache.TryGetValue(playlistName, out var cached))
                {
                    await plex.SendSseAsync(exportId,
                        $"📄 {L["SpotifyToPlex_Continuing"]}: {cached.Items.Count} cached entries found");

                    foreach (var entry in cached.Items)
                    {
                        var parts = entry.Split('—');
                        if (parts.Length >= 2)
                        {
                            var artist = parts[0].Trim();
                            var title = parts.Last().Trim();
                            processed.Add($"{artist}—{title}");
                            missingList.Add((artist, title, parts.Length > 2 ? parts[1].Trim() : null));
                        }
                    }
                }

                // ============================================================
                // 🔸 Batch-wise export (keep 1 to be safe with Plex)
                // ============================================================
                const int batchSize = 1;
                var matchedKeys = new List<string>();

                foreach (var (title, artist) in tracks)
                {
                    if (processed.Contains($"{artist}—{title}"))
                        continue;

                    await plex.SendSseAsync(exportId, $"🔍 {L["SpotifyToPlex_Searching"]}: {artist} — {title}");

                    var found = await plex.SearchTracksOnPlexAsync(baseUrl, plexToken,
                        new List<(string Title, string Artist)> { (title, artist) });
                    var match = found.FirstOrDefault();

                    if (!string.IsNullOrEmpty(match.RatingKey))
                    {
                        matchedKeys.Add(match.RatingKey);
                        added++;
                        await plex.SendSseAsync(exportId, $"✅ {L["SpotifyToPlex_Found"]}: {artist} — {title}");
                    }
                    else
                    {
                        missing++;
                        missingList.Add((artist, title, null));
                        await plex.SendSseAsync(exportId, $"❌ {L["SpotifyToPlex_MissingTrack"]}: {artist} — {title}");
                    }

                    if (matchedKeys.Count >= batchSize)
                    {
                        await plex.AddTracksToPlaylistAsync(
                            baseUrl, plexToken, plexPlaylistKey,
                            matchedKeys, defaultMachineId, exportId);

                        matchedKeys.Clear();
                        await plex.SendSseAsync(exportId, $"progress:{added}:{missing}:{total}");
                    }

                    PlexService.UpdateMissingCache(playlistName, missingList);
                    PlexService.SaveMissingCacheToFile();
                }

                // send remaining batch
                if (matchedKeys.Count > 0)
                {
                    await plex.AddTracksToPlaylistAsync(
                        baseUrl, plexToken, plexPlaylistKey,
                        matchedKeys, defaultMachineId, exportId);
                }

                PlexService.UpdateMissingCache(playlistName, missingList);
                PlexService.SaveMissingCacheToFile();
                await plex.SendSseAsync(exportId, $"done:{added}:{missing}:{failed}:{total}");
            }
            catch (Exception ex)
            {
                await plex.SendSseAsync(exportId, $"❌ ERROR {ex.Message}");
                PlexService.SaveMissingCacheToFile();
            }
            finally
            {
                try
                {
                    await plex.SendSseAsync(exportId, $"⚠️ {L["SpotifyToPlex_ExportAbortedOrFinished"]}");
                    await writer.FlushAsync();
                }
                catch
                {
                    Console.WriteLine("[ExportOneLive] SSE client disconnected before flush.");
                }

                plex.SaveMissingCacheForServer(defaultMachineId);
                PlexService.UnregisterSseStream(exportId);
                PlexService.SaveMissingCacheToFile();
            }
        }

        // ============================================================
        // 🔸 Download missing tracks for last or specific playlist
        // ============================================================
        [HttpGet("DownloadMissing")]
        public IActionResult DownloadMissing([FromServices] PlexService plex, string? playlistName)
        {
            try
            {
                var plexToken = HttpContext.Session.GetString("PlexAuthToken");
                if (string.IsNullOrEmpty(plexToken))
                    return Unauthorized();

                // Ermittle Plex-Server-ID synchron
                var (_, machineId) = plex.DiscoverServerAsync(plexToken).GetAwaiter().GetResult();

                // Lade Cache von Datei (wenn noch nicht geladen)
                plex.LoadMissingCacheForServer(machineId);

                // Hole Snapshot
                var cache = PlexService.GetMissingCacheSnapshot();

                if (cache.Count == 0)
                    return Content(_localizer["SpotifyToPlex_NoMissingCacheFound"].Value, "text/plain");

                // Wenn kein Playlist-Name angegeben → letzte exportierte Playlist nehmen
                if (string.IsNullOrWhiteSpace(playlistName))
                    playlistName = cache.Last().Key;

                if (!cache.TryGetValue(playlistName, out var missing) || missing.Items.Count == 0)
                    return Content(_localizer["SpotifyToPlex_NoMissingForPlaylist"].Value, "text/plain");

                // CSV generieren
                var csv = PlexService.GenerateMissingCsv(missing.Items, playlistName);

                // Eintrag löschen und Cache speichern
                plex.RemoveMissingEntry(machineId, playlistName);

                return File(csv, "text/csv", $"{playlistName}_missing.csv");
            }
            catch (Exception ex)
            {
                return Content($"Error while exporting missing list: {ex.Message}", "text/plain");
            }
        }

        // ==============================================================
        // 🔸 AJAX: Get all Plex playlists (Title + RatingKey)
        // ==============================================================
        [HttpGet("GetPlexPlaylists")]
        public async Task<IActionResult> GetPlexPlaylists([FromServices] PlexService plex)
        {
            try
            {
                var plexToken = HttpContext.Session.GetString("PlexAuthToken");
                if (string.IsNullOrEmpty(plexToken))
                    return Json(new { success = false, message = _localizer["Plex_NoToken"].Value });

                var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);
                plex.LoadMissingCacheForServer(machineId);
                var playlists = await plex.GetPlexPlaylistsAsync(baseUrl, plexToken);

                // Umwandeln in serialisierbare Objekte
                var result = playlists.Select(p => new { title = p.Title, ratingKey = p.RatingKey }).ToList();

                return Json(new { success = true, playlists = result });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[GetPlexPlaylists] " + ex);
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ==============================================================
        // 🔸 Raw playlists XML (debug)
        // ==============================================================
        [HttpGet("GetPlexPlaylistsRaw")]
        public async Task<IActionResult> GetPlexPlaylistsRaw([FromServices] PlexService plex)
        {
            try
            {
                var plexToken = HttpContext.Session.GetString("PlexAuthToken");
                if (string.IsNullOrEmpty(plexToken))
                    return Content(_localizer["Plex_NoToken"].Value);

                var (baseUrl, machineId) = await plex.DiscoverServerAsync(plexToken);
                plex.LoadMissingCacheForServer(machineId);
                var xml = await plex.GetRawPlaylistsXmlAsync(baseUrl, plexToken);

                return Content(xml, "application/xml");
            }
            catch (Exception ex)
            {
                return Content($"Error: {ex.Message}");
            }
        }

        // ==============================================================
        // 🔸 AJAX: Get tracks of one Plex playlist (parsed JSON)
        // ==============================================================
        [HttpGet("GetPlexPlaylistTracks")]
        public async Task<IActionResult> GetPlexPlaylistTracks([FromServices] PlexService plex,
            [FromServices] IStringLocalizer<SharedResource> L,
            string ratingKey)
        {
            try
            {
                var plexToken = HttpContext.Session.GetString("PlexAuthToken");
                var (baseUrl, defaultMachineId) = await plex.DiscoverServerAsync(plexToken);

                if (string.IsNullOrEmpty(plexToken))
                    return Json(new { success = false, message = L["Plex_NoToken"].Value });

                if (string.IsNullOrEmpty(ratingKey))
                    return Json(new { success = false, message = L["SpotifyToPlex_MissingRatingKey"].Value });

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("X-Plex-Token", plexToken);

                var url = $"{baseUrl}/playlists/{ratingKey}/items";
                var resp = await client.GetAsync(url);

                if (!resp.IsSuccessStatusCode)
                    return Json(new
                    {
                        success = false,
                        message = $"{L["SpotifyToPlex_PlexApiError"].Value} {resp.StatusCode}"
                    });

                var xml = await resp.Content.ReadAsStringAsync();
                var doc = new XmlDocument();
                doc.LoadXml(xml);

                var tracks = new List<object>();
                foreach (XmlNode node in doc.SelectNodes("//Track"))
                {
                    var title = node.Attributes["title"]?.Value ?? "";
                    var artist = node.Attributes["grandparentTitle"]?.Value ?? "";
                    var album = node.Attributes["parentTitle"]?.Value ?? "";
                    tracks.Add(new { title, artist, album });
                }

                return Json(new { success = true, tracks });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[GetPlexPlaylistTracks] " + ex);
                return Json(new
                {
                    success = false,
                    message = $"{L["SpotifyToPlex_LoadError"].Value}: {ex.Message}"
                });
            }
        }


        // ==============================================================
        // 🔸 Delete Plex playlist
        // ==============================================================
        [HttpPost("DeletePlexPlaylist")]
        public async Task<IActionResult> DeletePlexPlaylist(
            [FromServices] PlexService plex,
            [FromBody] JsonElement payload)
        {
            try
            {
                var token = HttpContext.Session.GetString("PlexAuthToken");
                if (string.IsNullOrEmpty(token))
                    return Json(new { success = false, message = _localizer["Plex_NoToken"].Value });

                if (!payload.TryGetProperty("key", out var keyProp))
                    return Json(new { success = false, message = _localizer["Job_Error_Preparation"].Value });

                var ratingKey = keyProp.GetString();
                if (string.IsNullOrWhiteSpace(ratingKey))
                    return Json(new { success = false, message = _localizer["Job_Error_Preparation"].Value });

                var (baseUrl, machineId) = await plex.DiscoverServerAsync(token);
                plex.LoadMissingCacheForServer(machineId);

                await plex.DeletePlaylistAsync(baseUrl, token, ratingKey);

                return Json(new { success = true, message = _localizer["SpotifyToPlex_DeleteSuccess"].Value });
            }
            catch (HttpRequestException httpEx)
            {
                Console.WriteLine("[DeletePlexPlaylist:HTTP] " + httpEx);
                return Json(new
                {
                    success = false,
                    message = $"{_localizer["Plex_DisconnectError"].Value} (HTTP)"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DeletePlexPlaylist] " + ex);
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ==============================================================
        // 🔸 Result page after single export
        // ==============================================================
        [HttpGet("ExportResult")]
        public IActionResult ExportResult()
        {
            return View("ExportResult");
        }

        // ============================================================
        // 🔸 ExportAllLive — Multi-Playlist Live Export with SSE
        // ============================================================
        [HttpGet("ExportAllLive")]
        public async Task ExportAllLive(
            [FromServices] PlexService plex,
            [FromServices] IStringLocalizer<SharedResource> L)
        {
            var exportId = Guid.NewGuid().ToString();
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");

            await using var writer = new StreamWriter(Response.Body);
            PlexService.RegisterSseStream(exportId, writer);

            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            var spotifyToken = HttpContext.Session.GetString("SpotifyAccessToken");

            if (plexToken == null || spotifyToken == null)
            {
                await plex.SendSseAsync(exportId, $"❌ {L["SpotifyToPlex_TokenExpired"]}");
                return;
            }

            try
            {
                var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
                var (baseUrl, defaultMachineId) = await plex.DiscoverServerAsync(plexToken);
                plex.LoadMissingCacheForServer(defaultMachineId);

                var playlists = await plex.GetAllSpotifyPlaylistsAsync(spotify);
                var totalPlaylists = playlists.Count;
                int processedPlaylists = 0;

                // kein Sprachwort "playlists" anhängen -> rein numerisch + Start-Text
                await plex.SendSseAsync(exportId, $"🎧 {L["SpotifyToPlex_Starting"]}: {totalPlaylists}");

                foreach (var item in playlists)
                {
                    processedPlaylists++;
                    var playlistId = item.Value;
                    var playlistName = item.Text;
                    var missingList = new List<(string Artist, string Title, string? Album)>();

                    await plex.SendSseAsync(exportId, $"🎵 {L["SpotifyToPlex_Exporting"]}: {playlistName}");

                    var existing = await plex.GetPlexPlaylistsAsync(baseUrl, plexToken);
                    var old = existing.FirstOrDefault(p =>
                        p.Title.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

                    string plexPlaylistKey;
                    if (!string.IsNullOrEmpty(old.RatingKey))
                    {
                        plexPlaylistKey = old.RatingKey;
                        await plex.SendSseAsync(exportId, $"♻️ {L["SpotifyToPlex_Continuing"]}: {playlistName}");
                    }
                    else
                    {
                        plexPlaylistKey = await plex.CreatePlaylistAsync(baseUrl, plexToken, playlistName);
                        await plex.SendSseAsync(exportId, $"🪄 {L["SpotifyToPlex_CreatingPlaylist"]}: {playlistName}");
                    }

                    var tracks = await plex.GetSpotifyPlaylistTracksAsync(spotify, playlistId);
                    var oldCache = PlexService.GetMissingCacheSnapshot();
                    var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (oldCache.TryGetValue(playlistName, out var cached))
                    {
                        await plex.SendSseAsync(exportId,
                            $"📄 {L["SpotifyToPlex_Continuing"]}: {cached.Items.Count} cached entries");
                        foreach (var entry in cached.Items)
                        {
                            var parts = entry.Split('—');
                            if (parts.Length >= 2)
                            {
                                var artist = parts[0].Trim();
                                var title = parts.Last().Trim();
                                processed.Add($"{artist}—{title}");
                                missingList.Add((artist, title, parts.Length > 2 ? parts[1].Trim() : null));
                            }
                        }
                    }

                    const int batchSize = 1;
                    var matchedKeys = new List<string>();
                    int added = 0, failed = 0, missing = 0, total = tracks.Count;

                    foreach (var (title, artist) in tracks)
                    {
                        if (processed.Contains($"{artist}—{title}"))
                            continue;

                        var found = await plex.SearchTracksOnPlexAsync(baseUrl, plexToken,
                            new List<(string Title, string Artist)> { (title, artist) });
                        var match = found.FirstOrDefault();

                        if (!string.IsNullOrEmpty(match.RatingKey))
                        {
                            matchedKeys.Add(match.RatingKey);
                            added++;
                        }
                        else
                        {
                            missing++;
                            missingList.Add((artist, title, null));
                        }

                        if (matchedKeys.Count >= batchSize)
                        {
                            await plex.AddTracksToPlaylistAsync(
                                baseUrl, plexToken, plexPlaylistKey,
                                matchedKeys, defaultMachineId, exportId);
                            matchedKeys.Clear();
                            await plex.SendSseAsync(exportId, $"progress:{added}:{missing}:{total}");
                        }

                        PlexService.UpdateMissingCache(playlistName, missingList);
                    }

                    if (matchedKeys.Count > 0)
                    {
                        await plex.AddTracksToPlaylistAsync(
                            baseUrl, plexToken, plexPlaylistKey,
                            matchedKeys, defaultMachineId, exportId);
                    }

                    PlexService.UpdateMissingCache(playlistName, missingList);
                    PlexService.SaveMissingCacheToFile();
                    await plex.SendSseAsync(exportId, $"done:{added}:{missing}:{failed}:{total}");

                    await plex.SendSseAsync(exportId,
                        $"✅ {L["SpotifyToPlex_Done"]}: {playlistName} ({added} added, {missing} missing)");
                    await plex.SendSseAsync(exportId, $"progressPlaylists:{processedPlaylists}:{totalPlaylists}");
                }

                await plex.SendSseAsync(exportId, $"🎉 {L["SpotifyToPlex_AllPlaylistsExported"]}");
            }
            catch (Exception ex)
            {
                await plex.SendSseAsync(exportId, $"❌ ERROR {ex.Message}");
                PlexService.SaveMissingCacheToFile();
            }
            finally
            {
                try
                {
                    await writer.FlushAsync();
                }
                catch
                {
                }

                PlexService.UnregisterSseStream(exportId);
                Console.WriteLine("[ExportAllLive] Completed SSE stream.");
            }
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
        [IgnoreAntiforgeryToken] // AJAX endpoint → no CSRF needed
        public IActionResult SavePlexToken([FromBody] JsonElement json)
        {
            try
            {
                var token = json.GetProperty("token").GetString();
                if (string.IsNullOrWhiteSpace(token))
                    return BadRequest(_localizer["Plex_NoToken"].Value);

                HttpContext.Session.SetString("PlexAuthToken", token);
                Console.WriteLine($"[Plex] Token saved: {token[..8]}...");
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SavePlexToken] " + ex);
                return BadRequest(new { success = false, message = _localizer["Plex_LoginFailed"].Value });
            }
        }

        // ==============================================================
        // 🔸 Redirect helper (legacy)
        // ==============================================================
        [HttpGet("PlexActions")]
        public IActionResult PlexActions()
        {
            return RedirectToAction("SpotifyToPlex");
        }

        // ==============================================================
        // 🔸 Rename Plex playlist
        // ==============================================================
        [HttpPost("RenamePlexPlaylist")]
        public async Task<IActionResult> RenamePlexPlaylist(
            [FromServices] PlexService plex,
            [FromBody] JsonElement payload)
        {
            var token = HttpContext.Session.GetString("PlexAuthToken");
            var baseUrl = HttpContext.Session.GetString("PlexBaseUrl");

            if (string.IsNullOrEmpty(token))
                return Json(new { success = false, message = _localizer["Plex_NoToken"].Value });

            if (string.IsNullOrEmpty(baseUrl))
            {
                var (b, m) = await plex.DiscoverServerAsync(token);
                baseUrl = b;
                HttpContext.Session.SetString("PlexBaseUrl", baseUrl);
                HttpContext.Session.SetString("PlexMachineId", m);
            }

            if (!payload.TryGetProperty("ratingKey", out var rk) ||
                !payload.TryGetProperty("newName", out var nn))
                return Json(new { success = false, message = _localizer["Job_Error_Preparation"].Value });

            var ratingKey = rk.GetString();
            var newName = nn.GetString();
            if (string.IsNullOrWhiteSpace(ratingKey) || string.IsNullOrWhiteSpace(newName))
                return Json(new { success = false, message = _localizer["Job_Error_Preparation"].Value });

            var url = $"{baseUrl}/playlists/{ratingKey}?title={Uri.EscapeDataString(newName)}";
            using var req = new HttpRequestMessage(HttpMethod.Put, url);
            req.Headers.Add("X-Plex-Token", token);
            req.Headers.Add("Accept", "application/json");

            using var http = new HttpClient();
            var resp = await http.SendAsync(req);

            if (resp.IsSuccessStatusCode)
                return Json(new { success = true, message = _localizer["SpotifyToPlex_RenameSuccess"].Value });

            return Json(new
            {
                success = false,
                message = $"HTTP {(int)resp.StatusCode} – {resp.ReasonPhrase}"
            });
        }

        // ============================================================
        // 🔸 Simple text log endpoint for Live Export (last-run info)
        // ============================================================
        [HttpGet("Logs")]
        public IActionResult Logs()
        {
            // For now, return the missing-track cache info if available
            if (TempData.ContainsKey("MissingCsvPath"))
            {
                var path = TempData["MissingCsvPath"]?.ToString();
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    var txt = System.IO.File.ReadAllText(path);
                    return Content(txt, "text/plain; charset=utf-8");
                }
            }

            return Content(_localizer["Logs_None"].Value, "text/plain; charset=utf-8");
        }

        // ============================================================
        // 🔸 Get cache state for frontend (playlist count + timestamp)
        // ============================================================
        [HttpGet("GetMissingCacheState")]
        public IActionResult GetMissingCacheState([FromServices] PlexService plex)
        {
            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            if (string.IsNullOrEmpty(plexToken))
                return Unauthorized();

            var (_, machineId) = plex.DiscoverServerAsync(plexToken).GetAwaiter().GetResult();
            plex.LoadMissingCacheForServer(machineId);

            var cache = PlexService.GetMissingCacheSnapshot()
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        count = kvp.Value.Items.Count,
                        updated = kvp.Value.Created
                    });

            return new JsonResult(cache);
        }
    }
}

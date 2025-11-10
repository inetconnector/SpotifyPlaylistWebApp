using LukeHagar.PlexAPI.SDK;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using SpotifyAPI.Web;
using SpotifyPlaylistWebApp.Services;
using System.Text.Json;
using System.Xml;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

        [HttpGet("LiveExportStatus")]
        public IActionResult LiveExportStatus()
        {
            var sessionId = HttpContext.Session.Id;
            var snapshot = PlexService.GetLiveExportSnapshot(sessionId);

            return Json(new
            {
                snapshot.ExportId,
                snapshot.Status,
                snapshot.IsRunning,
                snapshot.Started,
                snapshot.Finished,
                snapshot.CurrentPlaylist,
                snapshot.TotalPlaylists,
                snapshot.CompletedPlaylists,
                snapshot.Added,
                snapshot.Missing,
                snapshot.Failed,
                snapshot.TotalTracks,
                snapshot.LastMessage,
                Log = snapshot.Log.Select(evt => new
                {
                    evt.Timestamp,
                    evt.Type,
                    evt.Message,
                    evt.Data
                }).ToList()
            });
        }

        [HttpPost("CancelLiveExport")]
        public async Task<IActionResult> CancelLiveExport([FromServices] PlexService plex)
        {
            var sessionId = HttpContext.Session.Id;

            if (PlexService.TryCancelLiveExport(sessionId, out var exportId) &&
                !string.IsNullOrEmpty(exportId))
            {
                await plex.PublishLiveEventAsync(sessionId, exportId, "stopping",
                    $"⏹ {_localizer["SpotifyToPlex_Cancelling"].Value}");

                return Json(new { success = true });
            }

            return Json(new
            {
                success = false,
                message = _localizer["SpotifyToPlex_NoLiveExport"].Value
            });
        }

        [HttpGet("LiveExportStream")]
        public async Task LiveExportStream()
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");

            await using var writer = new StreamWriter(Response.Body);

            var sessionId = HttpContext.Session.Id;
            var snapshot = PlexService.GetLiveExportSnapshot(sessionId);

            if (string.IsNullOrEmpty(snapshot.ExportId))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "status",
                    status = snapshot.Status,
                    message = _localizer["SpotifyToPlex_NoLiveExport"].Value,
                    data = new
                    {
                        snapshot.Status,
                        snapshot.IsRunning,
                        snapshot.TotalPlaylists,
                        snapshot.CompletedPlaylists,
                        snapshot.Added,
                        snapshot.Missing,
                        snapshot.Failed
                    }
                });

                await writer.WriteAsync($"data: {payload}\n\n");
                await writer.FlushAsync();
                return;
            }

            var exportId = snapshot.ExportId;
            var registrationId = PlexService.RegisterSseStream(exportId, writer);

            try
            {
                var statusPayload = JsonSerializer.Serialize(new
                {
                    type = "status",
                    status = snapshot.Status,
                    snapshot.IsRunning,
                    snapshot.CurrentPlaylist,
                    snapshot.TotalPlaylists,
                    snapshot.CompletedPlaylists,
                    snapshot.Added,
                    snapshot.Missing,
                    snapshot.Failed,
                    snapshot.TotalTracks,
                    snapshot.LastMessage,
                    snapshot.Started,
                    snapshot.Finished
                });

                await writer.WriteAsync($"data: {statusPayload}\n\n");
                await writer.FlushAsync();

                foreach (var evt in snapshot.Log)
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        evt.Type,
                        evt.Message,
                        evt.Data,
                        evt.Timestamp
                    });

                    await writer.WriteAsync($"data: {json}\n\n");
                    await writer.FlushAsync();
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), HttpContext.RequestAborted);
                }
                catch (TaskCanceledException)
                {
                }
            }
            finally
            {
                PlexService.UnregisterSseStream(exportId, registrationId);
            }
        }

        private const string SessionSpotifyTokenKey = "SpotifyAccessToken";
        private readonly IStringLocalizer<SharedResource> _localizer;

        public HomePlexController(IStringLocalizer<SharedResource> localizer)
        {
            _localizer = localizer;
        }

        private static string NormalizePlexPlaylistTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            var clean = title.Trim();
            clean = Regex.Replace(clean, @"_Spotify_\d{4}-\d{2}-\d{2}$", string.Empty,
                RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\s*\([^)]*\)\s*$", string.Empty);
            return clean.Trim();
        }

        private static string BuildTrackKey(string artist, string title)
        {
            return $"{(artist ?? string.Empty).Trim()}—{(title ?? string.Empty).Trim()}";
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
                ViewData["Title"] = _localizer["SpotifyToPlex_Title"].Value;

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
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");

            await using var writer = new StreamWriter(Response.Body);

            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            var spotifyToken = HttpContext.Session.GetString("SpotifyAccessToken");

            if (string.IsNullOrWhiteSpace(plexToken) || string.IsNullOrWhiteSpace(spotifyToken))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "error",
                    message = _localizer["SpotifyToPlex_TokenExpired"].Value
                });

                await writer.WriteAsync($"data: {payload}\n\n");
                await writer.FlushAsync();
                return;
            }

            var sessionId = HttpContext.Session.Id;

            if (!PlexService.TryBeginLiveExport(sessionId, "single", out var liveState,
                    out var activeExportId))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "error",
                    message = _localizer["SpotifyToPlex_ExportAlreadyRunning"].Value,
                    data = new { exportId = activeExportId }
                });

                await writer.WriteAsync($"data: {payload}\n\n");
                await writer.FlushAsync();
                return;
            }

            var exportId = liveState.ExportId;
            var registrationId = PlexService.RegisterSseStream(exportId, writer);
            var cancellation = liveState.Cancellation;
            string baseUrl = string.Empty;
            string defaultMachineId = string.Empty;

            var missingList = new List<(string Artist, string Title, string? Album)>();

            try
            {
                var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
                var tracks = await plex.GetSpotifyPlaylistTracksAsync(spotify, playlistId);

                PlexService.UpdateLiveExport(sessionId, state =>
                {
                    state.TotalPlaylists = 1;
                    state.CurrentPlaylist = playlistName;
                    state.TotalTracks = tracks.Count;
                });

                await plex.PublishLiveEventAsync(sessionId, exportId, "status",
                    string.Format(L["SpotifyToPlex_Exporting"].Value, playlistName),
                    new Dictionary<string, object?>
                    {
                        ["playlist"] = playlistName,
                        ["totalTracks"] = tracks.Count
                    });

                (baseUrl, defaultMachineId) = await plex.DiscoverServerAsync(plexToken);
                plex.LoadMissingCacheForServer(defaultMachineId);

                var existing = await plex.GetPlexPlaylistsAsync(baseUrl, plexToken);
                var normalizedName = NormalizePlexPlaylistTitle(playlistName);
                var old = existing.FirstOrDefault(p =>
                    NormalizePlexPlaylistTitle(p.Title)
                        .Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

                string plexPlaylistKey;

                if (!string.IsNullOrEmpty(old.RatingKey))
                {
                    plexPlaylistKey = old.RatingKey;
                    await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                        $"♻️ {L["SpotifyToPlex_Continuing"].Value}: {playlistName}");
                }
                else
                {
                    await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                        $"🪄 {L["SpotifyToPlex_CreatingPlaylist"].Value}: {playlistName}");
                    plexPlaylistKey = await plex.CreatePlaylistAsync(baseUrl, plexToken, playlistName);
                    if (string.IsNullOrEmpty(plexPlaylistKey))
                    {
                        await plex.PublishLiveEventAsync(sessionId, exportId, "error",
                            "❌ Could not create Plex playlist.");
                        PlexService.CompleteLiveExport(sessionId, "failed",
                            "Could not create Plex playlist");
                        return;
                    }
                }

                var existingTracks = await plex.GetPlaylistTracksAsync(baseUrl, plexToken, plexPlaylistKey);
                var existingTrackSet = new HashSet<string>(
                    existingTracks.Select(t => BuildTrackKey(t.Artist, t.Title)),
                    StringComparer.OrdinalIgnoreCase);

                var oldCache = PlexService.GetMissingCacheSnapshot();
                var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (oldCache.TryGetValue(playlistName, out var cached))
                {
                    await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                        $"📄 {L["SpotifyToPlex_Continuing"].Value}: {cached.Items.Count} cached entries");

                    foreach (var entry in cached.Items)
                    {
                        var parts = entry.Split('—');
                        if (parts.Length >= 2)
                        {
                            var artist = parts[0].Trim();
                            var title = parts.Last().Trim();
                            var key = BuildTrackKey(artist, title);
                            processed.Add(key);
                            missingList.Add((artist, title, parts.Length > 2 ? parts[1].Trim() : null));
                        }
                    }
                }

                const int batchSize = 1;
                int failed = 0, added = 0, missing = 0, total = tracks.Count;
                var matchedKeys = new List<string>();

                foreach (var (title, artist, album) in tracks)
                {
                    if (cancellation.IsCancellationRequested)
                        break;

                    var trackKey = BuildTrackKey(artist, title);
                    if (processed.Contains(trackKey))
                        continue;

                    if (existingTrackSet.Contains(trackKey))
                    {
                        processed.Add(trackKey);
                        await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                            $"ℹ️ {L["SpotifyToPlex_AlreadyInPlex"].Value}: {artist} — {title}");
                        continue;
                    }

                    await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                        $"🔍 {L["SpotifyToPlex_Searching"].Value}: {artist} — {title}");

                    var found = await plex.SearchTracksOnPlexAsync(baseUrl, plexToken,
                        new List<(string Title, string Artist)> { (title, artist) });
                    var match = found.FirstOrDefault();

                    if (!string.IsNullOrEmpty(match.RatingKey))
                    {
                        matchedKeys.Add(match.RatingKey);
                        existingTrackSet.Add(trackKey);
                        processed.Add(trackKey);
                        added++;
                        await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                            $"✅ {L["SpotifyToPlex_Found"].Value}: {artist} — {title}");
                    }
                    else
                    {
                        missing++;
                        processed.Add(trackKey);
                        missingList.Add((artist, title, album));
                        await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                            $"❌ {L["SpotifyToPlex_MissingTrack"].Value}: {artist} — {title}");
                    }

                    PlexService.UpdateLiveExport(sessionId, state =>
                    {
                        state.Added = added;
                        state.Missing = missing;
                        state.Failed = failed;
                        state.TotalTracks = total;
                    });

                    var progressData = new Dictionary<string, object?>
                    {
                        ["added"] = added,
                        ["missing"] = missing,
                        ["failed"] = failed,
                        ["total"] = total,
                        ["playlist"] = playlistName,
                        ["completedPlaylists"] = 0,
                        ["totalPlaylists"] = 1
                    };

                    await plex.PublishLiveEventAsync(sessionId, exportId, "progress", string.Empty, progressData);

                    if (matchedKeys.Count >= batchSize)
                    {
                        await plex.AddTracksToPlaylistAsync(
                            baseUrl, plexToken, plexPlaylistKey,
                            matchedKeys, defaultMachineId, exportId);

                        matchedKeys.Clear();
                    }

                    PlexService.UpdateMissingCache(playlistName, missingList);
                }

                if (!cancellation.IsCancellationRequested && matchedKeys.Count > 0)
                {
                    await plex.AddTracksToPlaylistAsync(
                        baseUrl, plexToken, plexPlaylistKey,
                        matchedKeys, defaultMachineId, exportId);
                }

                PlexService.UpdateMissingCache(playlistName, missingList);
                PlexService.SaveMissingCacheToFile();

                var summaryData = new Dictionary<string, object?>
                {
                    ["added"] = added,
                    ["missing"] = missing,
                    ["failed"] = failed,
                    ["total"] = total,
                    ["playlist"] = playlistName
                };

                if (cancellation.IsCancellationRequested)
                {
                    PlexService.UpdateLiveExport(sessionId, state =>
                    {
                        state.CompletedPlaylists = 0;
                        state.Added = added;
                        state.Missing = missing;
                        state.Failed = failed;
                    });
                    await plex.PublishLiveEventAsync(sessionId, exportId, "stopped",
                        $"⏹ {L["SpotifyToPlex_ExportStopped"].Value}", summaryData);
                    PlexService.CompleteLiveExport(sessionId, "cancelled",
                        L["SpotifyToPlex_ExportStopped"].Value);
                }
                else
                {
                    PlexService.UpdateLiveExport(sessionId, state =>
                    {
                        state.CompletedPlaylists = 1;
                        state.Added = added;
                        state.Missing = missing;
                        state.Failed = failed;
                        state.TotalPlaylists = 1;
                    });
                    await plex.PublishLiveEventAsync(sessionId, exportId, "summary",
                        $"✅ {L["SpotifyToPlex_Done"].Value}: {playlistName}", summaryData);
                    PlexService.CompleteLiveExport(sessionId, "completed",
                        $"{playlistName}: {added} added");
                }
            }
            catch (Exception ex)
            {
                await plex.PublishLiveEventAsync(sessionId, exportId, "error", $"❌ ERROR {ex.Message}");
                PlexService.CompleteLiveExport(sessionId, "failed", ex.Message);
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
                    Console.WriteLine("[ExportOneLive] SSE client disconnected before flush.");
                }

                if (!string.IsNullOrEmpty(defaultMachineId))
                    plex.SaveMissingCacheForServer(defaultMachineId);

                PlexService.UnregisterSseStream(exportId, registrationId);
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
        // 🔸 AJAX: Get tracks of a Spotify playlist
        // ==============================================================
        [HttpGet("GetPlaylistTracks")]
        public async Task<IActionResult> GetPlaylistTracks([FromServices] PlexService plex, string playlistId)
        {
            try
            {
                var spotifyToken = HttpContext.Session.GetString(SessionSpotifyTokenKey);

                if (string.IsNullOrWhiteSpace(spotifyToken))
                    return Json(new { success = false, message = _localizer["SpotifyToPlex_TokenExpired"].Value });

                if (string.IsNullOrWhiteSpace(playlistId))
                    return Json(new { success = false, message = _localizer["SpotifyToPlex_LoadError"].Value });

                var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
                var tracks = await plex.GetSpotifyPlaylistTracksAsync(spotify, playlistId);

                var result = tracks
                    .Select(t => new { title = t.Title, artist = t.Artist, album = t.Album })
                    .ToList();

                return Json(new { success = true, tracks = result });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[GetPlaylistTracks] " + ex);
                return Json(new
                {
                    success = false,
                    message = $"{_localizer["SpotifyToPlex_LoadError"].Value}: {ex.Message}"
                });
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
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");

            await using var writer = new StreamWriter(Response.Body);

            var plexToken = HttpContext.Session.GetString("PlexAuthToken");
            var spotifyToken = HttpContext.Session.GetString("SpotifyAccessToken");

            if (plexToken == null || spotifyToken == null)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "error",
                    message = _localizer["SpotifyToPlex_TokenExpired"].Value
                });

                await writer.WriteAsync($"data: {payload}\n\n");
                await writer.FlushAsync();
                return;
            }

            var sessionId = HttpContext.Session.Id;

            if (!PlexService.TryBeginLiveExport(sessionId, "all", out var liveState,
                    out var activeExportId))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "error",
                    message = _localizer["SpotifyToPlex_ExportAlreadyRunning"].Value,
                    data = new { exportId = activeExportId }
                });

                await writer.WriteAsync($"data: {payload}\n\n");
                await writer.FlushAsync();
                return;
            }

            var exportId = liveState.ExportId;
            var registrationId = PlexService.RegisterSseStream(exportId, writer);
            var cancellation = liveState.Cancellation;
            string baseUrl = string.Empty;
            string defaultMachineId = string.Empty;

            try
            {
                var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault(spotifyToken));
                (baseUrl, defaultMachineId) = await plex.DiscoverServerAsync(plexToken);
                plex.LoadMissingCacheForServer(defaultMachineId);

                var playlists = await plex.GetAllSpotifyPlaylistsAsync(spotify);
                var totalPlaylists = playlists.Count;
                int processedPlaylists = 0;
                int totalAddedAll = 0, totalMissingAll = 0, totalFailedAll = 0;

                PlexService.UpdateLiveExport(sessionId, state =>
                {
                    state.TotalPlaylists = totalPlaylists;
                    state.CompletedPlaylists = 0;
                    state.CurrentPlaylist = null;
                });

                await plex.PublishLiveEventAsync(sessionId, exportId, "status",
                    $"🎧 {L["SpotifyToPlex_Starting"].Value}: {totalPlaylists}",
                    new Dictionary<string, object?> { ["totalPlaylists"] = totalPlaylists });

                foreach (var item in playlists)
                {
                    if (cancellation.IsCancellationRequested)
                        break;

                    processedPlaylists++;
                    var playlistId = item.Value;
                    var playlistName = item.Text;
                    var missingList = new List<(string Artist, string Title, string? Album)>();

                    PlexService.UpdateLiveExport(sessionId, state =>
                    {
                        state.CurrentPlaylist = playlistName;
                        state.CompletedPlaylists = processedPlaylists - 1;
                    });

                    await plex.PublishLiveEventAsync(sessionId, exportId, "status",
                        $"🎵 {L["SpotifyToPlex_Exporting"].Value}: {playlistName}",
                        new Dictionary<string, object?>
                        {
                            ["playlist"] = playlistName,
                            ["completedPlaylists"] = processedPlaylists - 1,
                            ["totalPlaylists"] = totalPlaylists
                        });

                    var existing = await plex.GetPlexPlaylistsAsync(baseUrl, plexToken);
                    var normalizedName = NormalizePlexPlaylistTitle(playlistName);
                    var old = existing.FirstOrDefault(p =>
                        NormalizePlexPlaylistTitle(p.Title)
                            .Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

                    string plexPlaylistKey;
                    if (!string.IsNullOrEmpty(old.RatingKey))
                    {
                        plexPlaylistKey = old.RatingKey;
                        await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                            $"♻️ {L["SpotifyToPlex_Continuing"].Value}: {playlistName}");
                    }
                    else
                    {
                        await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                            $"🪄 {L["SpotifyToPlex_CreatingPlaylist"].Value}: {playlistName}");
                        plexPlaylistKey = await plex.CreatePlaylistAsync(baseUrl, plexToken, playlistName);
                        if (string.IsNullOrEmpty(plexPlaylistKey))
                        {
                            await plex.PublishLiveEventAsync(sessionId, exportId, "error",
                                "❌ Could not create Plex playlist.");
                            PlexService.CompleteLiveExport(sessionId, "failed",
                                "Could not create Plex playlist");
                            return;
                        }
                    }

                    var tracks = await plex.GetSpotifyPlaylistTracksAsync(spotify, playlistId);
                    var existingTracks = await plex.GetPlaylistTracksAsync(baseUrl, plexToken, plexPlaylistKey);
                    var existingTrackSet = new HashSet<string>(
                        existingTracks.Select(t => BuildTrackKey(t.Artist, t.Title)),
                        StringComparer.OrdinalIgnoreCase);

                    var oldCache = PlexService.GetMissingCacheSnapshot();
                    var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (oldCache.TryGetValue(playlistName, out var cached))
                    {
                        await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                            $"📄 {L["SpotifyToPlex_Continuing"].Value}: {cached.Items.Count} cached entries");
                        foreach (var entry in cached.Items)
                        {
                            var parts = entry.Split('—');
                            if (parts.Length >= 2)
                            {
                                var artist = parts[0].Trim();
                                var title = parts.Last().Trim();
                                var key = BuildTrackKey(artist, title);
                                processed.Add(key);
                                missingList.Add((artist, title, parts.Length > 2 ? parts[1].Trim() : null));
                            }
                        }
                    }

                    const int batchSize = 1;
                    var matchedKeys = new List<string>();
                    int added = 0, missing = 0, failed = 0, total = tracks.Count;

                    foreach (var (title, artist, album) in tracks)
                    {
                        if (cancellation.IsCancellationRequested)
                            break;

                        var trackKey = BuildTrackKey(artist, title);
                        if (processed.Contains(trackKey))
                            continue;

                        if (existingTrackSet.Contains(trackKey))
                        {
                            processed.Add(trackKey);
                            await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                                $"ℹ️ {L["SpotifyToPlex_AlreadyInPlex"].Value}: {artist} — {title}");
                            continue;
                        }

                        await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                            $"🔍 {L["SpotifyToPlex_Searching"].Value}: {artist} — {title}");

                        var found = await plex.SearchTracksOnPlexAsync(baseUrl, plexToken,
                            new List<(string Title, string Artist)> { (title, artist) });
                        var match = found.FirstOrDefault();

                        if (!string.IsNullOrEmpty(match.RatingKey))
                        {
                            matchedKeys.Add(match.RatingKey);
                            existingTrackSet.Add(trackKey);
                            processed.Add(trackKey);
                            added++;
                            await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                                $"✅ {L["SpotifyToPlex_Found"].Value}: {artist} — {title}");
                        }
                        else
                        {
                            missing++;
                            processed.Add(trackKey);
                            missingList.Add((artist, title, album));
                            await plex.PublishLiveEventAsync(sessionId, exportId, "log",
                                $"❌ {L["SpotifyToPlex_MissingTrack"].Value}: {artist} — {title}");
                        }

                        var progressData = new Dictionary<string, object?>
                        {
                            ["added"] = added,
                            ["missing"] = missing,
                            ["failed"] = failed,
                            ["total"] = total,
                            ["playlist"] = playlistName,
                            ["completedPlaylists"] = processedPlaylists - 1,
                            ["totalPlaylists"] = totalPlaylists
                        };

                        await plex.PublishLiveEventAsync(sessionId, exportId, "progress", string.Empty, progressData);

                        PlexService.UpdateLiveExport(sessionId, state =>
                        {
                            state.Added = totalAddedAll + added;
                            state.Missing = totalMissingAll + missing;
                            state.Failed = totalFailedAll + failed;
                            state.TotalTracks = total;
                        });

                        if (matchedKeys.Count >= batchSize)
                        {
                            await plex.AddTracksToPlaylistAsync(
                                baseUrl, plexToken, plexPlaylistKey,
                                matchedKeys, defaultMachineId, exportId);
                            matchedKeys.Clear();
                        }

                        PlexService.UpdateMissingCache(playlistName, missingList);
                    }

                    if (!cancellation.IsCancellationRequested && matchedKeys.Count > 0)
                    {
                        await plex.AddTracksToPlaylistAsync(
                            baseUrl, plexToken, plexPlaylistKey,
                            matchedKeys, defaultMachineId, exportId);
                    }

                    PlexService.UpdateMissingCache(playlistName, missingList);
                    PlexService.SaveMissingCacheToFile();

                    totalAddedAll += added;
                    totalMissingAll += missing;
                    totalFailedAll += failed;

                    var playlistSummary = new Dictionary<string, object?>
                    {
                        ["added"] = added,
                        ["missing"] = missing,
                        ["failed"] = failed,
                        ["total"] = total,
                        ["playlist"] = playlistName,
                        ["completedPlaylists"] = processedPlaylists,
                        ["totalPlaylists"] = totalPlaylists
                    };

                    if (cancellation.IsCancellationRequested)
                    {
                        await plex.PublishLiveEventAsync(sessionId, exportId, "stopped",
                            $"⏹ {L["SpotifyToPlex_ExportStopped"].Value}", playlistSummary);
                        PlexService.UpdateLiveExport(sessionId, state =>
                        {
                            state.CompletedPlaylists = processedPlaylists;
                            state.Added = totalAddedAll;
                            state.Missing = totalMissingAll;
                            state.Failed = totalFailedAll;
                        });
                        break;
                    }

                    await plex.PublishLiveEventAsync(sessionId, exportId, "summary",
                        $"✅ {L["SpotifyToPlex_Done"].Value}: {playlistName}", playlistSummary);

                    PlexService.UpdateLiveExport(sessionId, state =>
                    {
                        state.CompletedPlaylists = processedPlaylists;
                        state.Added = totalAddedAll;
                        state.Missing = totalMissingAll;
                        state.Failed = totalFailedAll;
                        state.TotalTracks = total;
                    });

                    await plex.PublishLiveEventAsync(sessionId, exportId, "playlist-progress", string.Empty,
                        new Dictionary<string, object?>
                        {
                            ["completedPlaylists"] = processedPlaylists,
                            ["totalPlaylists"] = totalPlaylists
                        });
                }

                if (cancellation.IsCancellationRequested)
                {
                    PlexService.CompleteLiveExport(sessionId, "cancelled",
                        L["SpotifyToPlex_ExportStopped"].Value);
                }
                else
                {
                    await plex.PublishLiveEventAsync(sessionId, exportId, "all-complete",
                        $"🎉 {L["SpotifyToPlex_AllPlaylistsExported"].Value}",
                        new Dictionary<string, object?>
                        {
                            ["processed"] = processedPlaylists,
                            ["total"] = totalPlaylists,
                            ["added"] = totalAddedAll,
                            ["missing"] = totalMissingAll,
                            ["failed"] = totalFailedAll
                        });
                    PlexService.CompleteLiveExport(sessionId, "completed",
                        L["SpotifyToPlex_AllPlaylistsExported"].Value);
                }
            }
            catch (Exception ex)
            {
                await plex.PublishLiveEventAsync(sessionId, exportId, "error", $"❌ ERROR {ex.Message}");
                PlexService.CompleteLiveExport(sessionId, "failed", ex.Message);
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
                    Console.WriteLine("[ExportAllLive] Completed SSE stream.");
                }

                if (!string.IsNullOrEmpty(defaultMachineId))
                    plex.SaveMissingCacheForServer(defaultMachineId);

                PlexService.UnregisterSseStream(exportId, registrationId);
                PlexService.SaveMissingCacheToFile();
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

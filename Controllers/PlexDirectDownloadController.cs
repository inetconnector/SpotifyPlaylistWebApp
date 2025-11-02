using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SpotifyPlaylistWebApp.Services;
using System.IO.Compression;

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
                    trackNumber = x.TrackNumber,
                    filename = x.FileName,
                    url = x.PlexUrl,
                    sizeBytes = x.SizeBytes
                })
            });
        }

        [HttpGet("Zip")]
        public async Task<IActionResult> Zip(
           [FromServices] PlexDirectDownloadService direct,
           [FromServices] PlexService plex,
           [FromServices] IHttpClientFactory httpFactory,
           [FromQuery] string playlistKey,
           [FromQuery] string? playlistName,
           CancellationToken ct = default)
        {
            var token = HttpContext.Session.GetString("PlexAuthToken");
            var baseUrl = HttpContext.Session.GetString("PlexBaseUrl");

            if (string.IsNullOrEmpty(token))
                return Unauthorized(new { message = "Not logged in to Plex." });

            if (string.IsNullOrWhiteSpace(baseUrl))
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
            if (items.Count == 0)
                return BadRequest(new { message = "No items in playlist." });

            var zipName = Sanitize((playlistName?.Trim().Length > 0 ? playlistName!.Trim() : "playlist") + ".zip");

            var stream = new System.IO.Pipelines.Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    var http = httpFactory.CreateClient();
                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    await using var writer = stream.Writer.AsStream();
                    using var archive = new ZipArchive(writer, ZipArchiveMode.Create, leaveOpen: true);

                    foreach (var it in items)
                    {
                        ct.ThrowIfCancellationRequested();

                        var entryName = EnsureUniqueName(usedNames, it.FileName);
                        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);

                        try
                        {
                            using var entryStream = entry.Open();

                            using var req = new HttpRequestMessage(HttpMethod.Get, it.PlexUrl);
                            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                            res.EnsureSuccessStatusCode();

                            await using var remote = await res.Content.ReadAsStreamAsync(ct);
                            await remote.CopyToAsync(entryStream, 1024 * 1024, ct);
                        }
                        catch (Exception ex)
                        {
                            var errEntry = archive.CreateEntry(entryName + "._ERROR.txt", CompressionLevel.NoCompression);
                            await using var es = errEntry.Open();
                            var msg = System.Text.Encoding.UTF8.GetBytes($"Failed to download {entryName}\n{ex}");
                            await es.WriteAsync(msg, ct);
                        }
                    }
                }
                catch { }
                finally
                {
                    await stream.Writer.CompleteAsync();
                }
            });

            return File(stream.Reader.AsStream(), "application/zip", zipName);

            static string EnsureUniqueName(HashSet<string> used, string name)
            {
                if (!used.Contains(name))
                {
                    used.Add(name);
                    return name;
                }

                var ext = Path.GetExtension(name);
                var baseName = Path.GetFileNameWithoutExtension(name);
                int i = 2;
                string candidate;
                do
                {
                    candidate = $"{baseName} ({i}){ext}";
                    i++;
                } while (!used.Add(candidate));

                return candidate;
            }

            static string Sanitize(string name)
            {
                foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                return name.Trim();
            }
        }


        [HttpGet("DirectDownload")]
        public IActionResult DirectDownload(
            [FromQuery] string playlistKey = "",
            [FromQuery] string playlistName = "",
            [FromServices] IStringLocalizer<SharedResource> localizer = null)
        {
            if (localizer != null)
            {
                ViewData["Title"] = localizer["DirectDownload_PageTitle"].Value;
            }

            ViewBag.PlaylistKey = playlistKey ?? "";
            ViewBag.PlaylistName = playlistName ?? "";

            return View("~/Views/HomePlex/DirectDownload.cshtml");
        }
    }
}

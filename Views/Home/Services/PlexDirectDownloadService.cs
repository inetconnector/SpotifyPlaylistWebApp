using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SpotifyPlaylistWebApp.Services
{
    public sealed record PlexDirectItem(
        string Title,
        string? Artist,
        string? Album,
        int? TrackNumber,
        string FileName,
        string PlexUrl,
        long? SizeBytes
    );

    /// <summary>
    /// Builds direct Plex download URLs for playlist items (no transcoding).
    /// Requires: baseUrl + Plex token (from session).
    /// </summary>
    public sealed class PlexDirectDownloadService
    {
        private readonly IHttpClientFactory _httpFactory;

        public PlexDirectDownloadService(IHttpClientFactory httpFactory)
        {
            _httpFactory = httpFactory;
        }

        public async Task<IReadOnlyList<PlexDirectItem>> BuildDirectListAsync(
            string baseUrl, string plexToken, string playlistKey, CancellationToken ct = default)
        {
            var http = _httpFactory.CreateClient();
            var url = $"{baseUrl.TrimEnd('/')}/playlists/{playlistKey}/items?X-Plex-Token={plexToken}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            var payload = await res.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(payload);
            var mc = doc.RootElement.GetProperty("MediaContainer");
            if (!mc.TryGetProperty("Metadata", out var md)) return Array.Empty<PlexDirectItem>();

            var list = new List<PlexDirectItem>();
            foreach (var t in md.EnumerateArray())
            {
                string title  = t.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                string? artist= t.TryGetProperty("grandparentTitle", out var ar) ? ar.GetString() : null;
                string? album = t.TryGetProperty("parentTitle", out var al) ? al.GetString() : null;
                int? trackNumber = t.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var trackIdx) ? trackIdx : (int?)null;

                string? file = null; string? partId = null; long? size = null;
                try
                {
                    var part = t.GetProperty("Media")[0].GetProperty("Part")[0];
                    file  = part.TryGetProperty("file", out var f) ? f.GetString() : null;
                    partId= part.TryGetProperty("id", out var id) ? id.GetRawText().Trim('"') : null;
                    size  = part.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s64) ? s64 : (long?)null;
                }
                catch { /* ignore */ }

                if (string.IsNullOrWhiteSpace(partId))
                    continue; // cannot build safe direct download without partId

                var ext = SafeExt(file) ?? ".bin";
                var baseName = BuildBaseFileName(title, artist, album, trackNumber);
                var fileName = Sanitize(string.IsNullOrWhiteSpace(baseName) ? "track" : baseName) + ext;

                var plexUrl = $"{baseUrl.TrimEnd('/')}/library/parts/{partId}/file{ext}?download=1&X-Plex-Token={plexToken}";
                list.Add(new PlexDirectItem(title, artist, album, trackNumber, fileName, plexUrl, size));
            }
            return list;
        }

        private static string BuildBaseFileName(string title, string? artist, string? album, int? trackNumber)
        {
            var parts = new List<string>();

            if (trackNumber is { } tn && tn > 0)
                parts.Add(tn.ToString("D2"));

            if (!string.IsNullOrWhiteSpace(artist))
                parts.Add(artist!.Trim());

            if (!string.IsNullOrWhiteSpace(album))
                parts.Add(album!.Trim());

            if (!string.IsNullOrWhiteSpace(title))
                parts.Add(title.Trim());

            var baseName = string.Join(" - ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            return baseName;
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        private static string? SafeExt(string? path)
        {
            try
            {
                var ext = Path.GetExtension(path ?? "");
                return string.IsNullOrWhiteSpace(ext) ? null : ext;
            }
            catch { return null; }
        }
    }
}
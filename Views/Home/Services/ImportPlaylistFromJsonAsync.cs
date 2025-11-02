using System.Text.Json;
using LukeHagar.PlexAPI.SDK;
using LukeHagar.PlexAPI.SDK.Models.Components;

namespace SpotifyPlaylistWebApp.Services
{
    public class ImportPlaylistFromJsonAsync
    {
        public async Task<bool> ImportPlaylistFromJsonAsync(string baseUrl, string token, string libraryName, string jsonFile)
        {
            try
            {
                var sdk = new PlexAPI(accessToken: token);
                Console.WriteLine($"[Plex Import] Connecting to Plex at {baseUrl}...");

                // 🔹 Load existing playlists
                var playlistsRes = await sdk.Playlists.GetPlaylistsAsync();
                var existingNames = playlistsRes?.MediaContainer?.Metadata?.Select(p => p.Title)?.ToList() ?? new();

                // 🔹 Load JSON file (same format as Python version)
                Console.WriteLine($"[Plex Import] Reading {jsonFile}...");
                var json = await File.ReadAllTextAsync(jsonFile);
                var playlists = JsonSerializer.Deserialize<List<PlexPlaylistJson>>(json);

                if (playlists == null || playlists.Count == 0)
                {
                    Console.WriteLine("[Plex Import] No playlists found in JSON.");
                    return false;
                }

                // 🔹 Iterate through playlists
                foreach (var p in playlists)
                {
                    if (existingNames.Contains(p.Title))
                    {
                        Console.WriteLine($"[Plex Import] Skipping existing: {p.Title}");
                        continue;
                    }

                    Console.WriteLine($"[Plex Import] Creating playlist: {p.Title}");
                    var trackUris = new List<string>();

                    foreach (var t in p.Tracks.Where(t => !t.Removed))
                    {
                        Console.WriteLine($"   🔍 Searching: {t.Artist} — {t.Title}");
                        // Hier würdest du deinen bestehenden PlexService.SearchTracksOnPlexAsync() aufrufen
                        // Beispiel:
                        var found = await SearchTracksOnPlexAsync(baseUrl, token, new[] { (t.Title, t.Artist) });
                        var match = found.FirstOrDefault();

                        if (!string.IsNullOrEmpty(match.RatingKey))
                        {
                            trackUris.Add($"server://{match.MachineId}/com.plexapp.plugins.library/library/metadata/{match.RatingKey}");
                        }
                        else
                        {
                            Console.WriteLine($"      ⚠️ Not found: {t.Title} — {t.Artist}");
                        }
                    }

                    // 🔹 Create playlist in Plex
                    // 🔹 Create playlist in Plex
                    if (trackUris.Count > 0)
                    {
                        Console.WriteLine($"   ➕ Creating with {trackUris.Count} tracks...");
                        var uri = string.Join(",", trackUris);

                        var res = await sdk.Playlists.CreatePlaylistAsync(
                            p.Title,               
                            uri,                 
                            "audio"    
                        );

                        if (res.RawResponse != null && res.RawResponse.IsSuccessStatusCode)
                            Console.WriteLine($"   ✅ Playlist '{p.Title}' created.");
                        else
                            Console.WriteLine($"   ❌ Failed to create '{p.Title}'.");
                    }

                    else
                    {
                        Console.WriteLine($"   ⚠️ No valid tracks found for '{p.Title}'");
                    }
                }

                Console.WriteLine("[Plex Import] Finished importing all playlists.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Plex Import] ⚠️ Error: {ex.Message}");
                return false;
            }
        }

        // Helper classes for JSON format
        public class PlexPlaylistJson
        {
            public string Title { get; set; } = "";
            public List<PlexTrackJson> Tracks { get; set; } = new();
        }

        public class PlexTrackJson
        {
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public bool Removed { get; set; }
        }
    }
}

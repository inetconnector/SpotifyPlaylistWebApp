# 🎧 Playlist Web App for Spotify® + Plex Integration — README (v3.5)

## 🌐 Live Demo
👉 **https://playlists.inetconnector.com**

> ⚠️ **Access note:**  
> Due to Spotify API policy changes, this demo is available **only for invited testers**.  
> If you would like to try it, please send an email to  
> **📧 info@inetconnector.com**  
> with your Spotify account email address.  
>  
> Your Spotify email will be added to the test app’s allow-list, and you’ll then be able to log in and create playlists.

---

## 🧩 Overview

This **ASP.NET Core 8 MVC web application** connects to the **Spotify Web API** to automatically create, clone, and organize playlists based on your listening history, liked songs, and personalized recommendations.  
It also integrates optional **Plex® export functionality**, allowing you to sync selected or all playlists directly to a Plex Media Server library.

All authentication is handled through **OAuth 2.0 PKCE**, ensuring full security without storing secrets server-side.

---

## 🚀 Dashboard Features

| Icon | Action | Description |
|------|---------|-------------|
| 📜 | **Clone Liked Songs** | Copies all your liked Spotify songs into a new playlist. |
| 🎶 | **Alternative Favorites** | Creates playlists with alternative tracks by the same artists/albums — removes duplicates. |
| ✨ | **Recommendations** | Builds a discovery playlist from Spotify’s recommendation engine. |
| 🔀 | **Shuffle Liked Songs** | Creates a randomized “Liked Mix”. |
| 📈 | **Most-Listened Songs** | Lists your most-played tracks (long-term). |
| 🎬 | **Export to Plex (Live)** | Syncs generated playlists to a Plex library using the Plex API (PIN-authenticated). |
| 🌐 | **Export All to Plex (Live)** | Exports **all** your Spotify playlists to Plex with batch processing and live progress. |

All labels, buttons, and tooltips are fully localized (🇬🇧 English, 🇩🇪 German, 🇪🇸 Spanish).

---

## 🧠 Technical Overview

| Component | Technology |
|------------|-------------|
| **Framework** | ASP.NET Core MVC (.NET 8) |
| **Language** | C# |
| **Frontend** | Razor Views + TailwindCSS (CDN) |
| **Backend APIs** | Spotify Web API (`SpotifyAPI.Web`), Plex API (custom client) |
| **Auth Flow** | OAuth 2.0 PKCE |
| **Localization** | `.resx` via `IStringLocalizer` |
| **Storage** | In-memory token/session cache |
| **Logging** | Console + internal middleware |
| **Deployment** | Linux/Windows (Kestrel, Nginx, IIS) |

---

## 🗂️ File Structure (Actual v3.5)

```
Controllers/ErrorController.cs
Controllers/HomeController.cs
Controllers/HomePlexController.cs
Controllers/LanguageController.cs
Resources/SharedResource.de.resx
Resources/SharedResource.en.resx
Resources/SharedResource.es.resx
Services/IPlexTokenStore.cs
Services/MemoryPlexTokenStore.cs
Services/PlexService.cs
Services/PlexTokenStore.cs
Views/Home/Dashboard.cshtml
Views/Home/Datenschutz.cshtml
Views/Home/Impressum.cshtml
Views/Home/Index.cshtml
Views/HomePlex/ExportResult.cshtml
Views/HomePlex/PlexActions.cshtml
Views/Shared/Error.cshtml
Views/Shared/LanguageSwitcher.cshtml
Views/Shared/Layout.cshtml
Views/ViewImports.cshtml
wwwroot/images/plex-spotify.svg
wwwroot/js/plex-login.js
wwwroot/css/site.css
wwwroot/favicon.ico
wwwroot/robots.txt
wwwroot/sitemap.xml
```

---

## 🧭 Data Flow

1. User authenticates with Spotify (OAuth 2 PKCE).  
2. Access token is stored temporarily in session.  
3. User triggers playlist creation or export.  
4. The app calls Spotify Web API asynchronously.  
5. Playlist is created in the user’s Spotify library.  
6. (Optional) Playlist(s) are exported to Plex via Plex API using a temporary PIN-based token.  
7. Progress and status are streamed via Server-Sent Events (SSE).

---

## 🛠️ Spotify + Plex Developer Setup

### 1️⃣ Create Spotify App
1. Go to https://developer.spotify.com/dashboard → **Create App**  
2. Set Redirect URIs:  
   ```
   https://localhost:5001/callback/
   https://yourdomain.com/callback/
   ```
3. Save and copy your **Client ID**.

### 2️⃣ Plex App Setup (for export feature)
The Plex integration uses a temporary PIN system (no permanent secret).  
No configuration needed, but ensure outbound access to:  
`https://plex.tv/api/v2/pins` and `https://plex.tv/api/v2/token`.

---

## 💻 Run Locally

```bash
dotnet restore
dotnet run
```
Then open [https://localhost:5001](https://localhost:5001)

You must be registered as a Spotify tester for your Client ID.

---

## 🌍 Localization

| Language | Resource File |
|-----------|----------------|
| English | `Resources/SharedResource.en.resx` |
| German | `Resources/SharedResource.de.resx` |
| Spanish | `Resources/SharedResource.es.resx` |

New localization keys for ExportAllLive:
- `SpotifyToPlex_LiveExportAll`
- `SpotifyToPlex_AllPlaylistsExported`
- `SpotifyToPlex_PlaylistProgress`
- `SpotifyToPlex_AllDone`

---

## 🎵 Playlist Logic Overview

| Method | Description |
|---------|-------------|
| `GeneratePlaylistAsync()` | Handles playlist creation from liked or top tracks. |
| `GenerateAlternativeFavoritesAsync()` | Finds similar songs and filters duplicates. |
| `GenerateRecommendationsAsync()` | Uses Spotify’s seed-based recommendation system. |
| `ExportOneLive()` | Exports one Spotify playlist to Plex with live SSE progress. |
| `ExportAllLive()` | Exports all Spotify playlists sequentially with live progress and batching. |

---

## 🧩 Additional Features

- Automatic **market detection** from UI culture  
- **Batch uploads to Plex** (up to 50 tracks/request)  
- **Live progress streaming** via Server-Sent Events (SSE)  
- **Plex export cache** to avoid duplicates  
- **Localized language switcher** (`LanguageController`)  
- **Responsive TailwindCSS layout**  
- **No persistent user data** — fully GDPR compliant  

---

## 🧾 Recent Updates (v3.5 – Oct 2025)

- Added **ExportAllLive**: multi-playlist live export with SSE feedback  
- Added **Batch export** (50-track chunk uploads to Plex)  
- Optimized **AddTracksToPlaylistAsync** for high-speed transfers  
- Added new `.resx` localization keys for live multi-export  
- Cleaned up old `ExportOne` and `ExportAll` legacy endpoints  
- Improved overall performance and error resilience  

---

## 📜 License

© 2025 **InetConnector / Daniel Frede**  
Licensed for personal and educational use.  
Commercial redistribution requires prior written permission.

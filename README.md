# 🎧 Spotify Playlist Web App – README

## Overview
This **ASP.NET Core web application** connects to the **Spotify Web API** to automatically create, clone, and organize playlists based on your listening history, liked songs, and personalized recommendations.

It uses **OAuth2 PKCE authentication** for secure login and interacts with Spotify’s API to access user data such as top tracks, saved songs, and generated recommendations — all without storing any sensitive credentials server-side.

---

## 🚀 Main Features (Dashboard Buttons)

| Icon | Action | Description |
|------|---------|-------------|
| 📜 | **Clone Liked Songs** | Creates a playlist that clones all your liked (saved) Spotify songs. |
| 🎶 | **Create Alternative Favorites** | Builds a playlist of *alternative songs* by the same artists or albums as your liked tracks — with strict duplicate filtering. |
| ✨ | **Create Recommendations** | Generates a fresh playlist using Spotify’s **Recommendations API**, seeded from your top songs and automatically adjusted to your current language/market. |
| 🔀 | **Shuffle Liked Songs** | Produces a randomized “Liked Mix” of your saved tracks. |
| 📈 | **Most-Listened Songs** | Creates a playlist of your top-played tracks (long-term history). |

All button texts, tooltips, and messages are fully localized via  
`SharedResource.en.resx` and `SharedResource.de.resx`.

---

## 🧠 Technical Overview

- **Framework:** ASP.NET Core MVC (.NET 8)  
- **Language:** C#  
- **API:** Spotify Web API (via `SpotifyAPI.Web`)  
- **Authentication:** OAuth2 PKCE Flow  
- **Frontend:** Razor Pages + TailwindCSS (via CDN)  
- **Localization:** .NET IStringLocalizer with culture cookies  
- **Session:** Stores access token (in-memory or distributed session)  

---

## 🗂️ File Structure

```
Controllers/
  HomeController.cs          # All Spotify playlist generation logic
Views/
  Home/Dashboard.cshtml      # Dashboard with main feature buttons
  Shared/LanguageSwitcher.cshtml
Resources/
  SharedResource.en.resx     # English UI texts
  SharedResource.de.resx     # German UI texts
wwwroot/
  css/, js/, images/         # Static assets (QR code, styling)
```

---

## 🔄 Data Flow

1. User clicks **Login with Spotify**.  
2. OAuth2 PKCE handshake completes → access token saved in session.  
3. Dashboard displays all available playlist actions.  
4. User clicks a feature (e.g., “Create Recommendations”).  
5. The app calls the Spotify Web API asynchronously in the background.  
6. A new private playlist is created in the user’s Spotify account.

---

## 🎵 Intelligent Playlist Generation

### 1️⃣ `GeneratePlaylistAsync()`
- Fetches **top tracks** or **liked tracks** (depending on action).  
- Handles pagination and rate limits automatically.  
- Creates playlists with batching (max 100 tracks per call).  

### 2️⃣ `GenerateAlternativeFavoritesAsync()`
- For each liked track, tries to find:
  - Another song from the same **album**, or  
  - A **top track by the same artist** (in the user’s market).  
- Ensures **no duplicate URIs** in the output playlist.  

### 3️⃣ `GenerateRecommendationsAsync()`
- Uses up to **5 random seeds** from the user’s top tracks.  
- Derives the **Spotify market automatically** from browser language (e.g., `de-DE → DE`, `en-US → US`).  
- Calls Spotify’s **Recommendations endpoint** to build a fresh discovery playlist.

---

## 🎧 Filtering and Cleanup

The app automatically excludes **audiobooks** and **podcasts** to prevent unwanted content:
```csharp
if (type.Contains("audiobook") || type.Contains("podcast"))
    return false;
```

---

## 🧩 Additional Features

- **Cooldown System:** Prevents API abuse by enforcing a short delay (4 minutes) between consecutive playlist generations per user.  
- **Automatic Localization:** UI switches between English and German via culture cookie.  
- **Donation Prompt:** After several uses, a subtle donation popup encourages voluntary support.  
- **Session Management:** Spotify tokens expire gracefully — if invalid, user is redirected to login.

---

## 🛠️ Configuration

### `appsettings.json` (local development)
```json
{
  "Spotify": {
    "ClientId": "your_spotify_client_id",
    "RedirectUri": "https://localhost:5001/callback/"
  }
}
```

### Environment Variables (for deployment)
```bash
export SPOTIFY_CLIENT_ID="your_client_id"
export SPOTIFY_REDIRECT_URI="https://playlists.inetconnector.com/callback/"
```

Ensure that the **redirect URI** is also registered in your Spotify Developer Dashboard.

---

## 💻 Running Locally

```bash
dotnet restore
dotnet run
```

Then open your browser at:  
👉 **https://localhost:5001**

---

## 🧾 Recent Updates

### v3.0 – October 2025
- Added **automatic market detection** (`CultureInfo → Spotify Market`)  
- Fixed SpotifyAPI.Web property changes for `RecommendationsRequest`  
- Added strict duplicate filtering in `AlternativeFavorites`  
- Enhanced error reporting for API failures (`APIException` logging)  
- Consolidated all actions under unified **HomeController.cs**  
- Improved Tailwind dashboard with clean tooltips and animation effects  

---

## 🧠 Future Enhancements
- “Smart Clone” that merges liked songs + recommendations  
- Auto-refresh tokens for long-lived sessions  
- UI dark/light theme toggle  
- Optional inclusion of public playlists in seed generation  

---

## 📜 License
© 2025 **InetConnector / Daniel Frede**  
Licensed for personal and educational use.  
Commercial redistribution requires prior written permission.

---

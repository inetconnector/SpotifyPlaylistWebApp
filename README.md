# Spotify Playlist Web App – Consolidated README

# Spotify Playlist Web App

## Overview
This ASP.NET Core web application connects to the **Spotify Web API** to automatically generate, clone, and organize playlists based on a user's listening behavior and saved tracks.

The app uses OAuth2 PKCE authentication and communicates with Spotify's API to access private data such as top tracks, saved tracks, and recommendations.

---

## Main Features (Dashboard Buttons)

| Icon | Action | Description |
|------|---------|-------------|
| 📜 | **Clone Liked Songs** | Creates a playlist containing all your saved Spotify songs. |
| 🎶 | **Create Alternative Favorites** | Generates a new playlist with alternative tracks from the same artists, avoiding duplicates. |
| ✨ | **Create Recommendations** | Uses Spotify recommendations to suggest new songs based on your profile. |
| 🔀 | **Shuffle Liked Songs** | Builds a randomized mix of your liked songs. |
| 📈 | **Most-Listened Songs** | Lists your most frequently played tracks. |

All text and tooltips are localized through `SharedResource.de.resx` and `SharedResource.en.resx`.

---

## Technical Details

- **Framework:** ASP.NET Core MVC (C#)  
- **API:** Spotify Web API via `SpotifyAPI.Web` NuGet package  
- **Authentication:** OAuth2 PKCE Flow  
- **Localization:** Built-in .NET localization middleware with culture cookies  
- **Frontend:** Razor Views with Tailwind-inspired utility classes  

### File Structure (Key Components)
```
Controllers/
  HomeController.cs          # Contains all playlist generation actions
Views/
  Home/Dashboard.cshtml      # Displays all dashboard buttons
  Shared/LanguageSwitcher.cshtml
Resources/
  SharedResource.de.resx     # German localization
  SharedResource.en.resx     # English localization
wwwroot/
  css/, js/, icons/          # Static assets
```

### Data Flow
1. User logs in with Spotify (OAuth2 PKCE).  
2. Access token stored in session.  
3. User triggers an action via a dashboard button.  
4. Controller calls Spotify Web API.  
5. A new playlist is created in the user's account.  

---

## Audiobook & Podcast Filter

A helper function `IsPlayableMusicTrack()` was added to exclude **audiobooks** and **podcasts** from all playlists:
```csharp
if (type.Contains("audiobook") || type.Contains("podcast"))
    return false;
```
This prevents unwanted tracks (e.g., audiobook chapters) from appearing in generated playlists.

---

## Recent Changes (Clean Buttons Update)

### v2.5 – October 2025
- Consolidated to five intuitive dashboard actions  
- Added multilingual button texts (English + German)  
- Added `CreateAlternativeFavorites()` action  
- Integrated audiobook/podcast filter in all playlist generators  
- Cleaned up tooltips, icons, and button order  

---

## Requirements

- .NET 8.0 SDK or newer  
- Spotify Developer Account  
- Client ID configured in `appsettings.json` or environment variables  
- Redirect URI configured in Spotify Dashboard

Run locally via:
```bash
dotnet run
```

---

## License
© 2025 InetConnector / Daniel Frede.  
Licensed for personal and educational use. Redistribution requires permission.

---

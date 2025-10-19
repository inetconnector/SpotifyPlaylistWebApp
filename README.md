# 🎧 Spotify Playlist Web App – README (v3.3)

## 🌐 Live Demo
👉 **[https://playlists.inetconnector.com](https://playlists.inetconnector.com)**  

> ⚠️ **Access note:**  
> Due to Spotify API policy changes, this demo is available **only for invited testers**.  
> If you would like to try it, please send an email to  
> **📧 info@inetconnector.com**  
> with your Spotify account email address.  
>  
> Your Spotify email will be added to the test app’s allow-list, and you’ll then be able to log in and create playlists.

---

## 🧩 Overview
This **ASP.NET Core web application** connects to the **Spotify Web API** to automatically create, clone, and organize playlists based on your listening history, liked songs, and personalized recommendations.  

The app uses **OAuth 2.0 PKCE authentication** for secure, client-side authorization — no secrets or credentials are stored on the server.

---

## 🚀 Dashboard Features

| Icon | Action | Description |
|------|---------|-------------|
| 📜 | **Clone Liked Songs** | Copies all your liked Spotify songs into a new playlist. |
| 🎶 | **Alternative Favorites** | Creates a playlist with alternative tracks by the same artists or albums (no duplicates). |
| ✨ | **Recommendations** | Builds a discovery playlist from Spotify’s recommendation engine, seeded from your top tracks. |
| 🔀 | **Shuffle Liked Songs** | Creates a randomized “Liked Mix”. |
| 📈 | **Most-Listened Songs** | Lists your most-played tracks (long-term history). |

All button texts and tooltips are localized through  
`SharedResource.en.resx` and `SharedResource.de.resx`.

---

## 🧠 Technical Overview

- **Framework:** ASP.NET Core MVC (.NET 8)  
- **Language:** C#  
- **API:** [Spotify Web API](https://developer.spotify.com/documentation/web-api) via `SpotifyAPI.Web`  
- **Authentication:** OAuth 2.0 PKCE Flow  
- **Frontend:** Razor Views + TailwindCSS (CDN)  
- **Localization:** IStringLocalizer (.resx files)  
- **Session:** In-memory access-token storage  

---

## 🗂️ File Structure

```
Controllers/
  HomeController.cs
Views/
  Home/Dashboard.cshtml
  Shared/LanguageSwitcher.cshtml
Resources/
  SharedResource.en.resx
  SharedResource.de.resx
wwwroot/
  css/, js/, images/
```

---

## 🧭 Data Flow

1. User logs in with Spotify (OAuth 2 PKCE).  
2. The authorization code is exchanged for an access token.  
3. Token is stored in session memory.  
4. User triggers playlist creation.  
5. Spotify Web API is called asynchronously.  
6. The resulting playlist appears in the user’s Spotify library.

---

## 🛠️ Spotify Developer Setup (for your own deployment)

If you want to run your own instance of this app (e.g. locally or on your domain),  
you need to create a **Spotify Developer App** with **your own Redirect URI**.

### 1️⃣ Create your app
1. Go to [https://developer.spotify.com/dashboard](https://developer.spotify.com/dashboard)  
2. Log in with your Spotify account.  
3. Click **“Create App”** → enter:
   - **App name:** `SpotifyPlaylistWebApp`
   - **Description:** `Personal playlist generator`
   - **Redirect URI(s):**
     ```
     https://localhost:5001/callback/
     https://yourdomain.com/callback/
     ```
     *(replace `yourdomain.com` with your own site)*  
4. Click **Save**

### 2️⃣ Get your Client ID
After saving, copy the **Client ID** shown in the app dashboard.  
You don’t need a Client Secret (PKCE flow does not require it).

### 3️⃣ Register your Redirect URI
In the same Spotify Developer page → **Edit Settings → Redirect URIs**  
Add the URI that matches your environment:

| Environment | Example Redirect URI |
|--------------|----------------------|
| Local development | `https://localhost:5001/callback/` |
| Your deployment | `https://yourdomain.com/callback/` |
| InetConnector (demo) | `https://playlists.inetconnector.com/callback/` *(only for invited testers)* |

Click **Add** and **Save**.

### 4️⃣ Configure the app

#### Option A – `appsettings.json`
```json
{
  "Spotify": {
    "ClientId": "your_spotify_client_id",
    "RedirectUri": "https://localhost:5001/callback/"
  }
}
```

#### Option B – Environment variables
```bash
setx SPOTIFY_CLIENT_ID "your_spotify_client_id"
setx SPOTIFY_REDIRECT_URI "https://yourdomain.com/callback/"
```

---

## 💻 Run Locally

```bash
dotnet restore
dotnet run
```

Then open [https://localhost:5001](https://localhost:5001) in your browser  
and log in with Spotify (you must be listed as a tester in your app settings).

---

## 🎵 Playlist Logic Overview

| Method | Description |
|---------|-------------|
| `GeneratePlaylistAsync()` | Creates playlists from liked or top tracks, handling pagination & rate limits. |
| `GenerateAlternativeFavoritesAsync()` | Finds similar songs from same albums/artists — removes duplicates. |
| `GenerateRecommendationsAsync()` | Uses up to 5 seed tracks for Spotify’s recommendations; market auto-detected from UI culture. |

---

## 🎧 Content Filtering

Audiobooks and podcasts are automatically excluded:
```csharp
if (type.Contains("audiobook") || type.Contains("podcast"))
    return false;
```

---

## 🧩 Additional Features

- 4-minute **cooldown** per user to avoid API spam  
- **Automatic localization** (English / German)  
- **Donation prompt** after repeated use  
- **Robust error logging** with Spotify API responses  

---

## 🧾 Recent Updates (v3.3 – Oct 2025)

- Added **Spotify Developer Setup** section  
- Clarified **Redirect URI examples** (InetConnector vs. self-hosted)  
- Added **access invitation notice** for the public demo  
- Integrated automatic **market detection**  
- Fixed `RecommendationsRequest` syntax for current SDK  
- Improved duplicate filtering and logging  

---

## 📜 License
© 2025 **InetConnector / Daniel Frede**  
Licensed for personal and educational use.  
Commercial redistribution requires prior written permission.

---

using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace SpotifyPlaylistWebApp.Controllers;

public class LanguageController : Controller
{
    [HttpGet]
    public IActionResult SetLanguage(string culture = "en-US", string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(culture))
            culture = "en-US";

        // ✅ Nur erlaubte Kulturen
        var allowed = new[] { "de-DE", "en-US" };
        if (!allowed.Contains(culture))
            culture = "en-US";

        // ✅ Cookie für 1 Jahr setzen (global gültig)
        var cookieValue = CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture, culture));
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            cookieValue,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                Path = "/"
            });

        // ✅ Zurück zur vorherigen Seite
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return RedirectToAction("Index", "Home");
    }
}
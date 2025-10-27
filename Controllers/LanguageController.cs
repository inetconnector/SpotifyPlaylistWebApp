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

        // ✅ Allowed cultures (including Spanish)
        var allowed = new[] { "de-DE", "en-US", "es-ES" };
        if (!allowed.Contains(culture))
            culture = "en-US";

        // ✅ Set cookie for 1 year (globally valid)
        var cookieValue = CookieRequestCultureProvider.MakeCookieValue(
            new RequestCulture(culture, culture)
        );

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            cookieValue,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                Path = "/"
            });

        // ✅ Redirect back to the previous local page if available
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        // ✅ Default fallback: go to home page
        return RedirectToAction("Index", "Home");
    }
}
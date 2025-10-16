using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SpotifyPlaylistWebApp.Controllers;

public class ErrorController : Controller
{
    [Route("/Error")]
    [Route("/Home/Error")]
    public IActionResult Index(string? message = null)
    {
        try
        {
            var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            var ex = feature?.Error;

            var model = new ErrorViewModel
            {
                Path = feature?.Path ?? "unbekannt",
                Message = message ?? ex?.Message ?? "Ein unbekannter Fehler ist aufgetreten.",
                StackTrace = ex?.StackTrace ?? "(keine weiteren Details)"
            };

            Response.StatusCode = 500;
            return View("Error", model);
        }
        catch (Exception ex2)
        {
            // falls sogar der Errorhandler selbst fehlschlägt
            return Content(
                $"Fehler in ErrorController: {ex2.Message}<br><br>{ex2.StackTrace}",
                "text/html");
        }
    }
}

public class ErrorViewModel
{
    public string? Path { get; set; }
    public string? Message { get; set; }
    public string? StackTrace { get; set; }
}
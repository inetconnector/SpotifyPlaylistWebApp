using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SpotifyPlaylistWebApp.Controllers;

public class ErrorController : Controller
{
    [Route("Home/Error")]
    public IActionResult Index()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        var ex = feature?.Error;

        var model = new ErrorViewModel
        {
            Path = feature?.Path ?? "unbekannt",
            Message = ex?.Message ?? "Ein unbekannter Fehler ist aufgetreten.",
            StackTrace = ex?.StackTrace
        };

        Response.StatusCode = 500;
        return View("Error", model);
    }
}

public class ErrorViewModel
{
    public string? Path { get; set; }
    public string? Message { get; set; }
    public string? StackTrace { get; set; }
}
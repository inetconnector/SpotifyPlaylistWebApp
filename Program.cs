using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// ==========================
// 🔧 Services registrieren
// ==========================

// MVC / Razor Views
builder.Services.AddControllersWithViews();

// HTTP-Client (z. B. für SpotifyAPI)
builder.Services.AddHttpClient();

// 🧩 Session aktivieren
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(1);  // Sitzungslaufzeit
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Logging (optional, aber empfohlen)
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var app = builder.Build();

// ==========================
// 🔧 Middleware-Pipeline
// ==========================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// 🔹 Session muss vor Authorization und Endpoints kommen
app.UseSession();

app.UseAuthorization();

// Standard-Routing
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}"
);

app.Run();
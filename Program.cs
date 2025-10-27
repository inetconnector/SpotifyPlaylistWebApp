using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using SpotifyPlaylistWebApp.Services;
using Microsoft.Extensions.Localization;

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// 🔹 Core Services
// =====================================================
builder.Services.AddControllersWithViews()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();

// Localization system (resources stored in /Resources)
builder.Services.AddLocalization(opt => opt.ResourcesPath = "Resources");

// Data protection (used for cookies, tokens, etc.)
builder.Services.AddDataProtection()
    .SetApplicationName("SpotifyPlaylistWebApp");

// Session handling (used for Spotify & Plex tokens)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>
{
    o.Cookie.IsEssential = true;
    o.IdleTimeout = TimeSpan.FromMinutes(60);
});

// Cookie policy (simplified: consent not required)
builder.Services.Configure<CookiePolicyOptions>(opt =>
{
    opt.CheckConsentNeeded = _ => false;
    opt.MinimumSameSitePolicy = SameSiteMode.Lax;
});

// Required for accessing HttpContext in Razor and services
builder.Services.AddHttpContextAccessor();

// =====================================================
// 🔹 Plex Integration
// =====================================================

// Token store for managing Plex tokens in session
builder.Services.AddScoped<IPlexTokenStore, PlexTokenStore>();

// Register default HttpClient factory (for API calls)
builder.Services.AddHttpClient();

// Register PlexService with localization and HttpClient support
builder.Services.AddTransient<PlexService>();

// =====================================================
// 🔹 Localization Setup
// =====================================================
var supportedCultures = new[]
{
    new CultureInfo("de-DE"),
    new CultureInfo("en-US"),
    new CultureInfo("es-ES")
};

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en-US");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders = new IRequestCultureProvider[]
    {
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    };
});

var app = builder.Build();

// =====================================================
// 🔹 Middleware Pipeline
// =====================================================

// Apply localization settings from configuration
var locOptions = app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;
app.UseRequestLocalization(locOptions);

// Exception handling and HSTS for production
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Standard middleware chain
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCookiePolicy();
app.UseSession();
app.UseAuthorization();

// Default route mapping
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

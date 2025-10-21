using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using SpotifyPlaylistWebApp.Services;

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// 🔹 Core Services
// =====================================================
builder.Services.AddControllersWithViews()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();

builder.Services.AddLocalization(opt => opt.ResourcesPath = "Resources");
builder.Services.AddDataProtection()
    .SetApplicationName("SpotifyPlaylistWebApp");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>
{
    o.Cookie.IsEssential = true;
    o.IdleTimeout = TimeSpan.FromMinutes(60);
});

builder.Services.Configure<CookiePolicyOptions>(opt =>
{
    opt.CheckConsentNeeded = _ => false;
    opt.MinimumSameSitePolicy = SameSiteMode.Lax;
});

// ✅ NEU – wichtig für deinen LanguageSwitcher.cshtml
builder.Services.AddHttpContextAccessor();

// =====================================================
// 🔹 Plex Integration
// =====================================================
builder.Services.AddScoped<IPlexTokenStore, PlexTokenStore>();

builder.Services.AddHttpClient<PlexService>();

// =====================================================
// 🔹 Localization Setup
// =====================================================
var supportedCultures = new[] { new CultureInfo("de-DE"), new CultureInfo("en-US") };

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
var locOptions = app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;
app.UseRequestLocalization(locOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCookiePolicy();
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    "default",
    "{controller=Home}/{action=Index}/{id?}");

app.Run();
namespace SpotifyPlaylistWebApp.Services;

public sealed class MissingCacheCleanupService : BackgroundService
{
    private readonly IWebHostEnvironment _env;
    private readonly MissingCacheStore _store;

    public MissingCacheCleanupService(MissingCacheStore store, IWebHostEnvironment env)
    {
        _store = store;
        _env = env;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var root = Path.Combine(_env.WebRootPath ?? "wwwroot", "data");
                if (Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var serverId = Path.GetFileName(dir);
                        _store.CleanupOlderThanDays(serverId);
                    }
            }
            catch
            {
                /* ignore */
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
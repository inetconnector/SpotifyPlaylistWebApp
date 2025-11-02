namespace SpotifyPlaylistWebApp.Services;

/// <summary>
///     Defines a simple contract for storing and retrieving Plex authentication tokens.
/// </summary>
public interface IPlexTokenStore
{
    /// <summary>
    ///     Saves a Plex token for a given user key.
    /// </summary>
    /// <param name="userKey">Unique user key (e.g., session ID or Spotify user ID).</param>
    /// <param name="plexToken">The Plex authentication token.</param>
    /// <param name="ct">Optional cancellation token.</param>
    Task SaveAsync(string userKey, string plexToken, CancellationToken ct = default);

    /// <summary>
    ///     Loads the stored Plex token for a given user key.
    /// </summary>
    /// <param name="userKey">Unique user key.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The stored Plex token, or null if not found.</returns>
    Task<string?> LoadAsync(string userKey, CancellationToken ct = default);

    /// <summary>
    ///     Deletes the Plex token associated with the given user key.
    /// </summary>
    /// <param name="userKey">Unique user key.</param>
    /// <param name="ct">Optional cancellation token.</param>
    Task DeleteAsync(string userKey, CancellationToken ct = default);
}
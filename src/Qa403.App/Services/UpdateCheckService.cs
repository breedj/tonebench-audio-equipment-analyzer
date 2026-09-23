using System.Reflection;
using System.Text.Json;

namespace Qa403.App.Services;

public sealed record UpdateCheckResult(string CurrentVersion, string? LatestVersion, string? LatestUrl, bool UpdateAvailable);

/// <summary>
/// Passive version check per docs/qa403-app-stack-spec.md §8: no auto-update, just "is a newer
/// release out" for the SPA to show as a dismissible banner. Queries the GitHub Releases API
/// directly rather than hosting a separate manifest file — the tag pushed for a release already is
/// the version signal.
///
/// Every failure path (network, non-2xx, unparsable body, no releases yet) returns
/// "no update available" rather than throwing — an offline bench machine must never surface an
/// error over this. Uses JsonDocument rather than a deserialized POCO so this stays trim-safe
/// without needing a source-generated JsonSerializerContext for one small external response.
/// </summary>
public sealed class UpdateCheckService(IHttpClientFactory httpClientFactory, ILogger<UpdateCheckService> logger)
{
    private const string ReleasesUrl = "repos/breedj/qa40x-audio-analyzer-cp/releases/latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    /// <summary>This build's version, as the release check compares it and the log announces it.</summary>
    public static string CurrentVersion { get; } = (typeof(UpdateCheckService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UpdateCheckService).Assembly.GetName().Version?.ToString()
        ?? "0.0.0");

    private readonly string _currentVersion = CurrentVersion;

    private UpdateCheckResult? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<UpdateCheckResult> GetStatusAsync(CancellationToken ct)
    {
        if (_cached is { } cached && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
            return cached;

        var result = await FetchAsync(ct);
        _cached = result;
        _cachedAt = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task<UpdateCheckResult> FetchAsync(CancellationToken ct)
    {
        var noUpdate = new UpdateCheckResult(_currentVersion, null, null, false);

        if (!SemVer.TryParse(_currentVersion, out var current))
            return noUpdate;

        try
        {
            var client = httpClientFactory.CreateClient("GitHubReleases");
            using var response = await client.GetAsync(ReleasesUrl, ct);
            if (!response.IsSuccessStatusCode)
                return noUpdate;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var tagName = doc.RootElement.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
            if (tagName is null || !SemVer.TryParse(tagName, out var latest))
                return noUpdate;

            return new UpdateCheckResult(_currentVersion, latest!.Value.ToString(), htmlUrl, latest.Value.CompareTo(current!.Value) > 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Update check against GitHub Releases failed; treating as no update available");
            return noUpdate;
        }
    }
}

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace LocalBridge.Services;

/// <summary>
/// Result of an update check operation.
/// </summary>
public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; init; }
    public string LatestVersion { get; init; } = string.Empty;
    public string ReleaseUrl { get; init; } = string.Empty;
}

/// <summary>
/// Service for checking application updates via GitHub Releases API.
/// </summary>
public sealed class UpdateCheckService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/b4631119-oss/LocalBridge/releases/latest";
    private const string UserAgent = "LocalBridge-Updater/1.0";

    private readonly HttpClient _httpClient;

    public UpdateCheckService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>
    /// Checks for a newer version on GitHub Releases.
    /// Returns IsUpdateAvailable=false on any error (network, rate limit, parsing).
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 403 = rate limit, 404 = no releases, etc. — silently return no update
                return new UpdateCheckResult { IsUpdateAvailable = false };
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Extract tag_name (e.g., "v1.0.1")
            if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            {
                return new UpdateCheckResult { IsUpdateAvailable = false };
            }

            var latestTag = tagElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(latestTag))
            {
                return new UpdateCheckResult { IsUpdateAvailable = false };
            }

            // Extract html_url (release page URL)
            string releaseUrl = string.Empty;
            if (root.TryGetProperty("html_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
            {
                releaseUrl = urlElement.GetString() ?? string.Empty;
            }

            // Compare versions
            var currentVersion = GetCurrentVersion();
            var isNewer = IsNewerVersion(latestTag, currentVersion);

            return new UpdateCheckResult
            {
                IsUpdateAvailable = isNewer,
                LatestVersion = latestTag.TrimStart('v'),
                ReleaseUrl = releaseUrl
            };
        }
        catch
        {
            // Network error, JSON parsing error, etc. — silently return no update
            return new UpdateCheckResult { IsUpdateAvailable = false };
        }
    }

    /// <summary>
    /// Gets the current application version from the assembly.
    /// </summary>
    private static Version GetCurrentVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var name = assembly.GetName();
        return name.Version ?? new Version(0, 0, 0);
    }

    /// <summary>
    /// Compares two version strings. latestTag may have a 'v' prefix (e.g., "v1.0.1").
    /// Returns true if latestTag version is greater than currentVersion.
    /// </summary>
    private static bool IsNewerVersion(string latestTag, Version currentVersion)
    {
        try
        {
            var cleanTag = latestTag.TrimStart('v', 'V');
            var latestVersion = Version.Parse(cleanTag);
            return latestVersion > currentVersion;
        }
        catch
        {
            // If parsing fails, assume no update
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
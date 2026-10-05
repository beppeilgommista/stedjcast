using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Stedjcast.Services;

/// <summary>Checks GitHub for a release newer than the running version.</summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/beppeilgommista/stedjcast/releases/latest";

    private sealed record Release([property: JsonPropertyName("tag_name")] string TagName,
                                  [property: JsonPropertyName("html_url")] string HtmlUrl);

    /// <summary>The release page URL when a newer version exists; null otherwise or on any error.</summary>
    public static async Task<string?> FindNewerReleaseAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var current = typeof(UpdateChecker).Assembly.GetName().Version!;
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Stedjcast/{current.ToString(3)}");

            var release = await http.GetFromJsonAsync<Release>(LatestReleaseApi);
            return release is not null
                   && Version.TryParse(release.TagName.TrimStart('v', 'V'), out var latest)
                   && latest > new Version(current.Major, current.Minor, current.Build)
                ? release.HtmlUrl
                : null;
        }
        catch (Exception exception)
        {
            // Offline or GitHub unreachable: no indicator, nothing shown to the user.
            LoggingService.Write($"Update check failed: {exception.Message}");
            return null;
        }
    }
}

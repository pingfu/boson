using System.Net.Http.Headers;
using System.Text.Json;

namespace Boson.Github;

public sealed record ManifestConversion(long Id, string Slug, string Pem, string WebhookSecret);

public interface IGithubClient
{
    /// <summary>POST /app-manifests/{code}/conversions.</summary>
    Task<ManifestConversion> ConvertManifestAsync(string code, CancellationToken ct = default);

    /// <summary>POST /app/installations/{id}/access_tokens with a Bearer JWT.</summary>
    Task<InstallationToken> CreateInstallationTokenAsync(
        string jwt, long installationId, CancellationToken ct = default);
}

public sealed class GithubClient : IGithubClient
{
    public const string BaseUrl = "https://api.github.com";
    private readonly HttpClient _http;

    public GithubClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("boson", VersionInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<ManifestConversion> ConvertManifestAsync(string code, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"{BaseUrl}/app-manifests/{Uri.EscapeDataString(code)}/conversions", content: null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GithubApiException(
                $"manifest conversion failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new ManifestConversion(
            root.GetProperty("id").GetInt64(),
            root.GetProperty("slug").GetString()!,
            root.GetProperty("pem").GetString()!,
            root.GetProperty("webhook_secret").GetString()!);
    }

    public async Task<InstallationToken> CreateInstallationTokenAsync(
        string jwt, long installationId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{BaseUrl}/app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GithubApiException(
                $"installation token exchange failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new InstallationToken(
            root.GetProperty("token").GetString()!,
            root.GetProperty("expires_at").GetDateTimeOffset());
    }
}

public sealed class GithubApiException(string message) : Exception(message);

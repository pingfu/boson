using System.Text;
using System.Text.Json;

namespace Boson.Caddy;

public interface ICaddyAdminClient
{
    Task<JsonDocument> GetConfigAsync(CancellationToken ct = default);
    /// <summary>POST /load — full replace, never a partial patch.</summary>
    Task LoadConfigAsync(JsonDocument cfg, CancellationToken ct = default);
    Task<bool> IsReachableAsync(CancellationToken ct = default);
}

public sealed class CaddyAdminClient : ICaddyAdminClient
{
    public const string BaseUrl = "http://127.0.0.1:2019";
    private readonly HttpClient _http;

    public CaddyAdminClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<JsonDocument> GetConfigAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{BaseUrl}/config/", ct);
        
        response.EnsureSuccessStatusCode();
        
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task LoadConfigAsync(JsonDocument cfg, CancellationToken ct = default)
    {
        var content = new StringContent(JsonSerializer.Serialize(cfg.RootElement), Encoding.UTF8, "application/json");
        
        var response = await _http.PostAsync($"{BaseUrl}/load", content, ct);
        
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"caddy rejected config load ({(int)response.StatusCode}): {body}");
        }
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{BaseUrl}/config/", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Boson.Serve;

public sealed class DaemonUnreachableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record RpcResult<T>(HttpStatusCode Status, T? Body, string? Error);

/// <summary>
/// The CLI side of the unix-socket RPC. Auth is the socket file
/// itself; no tokens, no TLS.
/// </summary>
public sealed class RpcClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public RpcClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://boson-daemon/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public Task<RpcResult<AddStartResponse>> AddAsync(AddRequest request, CancellationToken ct = default) =>
        SendAsync<AddStartResponse>(() => _http.PostAsJsonAsync("add", request, Json, ct), ct);

    public Task<RpcResult<AddStatusResponse>> AddStatusAsync(string token, CancellationToken ct = default) =>
        SendAsync<AddStatusResponse>(() => _http.GetAsync($"add/status/{token}", ct), ct);

    public Task<RpcResult<DeployStartResponse>> DeployAsync(string repo, CancellationToken ct = default) =>
        SendAsync<DeployStartResponse>(() => _http.PostAsync($"deploy/{repo}", content: null, ct), ct);

    public Task<RpcResult<RemoveResponse>> RemoveAsync(string repo, bool purge, CancellationToken ct = default) =>
        SendAsync<RemoveResponse>(() => _http.PostAsync($"remove/{repo}?purge={purge}", content: null, ct), ct);

    private static async Task<RpcResult<T>> SendAsync<T>(
        Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or IOException)
        {
            throw new DaemonUnreachableException(
                "boson daemon unreachable — check: systemctl status boson / journalctl -u boson", e);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            var parsed = string.IsNullOrWhiteSpace(body)
                ? default
                : JsonSerializer.Deserialize<T>(body, Json);
            return new RpcResult<T>(response.StatusCode, parsed, null);
        }

        string? error = null;
        try
        {
            error = JsonSerializer.Deserialize<ErrorResponse>(body, Json)?.Error;
        }
        catch (JsonException)
        {
        }
        return new RpcResult<T>(response.StatusCode, default, error);
    }

    public void Dispose() => _http.Dispose();
}

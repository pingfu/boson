using System.Text.Json;
using System.Text.Json.Nodes;
using Boson.Storage;

namespace Boson.Caddy;

/// <summary>
/// Synthesises the whole structured-JSON config from DB state (spec §7).
/// Route order: admin /_boson/* → daemon, then per project its www redirect
/// and its proxy route, then the admin catch-all 404.
/// </summary>
public sealed class CaddyConfigBuilder
{
    public const int DaemonPort = 9000;

    public JsonDocument Build(IReadOnlyList<Project> projects, string adminHostname)
    {
        var routes = new JsonArray
        {
            ProxyRoute(
                match: new JsonObject
                {
                    ["host"] = new JsonArray(adminHostname),
                    ["path"] = new JsonArray("/_boson/*"),
                },
                dial: $"127.0.0.1:{DaemonPort}"),
        };

        foreach (var p in projects.OrderBy(p => p.Hostname, StringComparer.Ordinal))
        {
            // The www alias only exists in Caddy's config, so the redirect has
            // to live here: no project container can answer for a hostname
            // Caddy holds no certificate for.
            if (!p.Hostname.StartsWith("www.", StringComparison.Ordinal))
                routes.Add(RedirectRoute($"www.{p.Hostname}", p.Hostname));

            routes.Add(ProxyRoute(
                match: new JsonObject { ["host"] = new JsonArray(p.Hostname) },
                dial: $"127.0.0.1:{p.UpstreamPort}"));
        }

        routes.Add(new JsonObject
        {
            ["match"] = new JsonArray(new JsonObject
            {
                ["host"] = new JsonArray(adminHostname),
            }),
            ["handle"] = new JsonArray(new JsonObject
            {
                ["handler"] = "static_response",
                ["status_code"] = 404,
            }),
        });

        var doc = new JsonObject
        {
            ["apps"] = new JsonObject
            {
                ["http"] = new JsonObject
                {
                    ["servers"] = new JsonObject
                    {
                        ["main"] = new JsonObject
                        {
                            ["listen"] = new JsonArray(":80", ":443"),
                            ["routes"] = routes,
                            // Access logging: Caddy writes to its own stdout,
                            // captured by `docker logs boson-caddy`.
                            ["logs"] = new JsonObject(),
                        },
                    },
                },
            },
            ["logging"] = new JsonObject
            {
                ["logs"] = new JsonObject
                {
                    ["default"] = new JsonObject
                    {
                        ["writer"] = new JsonObject { ["output"] = "stdout" },
                        ["encoder"] = new JsonObject { ["format"] = "json" },
                        ["level"] = "INFO",
                    },
                },
            },
        };

        return JsonDocument.Parse(doc.ToJsonString());
    }

    private static JsonObject ProxyRoute(JsonObject match, string dial) => new()
    {
        ["match"] = new JsonArray(match),
        ["handle"] = new JsonArray(
            // Transport compression is app-agnostic, so the edge does it for
            // every project; Caddy skips already-compressed responses.
            new JsonObject
            {
                ["handler"] = "encode",
                ["encodings"] = new JsonObject { ["gzip"] = new JsonObject() },
                ["prefer"] = new JsonArray("gzip"),
            },
            new JsonObject
            {
                ["handler"] = "reverse_proxy",
                ["upstreams"] = new JsonArray(new JsonObject { ["dial"] = dial }),
            }),
    };

    private static JsonObject RedirectRoute(string from, string to) => new()
    {
        ["match"] = new JsonArray(new JsonObject { ["host"] = new JsonArray(from) }),
        ["handle"] = new JsonArray(new JsonObject
        {
            ["handler"] = "static_response",
            ["status_code"] = 308,
            ["headers"] = new JsonObject
            {
                ["Location"] = new JsonArray($"https://{to}{{http.request.uri}}"),
            },
        }),
    };
}

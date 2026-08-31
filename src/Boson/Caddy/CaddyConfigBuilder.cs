using System.Text.Json;
using System.Text.Json.Nodes;
using Boson.Storage;

namespace Boson.Caddy;

/// <summary>
/// Synthesises the whole structured-JSON config from DB state (spec §7).
/// Route order: admin /_boson/* → daemon, then project hosts, then the admin
/// catch-all 404.
/// </summary>
public sealed class CaddyConfigBuilder
{
    public const int DaemonPort = 9000;

    public JsonDocument Build(IReadOnlyList<Project> projects, string adminHostname)
    {
        var routes = new JsonArray
        {
            Route(
                match: new JsonObject
                {
                    ["host"] = new JsonArray(adminHostname),
                    ["path"] = new JsonArray("/_boson/*"),
                },
                dial: $"127.0.0.1:{DaemonPort}"),
        };

        foreach (var p in projects.OrderBy(p => p.Hostname, StringComparer.Ordinal))
        {
            routes.Add(Route(
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
                        },
                    },
                },
            },
        };

        return JsonDocument.Parse(doc.ToJsonString());
    }

    private static JsonObject Route(JsonObject match, string dial) => new()
    {
        ["match"] = new JsonArray(match),
        ["handle"] = new JsonArray(new JsonObject
        {
            ["handler"] = "reverse_proxy",
            ["upstreams"] = new JsonArray(new JsonObject { ["dial"] = dial }),
        }),
    };
}

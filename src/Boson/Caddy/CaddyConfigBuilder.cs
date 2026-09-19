using System.Text.Json;
using System.Text.Json.Nodes;
using Boson.Storage;

namespace Boson.Caddy;

/// <summary>
/// Synthesises the whole structured-JSON config from DB state.
/// Route order: control /_boson/* → daemon, then per project its www redirect,
/// its reserved webhook path and its proxy route, then the control catch-all
/// 404. The control hostname can be private, because GitHub never reaches it;
/// each project's own public hostname carries that project's webhook.
/// </summary>
public sealed class CaddyConfigBuilder
{
    public const int DaemonPort = 9000;

    /// <summary>The only path boson reserves on a project's hostname.</summary>
    public const string WebhookPathPrefix = "/_boson/webhook/";

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

        // Ordered so the same DB state always produces byte-identical config:
        // every change is a full replace, and a stable document is what makes
        // one diffable against what Caddy is actually running.
        foreach (var p in projects.OrderBy(p => p.Hostname, StringComparer.Ordinal))
        {
            // The www alias only exists in Caddy's config, so the redirect has
            // to live here: no project container can answer for a hostname
            // Caddy holds no certificate for.
            if (!p.Hostname.StartsWith("www.", StringComparison.Ordinal))
                routes.Add(RedirectRoute($"www.{p.Hostname}", p.Hostname));

            // GitHub delivers to the project's own public hostname, so the
            // control plane never has to be reachable from the internet. This
            // route must precede the project's catch-all proxy route below.
            routes.Add(ProxyRoute(
                match: new JsonObject
                {
                    ["host"] = new JsonArray(p.Hostname),
                    ["path"] = new JsonArray($"{WebhookPathPrefix}*"),
                },
                dial: $"127.0.0.1:{DaemonPort}"));

            routes.Add(ProxyRoute(
                match: new JsonObject { ["host"] = new JsonArray(p.Hostname) },
                dial: $"127.0.0.1:{p.UpstreamPort}"));
        }

        // Last, and only after every project route: the control hostname answers
        // /_boson/* and nothing else. Without this terminator a request to it
        // falls through to whatever Caddy matches next, which is a project's
        // catch-all proxy — the control plane would quietly serve someone's app.
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

        var apps = new JsonObject
        {
            // Issuers are tried in order, so the control hostname needs no
            // configuration either way: a public name gets a real ACME
            // certificate, and a private one (boson.enclave and the like) is
            // rejected by the CA in milliseconds — "does not end with a valid
            // public suffix" — and falls through to Caddy's own CA.
            // Scoped to this subject deliberately: a public project hostname
            // must fail loudly rather than quietly serve an untrusted
            // certificate after a transient ACME failure.
            ["tls"] = new JsonObject
            {
                ["automation"] = new JsonObject
                {
                    ["policies"] = new JsonArray(new JsonObject
                    {
                        ["subjects"] = new JsonArray(adminHostname),
                        ["issuers"] = new JsonArray(
                            new JsonObject { ["module"] = "acme" },
                            new JsonObject { ["module"] = "internal" }),
                    }),
                },
            },
        };

        apps["http"] = new JsonObject
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
        };

        var doc = new JsonObject
        {
            ["apps"] = apps,
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

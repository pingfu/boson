using System.Text.Json;
using System.Text.Json.Nodes;
using Boson.Caddy;
using Boson.Tests.Support;
using Xunit;

namespace Boson.Tests;

public class CaddyConfigBuilderTests
{
    private readonly CaddyConfigBuilder _builder = new();

    // The admin hostname is the control plane and may be private; nothing
    // GitHub reaches depends on it.
    private JsonNode Build(params Storage.Deployment[] deployments)
    {
        using var doc = _builder.Build(deployments, "boson.enclave");
        return JsonNode.Parse(doc.RootElement.GetRawText())!;
    }

    private static JsonArray Routes(JsonNode config) =>
        config["apps"]!["http"]!["servers"]!["main"]!["routes"]!.AsArray();

    [Fact]
    public void The_site_server_listens_on_https_only()
    {
        var listen = Build()["apps"]!["http"]!["servers"]!["main"]!["listen"]!
            .AsArray().Select(l => l!.GetValue<string>());

        // Port 80 belongs to Caddy's automatic HTTPS, which runs the
        // HTTP->HTTPS redirect server and the ACME http-01 challenge there.
        // Claiming it here silently disables both.
        Assert.Equal(new[] { ":443" }, listen);
    }

    [Fact]
    public void No_projects_yields_admin_route_and_catch_all_404()
    {
        var config = Build();
        var expected = JsonNode.Parse("""
        {
          "apps": {
            "tls": {
              "automation": {
                "policies": [
                  {
                    "subjects": ["boson.enclave"],
                    "issuers": [{"module": "acme"}, {"module": "internal"}]
                  }
                ]
              }
            },
            "http": {
              "servers": {
                "main": {
                  "listen": [":443"],
                  "routes": [
                    {
                      "match": [{"host": ["boson.enclave"], "path": ["/_boson/*"]}],
                      "handle": [
                        {"handler": "encode", "encodings": {"gzip": {}}, "prefer": ["gzip"]},
                        {"handler": "reverse_proxy", "upstreams": [{"dial": "127.0.0.1:9000"}]}
                      ]
                    },
                    {
                      "match": [{"host": ["boson.enclave"]}],
                      "handle": [{"handler": "static_response", "status_code": 404}]
                    }
                  ],
                  "logs": {}
                }
              }
            }
          },
          "logging": {
            "logs": {
              "default": {
                "writer": {"output": "stdout"},
                "encoder": {"format": "json"},
                "level": "INFO"
              }
            }
          }
        }
        """)!;
        Assert.True(JsonNode.DeepEquals(expected, config), config.ToJsonString());
    }

    [Fact]
    public void One_project_gets_a_reverse_proxy_route_between_admin_and_404()
    {
        var config = Build(TestProjects.Deployment(repo: "acme/site", hostname: "site.example.com", port: 8080));
        var routes = Routes(config);

        // admin, deployment webhook, deployment proxy, 404
        Assert.Equal(4, routes.Count);
        var projectRoute = routes[2]!;
        Assert.Equal("site.example.com", projectRoute["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("127.0.0.1:8080",
            projectRoute["handle"]![1]!["upstreams"]![0]!["dial"]!.GetValue<string>());
    }

    [Fact]
    public void Project_webhook_path_routes_to_the_daemon_ahead_of_the_container()
    {
        var config = Build(TestProjects.Deployment(hostname: "example.org", port: 8080));
        var routes = Routes(config);

        var webhook = routes[1]!;
        Assert.Equal("example.org", webhook["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("/_boson/webhook/*", webhook["match"]![0]!["path"]![0]!.GetValue<string>());
        Assert.Equal($"127.0.0.1:{CaddyConfigBuilder.DaemonPort}",
            webhook["handle"]![1]!["upstreams"]![0]!["dial"]!.GetValue<string>());

        // Ordering is load-bearing: the catch-all proxy would otherwise swallow it.
        var proxy = routes[2]!;
        Assert.Null(proxy["match"]![0]!["path"]);
        Assert.Equal("127.0.0.1:8080",
            proxy["handle"]![1]!["upstreams"]![0]!["dial"]!.GetValue<string>());
    }

    [Fact]
    public void Control_hostname_falls_back_from_acme_to_caddys_own_ca()
    {
        // Verified against caddy:2-alpine: a private name is rejected by the CA
        // ("does not end with a valid public suffix") and the next issuer in the
        // list produces the certificate. Order is the whole mechanism.
        var policy = Build(TestProjects.Deployment())["apps"]!["tls"]!["automation"]!["policies"]![0]!;

        Assert.Equal("boson.enclave", policy["subjects"]![0]!.GetValue<string>());
        Assert.Equal("acme", policy["issuers"]![0]!["module"]!.GetValue<string>());
        Assert.Equal("internal", policy["issuers"]![1]!["module"]!.GetValue<string>());
    }

    [Fact]
    public void Project_hostnames_get_no_internal_fallback()
    {
        // A public site must fail loudly rather than quietly serve an untrusted
        // certificate, so only the control hostname carries a policy.
        var policies = Build(TestProjects.Deployment(hostname: "example.org"))
            ["apps"]!["tls"]!["automation"]!["policies"]!.AsArray();

        var subjects = policies.SelectMany(p => p!["subjects"]!.AsArray())
            .Select(s => s!.GetValue<string>());
        Assert.DoesNotContain("example.org", subjects);
    }

    [Fact]
    public void Every_proxy_route_compresses_before_proxying()
    {
        var config = Build(TestProjects.Deployment());
        foreach (var route in Routes(config))
        {
            var handlers = route!["handle"]!.AsArray();
            if (handlers[^1]!["handler"]!.GetValue<string>() != "reverse_proxy") continue;
            Assert.Equal("encode", handlers[0]!["handler"]!.GetValue<string>());
            Assert.Equal("gzip", handlers[0]!["prefer"]![0]!.GetValue<string>());
        }
    }

    [Fact]
    public void A_declared_alias_gets_a_308_route_before_the_proxy_route()
    {
        var config = Build(TestProjects.Deployment(
            hostname: "example.org", aliases: "www.example.org", port: 8080));
        var routes = Routes(config);

        Assert.Equal(5, routes.Count);
        var redirect = routes[1]!;
        Assert.Equal("www.example.org", redirect["match"]![0]!["host"]![0]!.GetValue<string>());
        var handler = redirect["handle"]![0]!;
        Assert.Equal("static_response", handler["handler"]!.GetValue<string>());
        Assert.Equal(308, handler["status_code"]!.GetValue<int>());
        Assert.Equal("https://example.org{http.request.uri}",
            handler["headers"]!["Location"]![0]!.GetValue<string>());

        // The apex still proxies, after its redirect and webhook routes.
        Assert.Equal("example.org", routes[3]!["match"]![0]!["host"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_deployment_declaring_no_alias_gets_no_redirect()
    {
        // The `www` redirect every project used to get whether it wanted one or
        // not now comes from the aliases the repository declares.
        var routes = Routes(Build(TestProjects.Deployment(hostname: "example.org")));

        Assert.DoesNotContain(routes, r =>
            r!["handle"]![0]!["status_code"]?.GetValue<int>() == 308);
    }

    [Fact]
    public void Several_aliases_all_redirect_to_the_one_hostname()
    {
        var routes = Routes(Build(TestProjects.Deployment(
            hostname: "example.org", aliases: "www.example.org,example.com")));

        var redirects = routes
            .Where(r => r!["handle"]![0]!["status_code"]?.GetValue<int>() == 308)
            .Select(r => r!["match"]![0]!["host"]![0]!.GetValue<string>());

        Assert.Equal(["example.com", "www.example.org"], redirects);
    }

    [Fact]
    public void Access_logging_is_enabled_on_the_server()
    {
        var config = Build();
        Assert.NotNull(config["apps"]!["http"]!["servers"]!["main"]!["logs"]);
        Assert.Equal("stdout",
            config["logging"]!["logs"]!["default"]!["writer"]!["output"]!.GetValue<string>());
    }

    [Fact]
    public void Many_projects_are_ordered_by_hostname_with_404_last()
    {
        var config = Build(
            TestProjects.Deployment(repo: "acme/zeta", hostname: "zeta.example.com", port: 8082),
            TestProjects.Deployment(repo: "acme/alpha", hostname: "alpha.example.com", port: 8081));
        var routes = Routes(config);

        // admin, then each deployment's webhook + proxy in hostname order, then 404
        Assert.Equal(6, routes.Count);
        Assert.Equal("alpha.example.com", routes[2]!["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("zeta.example.com", routes[4]!["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("static_response", routes[5]!["handle"]![0]!["handler"]!.GetValue<string>());
        Assert.Equal(404, routes[5]!["handle"]![0]!["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void Admin_boson_route_always_comes_first()
    {
        var config = Build(TestProjects.Deployment(hostname: "aaa.example.com"));
        var first = Routes(config)[0]!;
        Assert.Equal("/_boson/*", first["match"]![0]!["path"]![0]!.GetValue<string>());
        Assert.Equal($"127.0.0.1:{CaddyConfigBuilder.DaemonPort}",
            first["handle"]![1]!["upstreams"]![0]!["dial"]!.GetValue<string>());
    }

    [Fact]
    public void Output_is_valid_json_document()
    {
        using var doc = _builder.Build(
            [TestProjects.Deployment()], "deploy.example.com");
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}

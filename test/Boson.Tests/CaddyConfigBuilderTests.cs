using System.Text.Json;
using System.Text.Json.Nodes;
using Boson.Caddy;
using Boson.Tests.Support;
using Xunit;

namespace Boson.Tests;

public class CaddyConfigBuilderTests
{
    private readonly CaddyConfigBuilder _builder = new();

    private JsonNode Build(params Storage.Project[] projects)
    {
        using var doc = _builder.Build(projects, "deploy.example.com");
        return JsonNode.Parse(doc.RootElement.GetRawText())!;
    }

    private static JsonArray Routes(JsonNode config) =>
        config["apps"]!["http"]!["servers"]!["main"]!["routes"]!.AsArray();

    [Fact]
    public void No_projects_yields_admin_route_and_catch_all_404()
    {
        var config = Build();
        var expected = JsonNode.Parse("""
        {
          "apps": {
            "http": {
              "servers": {
                "main": {
                  "listen": [":80", ":443"],
                  "routes": [
                    {
                      "match": [{"host": ["deploy.example.com"], "path": ["/_boson/*"]}],
                      "handle": [
                        {"handler": "encode", "encodings": {"gzip": {}}, "prefer": ["gzip"]},
                        {"handler": "reverse_proxy", "upstreams": [{"dial": "127.0.0.1:9000"}]}
                      ]
                    },
                    {
                      "match": [{"host": ["deploy.example.com"]}],
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
        var config = Build(TestProjects.New(repo: "acme/site", hostname: "site.example.com", port: 8080));
        var routes = Routes(config);

        // admin, www redirect, project proxy, 404
        Assert.Equal(4, routes.Count);
        var projectRoute = routes[2]!;
        Assert.Equal("site.example.com", projectRoute["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("127.0.0.1:8080",
            projectRoute["handle"]![1]!["upstreams"]![0]!["dial"]!.GetValue<string>());
    }

    [Fact]
    public void Every_proxy_route_compresses_before_proxying()
    {
        var config = Build(TestProjects.New());
        foreach (var route in Routes(config))
        {
            var handlers = route!["handle"]!.AsArray();
            if (handlers[^1]!["handler"]!.GetValue<string>() != "reverse_proxy") continue;
            Assert.Equal("encode", handlers[0]!["handler"]!.GetValue<string>());
            Assert.Equal("gzip", handlers[0]!["prefer"]![0]!.GetValue<string>());
        }
    }

    [Fact]
    public void Every_project_gets_a_www_308_route_before_its_proxy_route()
    {
        var config = Build(TestProjects.New(hostname: "marketcanary.co", port: 8080));
        var routes = Routes(config);

        Assert.Equal(4, routes.Count);
        var redirect = routes[1]!;
        Assert.Equal("www.marketcanary.co", redirect["match"]![0]!["host"]![0]!.GetValue<string>());
        var handler = redirect["handle"]![0]!;
        Assert.Equal("static_response", handler["handler"]!.GetValue<string>());
        Assert.Equal(308, handler["status_code"]!.GetValue<int>());
        Assert.Equal("https://marketcanary.co{http.request.uri}",
            handler["headers"]!["Location"]![0]!.GetValue<string>());

        // The apex still proxies, immediately after its redirect route.
        Assert.Equal("marketcanary.co", routes[2]!["match"]![0]!["host"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_www_hostname_gets_no_redirect_of_its_own()
    {
        // www.www.example.com is never right.
        var routes = Routes(Build(TestProjects.New(hostname: "www.example.com")));
        Assert.DoesNotContain(routes, r =>
            r!["handle"]![0]!["status_code"]?.GetValue<int>() == 308);
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
            TestProjects.New(repo: "acme/zeta", hostname: "zeta.example.com", port: 8082),
            TestProjects.New(repo: "acme/alpha", hostname: "alpha.example.com", port: 8081));
        var routes = Routes(config);

        // admin, then each project's www redirect + proxy in hostname order, then 404
        Assert.Equal(6, routes.Count);
        Assert.Equal("www.alpha.example.com", routes[1]!["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("alpha.example.com", routes[2]!["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("www.zeta.example.com", routes[3]!["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("zeta.example.com", routes[4]!["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Equal("static_response", routes[5]!["handle"]![0]!["handler"]!.GetValue<string>());
        Assert.Equal(404, routes[5]!["handle"]![0]!["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void Admin_boson_route_always_comes_first()
    {
        var config = Build(TestProjects.New(hostname: "aaa.example.com"));
        var first = Routes(config)[0]!;
        Assert.Equal("/_boson/*", first["match"]![0]!["path"]![0]!.GetValue<string>());
        Assert.Equal($"127.0.0.1:{CaddyConfigBuilder.DaemonPort}",
            first["handle"]![1]!["upstreams"]![0]!["dial"]!.GetValue<string>());
    }

    [Fact]
    public void Output_is_valid_json_document()
    {
        using var doc = _builder.Build(
            [TestProjects.New()], "deploy.example.com");
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}

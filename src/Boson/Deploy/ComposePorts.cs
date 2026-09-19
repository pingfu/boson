using System.Text.Json;

namespace Boson.Deploy;

/// <summary>
/// Checks what a project's compose file publishes, read back from
/// `docker compose config` so compose itself does the parsing and the variable
/// substitution. Caddy dials one loopback port per project, so publishing
/// anything else is a deploy that cannot work, and the check runs before the
/// build rather than leaving a 502 to explain it.
/// </summary>
public static class ComposePorts
{
    private const string HostPortVariable = ComposeVariables.HostPortVariable;

    /// <summary>The problem with what this config publishes, or null when it is right.</summary>
    public static string? Problem(string composeConfigJson, int allocatedPort)
    {
        List<Binding> bindings;

        try
        {
            bindings = Read(composeConfigJson);
        }
        catch (JsonException e)
        {
            return $"could not read docker compose config: {e.Message}";
        }

        if (bindings.Count == 0)
            return $"no service publishes a port; boson routes to one, so publish it as " +
                   $"\"127.0.0.1:${{{HostPortVariable}}}:<the port your app listens on>\"";

        foreach (var b in bindings)
        {
            if (!IsLoopback(b.HostIp))
                return $"{b.Service} publishes {Describe(b)} on every interface, which puts it on " +
                       $"the internet with no TLS; bind it to 127.0.0.1";

            if (b.Published != allocatedPort)
                return $"{b.Service} publishes host port {b.Published}, and boson allocated " +
                       $"{allocatedPort} for this project; publish " +
                       $"\"127.0.0.1:${{{HostPortVariable}}}:<the port your app listens on>\" " +
                       $"so the number comes from boson";
        }

        return null;
    }

    private static List<Binding> Read(string composeConfigJson)
    {
        var bindings = new List<Binding>();

        using var doc = JsonDocument.Parse(composeConfigJson);

        if (!doc.RootElement.TryGetProperty("services", out var services)) return bindings;

        foreach (var service in services.EnumerateObject())
        {
            if (!service.Value.TryGetProperty("ports", out var ports)) continue;

            foreach (var port in ports.EnumerateArray())
            {
                // `published` comes back as a string on some compose versions
                // and a number on others.
                var published = port.TryGetProperty("published", out var p)
                    ? p.ValueKind == JsonValueKind.Number ? p.GetInt32() : int.Parse(p.GetString() ?? "0")
                    : 0;

                var hostIp = port.TryGetProperty("host_ip", out var ip) ? ip.GetString() : null;
                var target = port.TryGetProperty("target", out var t) ? t.GetInt32() : 0;

                bindings.Add(new Binding(service.Name, hostIp, published, target));
            }
        }

        return bindings;
    }

    private static bool IsLoopback(string? hostIp) =>
        hostIp is "127.0.0.1" or "::1" or "localhost";

    private static string Describe(Binding b) => $"{b.Published}:{b.Target}";

    private sealed record Binding(string Service, string? HostIp, int Published, int Target);
}

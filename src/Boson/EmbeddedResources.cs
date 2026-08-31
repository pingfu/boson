using System.Reflection;

namespace Boson;

public static class EmbeddedResources
{
    public static string Read(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var suffix = name.Replace('/', '.');
        var full = asm.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"embedded resource not found: {name}");
        
        using var stream = asm.GetManifestResourceStream(full)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string SystemdUnit => Read("boson.service");
    public static string PlatformCompose => Read("platform-compose.yaml");
    public static string ManifestStartHtml => Read("manifest-start.html");
    public static string ManifestSuccessHtml => Read("manifest-success.html");
}

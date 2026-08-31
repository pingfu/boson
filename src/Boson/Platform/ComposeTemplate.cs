namespace Boson.Platform;

public static class ComposeTemplate
{
    /// <summary>Rendered to a string and piped via stdin to docker compose -f - (spec §9).</summary>
    public static string Render() => EmbeddedResources.PlatformCompose;
}

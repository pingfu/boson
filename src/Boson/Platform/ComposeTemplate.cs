namespace Boson.Platform;

public static class ComposeTemplate
{
    /// <summary>Rendered to a string and piped via stdin to docker compose -f -.</summary>
    public static string Render() => EmbeddedResources.PlatformCompose;
}

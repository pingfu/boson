namespace Boson.Deploy;

/// <summary>
/// What boson supplies to every `docker compose` it runs for a deployment.
/// Supplied per invocation rather than written into the checkout: the values
/// live in boson's own state, so a `git reset --hard`, a re-clone or a purge
/// cannot disagree with them, and a secret never enters a git working tree.
/// </summary>
public sealed record ComposeVariables(int HostPort, string EnvFilePath, string Commit)
{
    public const string HostPortVariable = "BOSON_HOST_PORT";
    public const string EnvFileVariable = "BOSON_ENV_FILE";

    /// <summary>
    /// The commit this deploy fetched. Named for what it is rather than for
    /// tagging an image with it, which is one use among several: an app that
    /// reports its own version wants the same value.
    /// </summary>
    public const string CommitVariable = "BOSON_COMMIT";

    public Dictionary<string, string> ToEnvironment() => new()
    {
        [HostPortVariable] = HostPort.ToString(),
        [EnvFileVariable] = EnvFilePath,
        [CommitVariable] = Commit,
    };
}

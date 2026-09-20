namespace Boson.Deploy;

/// <summary>
/// What boson supplies to every `docker compose` it runs for a deployment.
/// Supplied per invocation rather than written into the checkout: the values
/// live in boson's own state, so a `git reset --hard`, a re-clone or a purge
/// cannot disagree with them, and a secret never enters a git working tree.
/// </summary>
public sealed record ComposeVariables(
    int Port, string EnvFilePath, string Commit, string BranchSlug)
{
    /// <summary>
    /// The loopback port boson allocated for this deployment: the compose file
    /// publishes on it and Caddy dials it. The port the app listens on inside
    /// the container is the repository's business and never reaches boson.
    /// </summary>
    public const string PortVariable = "BOSON_PORT";
    public const string EnvFileVariable = "BOSON_ENV_FILE";

    /// <summary>
    /// The branch this deployment serves, slugged: stable for the life of the
    /// deployment and unique across a repository's branches. A compose file
    /// puts it in any host path it mounts, so two branches never write to one
    /// directory.
    /// </summary>
    public const string BranchSlugVariable = "BOSON_BRANCH_SLUG";

    /// <summary>
    /// The commit this deploy fetched. Named for what it is rather than for
    /// tagging an image with it, which is one use among several: an app that
    /// reports its own version wants the same value.
    /// </summary>
    public const string CommitVariable = "BOSON_COMMIT";

    public Dictionary<string, string> ToEnvironment() => new()
    {
        [PortVariable] = Port.ToString(),
        [EnvFileVariable] = EnvFilePath,
        [CommitVariable] = Commit,
        [BranchSlugVariable] = BranchSlug,
    };
}

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Boson.Deploy;

/// <summary>
/// `_boson.yml`, the file a repository uses to say what it deploys. Read from
/// the checkout on every deploy, so the branch being deployed is the authority
/// on its own hostname and environment set.
/// </summary>
public sealed class BosonFile
{
    public const string FileName = "_boson.yml";

    /// <summary>Bumped when the format changes in a way an older boson would misread.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; }
    public List<DeploymentEntry> Deployments { get; set; } = [];
    public ImagesSection Images { get; set; } = new();

    /// <summary>The entry that governs a branch, or null when none matches.</summary>
    public DeploymentEntry? Match(string branch) =>
        Deployments.FirstOrDefault(d => d.Matches(branch));

    public static BosonFile? Find(string checkoutDir, out string? problem)
    {
        var path = Path.Combine(checkoutDir, FileName);

        if (!File.Exists(path))
        {
            problem = null;
            return null;
        }

        return Parse(File.ReadAllText(path), out problem);
    }

    public static BosonFile? Parse(string yaml, out string? problem)
    {
        BosonFile file;

        try
        {
            file = new DeserializerBuilder()
                       .WithNamingConvention(UnderscoredNamingConvention.Instance)
                       .IgnoreUnmatchedProperties()
                       .Build()
                       .Deserialize<BosonFile>(yaml)
                   ?? new BosonFile();
        }
        catch (Exception e)
        {
            // YamlDotNet's own message names the line and column, which is more
            // use to whoever wrote the file than anything restated here.
            problem = $"{FileName} does not parse: {e.Message}";
            return null;
        }

        problem = Validate(file);

        return problem is null ? file : null;
    }

    private static string? Validate(BosonFile file)
    {
        if (file.Version != CurrentVersion)
            return $"{FileName} declares version {file.Version}; this boson reads version {CurrentVersion}";

        if (file.Deployments.Count == 0)
            return $"{FileName} declares no deployments";

        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in file.Deployments)
        {
            if (string.IsNullOrWhiteSpace(entry.Branch))
                return $"{FileName} has a deployment with no branch";

            if (string.IsNullOrWhiteSpace(entry.Hostname))
                return $"{FileName}: deployment for {entry.Branch} has no hostname";

            // A pattern entry produces one deployment per branch it matches, so
            // its hostname has to vary with the branch or they collide the
            // moment a second branch matches.
            if (entry.IsPattern && !entry.Hostname.Contains(DeploymentEntry.BranchPlaceholder))
                return $"{FileName}: {entry.Branch} matches many branches, so its hostname " +
                       $"needs {DeploymentEntry.BranchPlaceholder} in it";

            if (!entry.IsPattern && entry.Hostname.Contains(DeploymentEntry.BranchPlaceholder))
                return $"{FileName}: {entry.Branch} names one branch, so " +
                       $"{DeploymentEntry.BranchPlaceholder} in its hostname has nothing to vary";

            foreach (var name in entry.AllNames())
            {
                if (claimed.TryGetValue(name, out var owner))
                    return $"{FileName}: {name} is claimed by both {owner} and {entry.Branch}";

                claimed[name] = entry.Branch;
            }
        }

        if (file.Images.Keep < 1)
            return $"{FileName}: images.keep is {file.Images.Keep}, and a deployment needs at least one image";

        return null;
    }
}

public sealed class DeploymentEntry
{
    public const string BranchPlaceholder = "{branch}";

    public string Branch { get; set; } = "";
    public string Hostname { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    /// <summary>Null when the entry names none, which is a project that needs no environment file.</summary>
    public string? Env { get; set; }

    /// <summary>Idle time before the deployment is stopped, e.g. `14d`. Null means it stays.</summary>
    public string? ExpireAfter { get; set; }

    public bool IsPattern => Branch.Contains('*');

    public bool Matches(string branch) =>
        IsPattern
            ? MatchesGlob(Branch, branch)
            : string.Equals(Branch, branch, StringComparison.Ordinal);

    /// <summary>The hostname this entry serves for a branch, with {branch} filled in.</summary>
    public string HostnameFor(string branch) =>
        Hostname.Replace(BranchPlaceholder, BranchLabel.From(branch), StringComparison.Ordinal);

    public IEnumerable<string> AllNames() => new[] { Hostname }.Concat(Aliases);

    /// <summary>
    /// `*` spans one or more characters including `/`, so `feature/*` covers
    /// `feature/a/b`. Anything more than that is a second pattern language for
    /// whoever reads the file to learn.
    /// </summary>
    private static bool MatchesGlob(string pattern, string branch)
    {
        var parts = pattern.Split('*');

        if (parts.Length == 1) return pattern == branch;

        // Each wildcard stands for at least one character, or `feature/*` would
        // match `feature/` with nothing after it and produce a hostname that is
        // just the rest of the template.
        if (branch.Length < parts.Sum(p => p.Length) + parts.Length - 1) return false;

        if (!branch.StartsWith(parts[0], StringComparison.Ordinal)) return false;
        if (!branch.EndsWith(parts[^1], StringComparison.Ordinal)) return false;

        var consumed = parts[0].Length;

        for (var i = 1; i < parts.Length - 1; i++)
        {
            var at = branch.IndexOf(parts[i], consumed, StringComparison.Ordinal);
            if (at < 0) return false;
            consumed = at + parts[i].Length;
        }

        return branch.Length - parts[^1].Length >= consumed;
    }
}

public sealed class ImagesSection
{
    public int Keep { get; set; } = 3;
}

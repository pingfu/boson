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

    /// <summary>
    /// Why this file cannot be served, when any entry spells out the control
    /// hostname as its hostname or an alias. Checked for every entry whichever
    /// branch is deploying, so it is caught when the project is added rather
    /// than when the branch concerned is first pushed.
    /// </summary>
    public string? AdminHostnameProblem(string? adminHostname) =>
        Deployments
            .Select(d => d.AdminHostnameProblem(adminHostname))
            .FirstOrDefault(problem => problem is not null);

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

    private static string NotAHostname(string name, string branch) =>
        $"{FileName}: \"{name}\" is not a hostname boson can serve for {branch}; write a DNS name " +
        "made of letters, digits, '-' and '.', such as example.org";

    private static string? Validate(BosonFile file)
    {
        if (file.Version != CurrentVersion)
            return $"{FileName} declares version {file.Version}; this boson reads version {CurrentVersion}";

        // A key written with nothing after it (`deployments:`, `images:`,
        // `aliases:`) deserialises as null over the property's default, and
        // every line below would otherwise meet it as a null reference.
        file.Deployments ??= [];
        file.Images ??= new ImagesSection();

        if (file.Deployments.Count == 0)
            return $"{FileName} declares no deployments";

        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in file.Deployments)
        {
            if (entry is null)
                return $"{FileName} has an empty entry under deployments";

            if (string.IsNullOrWhiteSpace(entry.Branch))
                return $"{FileName} has a deployment with no branch";

            if (string.IsNullOrWhiteSpace(entry.Hostname))
                return $"{FileName}: deployment for {entry.Branch} has no hostname";

            entry.Aliases ??= [];

            if (entry.Aliases.Any(string.IsNullOrWhiteSpace))
                return $"{FileName}: deployment for {entry.Branch} has an empty alias";

            // Hostnames are case-insensitive, and what is stored here becomes a
            // row in deployment_names, a Caddy matcher and a certificate
            // subject. Normalising once, here, is what keeps Example.org and
            // example.org from being two of anything.
            entry.Branch = entry.Branch.Trim();
            entry.Hostname = entry.Hostname.Trim().ToLowerInvariant();
            entry.Aliases = [.. entry.Aliases.Select(a => a.Trim().ToLowerInvariant())];

            // A pattern entry produces one deployment per branch it matches, so
            // its hostname has to vary with the branch or they collide the
            // moment a second branch matches.
            if (entry.IsPattern && !entry.Hostname.Contains(DeploymentEntry.BranchPlaceholder))
                return $"{FileName}: {entry.Branch} matches many branches, so its hostname " +
                       $"needs {DeploymentEntry.BranchPlaceholder} in it";

            if (!entry.IsPattern && entry.Hostname.Contains(DeploymentEntry.BranchPlaceholder))
                return $"{FileName}: {entry.Branch} names one branch, so " +
                       $"{DeploymentEntry.BranchPlaceholder} in its hostname has nothing to vary";

            // Each name becomes a Caddy host matcher, where `*` is a wildcard
            // and `{...}` a placeholder filled in per request, and aliases are
            // stored comma-separated. Anything but a plain DNS name would let
            // one repository match hostnames it never declared, the control
            // hostname and other projects' among them.
            var served = entry.IsPattern
                ? entry.Hostname.Replace(DeploymentEntry.BranchPlaceholder, "x", StringComparison.Ordinal)
                : entry.Hostname;

            if (!DeploymentEntry.IsHostname(served))
                return NotAHostname(entry.Hostname, entry.Branch);

            if (entry.Aliases.FirstOrDefault(a => !DeploymentEntry.IsHostname(a)) is { } badAlias)
                return NotAHostname(badAlias, entry.Branch);

            foreach (var name in entry.AllNames())
            {
                if (claimed.TryGetValue(name, out var owner))
                    return $"{FileName}: {name} is claimed by both {owner} and {entry.Branch}";

                claimed[name] = entry.Branch;
            }

            // The name becomes the last segment of a path under the project's
            // own environment directory. Anything that could climb out of it
            // (`../other/production`, an absolute path) would hand this
            // repository another project's secrets.
            if (entry.Env is not null && !DeploymentEntry.IsSetName(entry.Env))
                return $"{FileName}: env for {entry.Branch} is \"{entry.Env}\"; an environment set is a file " +
                       "name made of letters, digits, '.', '_' and '-', and does not start with '.'";

            // Refused here rather than ignored at expiry: a typo would
            // otherwise read as a deployment that never expires.
            if (entry.ExpireAfter is not null && ExpiryReaper.ParseDuration(entry.ExpireAfter) is null)
                return $"{FileName}: expire_after for {entry.Branch} is \"{entry.ExpireAfter}\"; write a whole " +
                       "number followed by d, h or m, such as 14d";
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
        Hostname.Replace(BranchPlaceholder, BranchSlug.From(branch), StringComparison.Ordinal);

    public IEnumerable<string> AllNames() => new[] { Hostname }.Concat(Aliases);

    /// <summary>
    /// A DNS name as a certificate authority and a browser would accept it:
    /// dot-separated labels of `a-z0-9-`, none empty, none starting or ending
    /// with `-`. Called after the name has been lowercased.
    /// </summary>
    public static bool IsHostname(string name) =>
        name.Length is > 0 and <= 253 && name.Split('.').All(label =>
            label.Length is > 0 and <= 63
            && label[0] != '-' && label[^1] != '-'
            && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'));

    public static bool IsSetName(string name) =>
        name.Length > 0 && name[0] != '.' && name.All(c =>
            c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-');

    /// <summary>
    /// Why this entry cannot be served for a branch, when one of its names is
    /// the admin hostname. Caddy would route that name to the deployment
    /// for every path outside /_boson/*, and the control plane would be
    /// serving a repository's app.
    /// </summary>
    public string? AdminHostnameProblem(string branch, string? adminHostname) =>
        AdminHostnameProblem(Aliases.Append(HostnameFor(branch)), branch, adminHostname);

    /// <summary>
    /// The same, for the names this entry spells out: its aliases, and its
    /// hostname unless that is a pattern's template, which names nothing until
    /// a branch fills it in.
    /// </summary>
    public string? AdminHostnameProblem(string? adminHostname) =>
        AdminHostnameProblem(IsPattern ? Aliases : Aliases.Append(Hostname), Branch, adminHostname);

    private static string? AdminHostnameProblem(
        IEnumerable<string> names, string branch, string? adminHostname)
    {
        if (string.IsNullOrWhiteSpace(adminHostname)) return null;

        return names.Any(n => string.Equals(n, adminHostname, StringComparison.OrdinalIgnoreCase))
            ? $"{BosonFile.FileName}: {adminHostname} is boson's admin hostname, so {branch} " +
              "cannot use it as a hostname or an alias"
            : null;
    }

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

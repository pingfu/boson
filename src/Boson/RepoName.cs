using System.Text.RegularExpressions;

namespace Boson;

/// <summary>
/// Canonical "org/name" handling. Lowercasing happens once, at CLI argument
/// parse; every layer below receives the canonical form.
/// </summary>
public static partial class RepoName
{
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9\-_.]*[a-z0-9])?/[a-z0-9\-_.]+$")]
    private static partial Regex ValidRepo();

    public static bool TryCanonicalise(string input, out string repo)
    {
        repo = input.Trim().Trim('/').ToLowerInvariant();
        return ValidRepo().IsMatch(repo);
    }

    public static (string Org, string Name) Split(string repo)
    {
        var i = repo.IndexOf('/');
        return (repo[..i], repo[(i + 1)..]);
    }

    /// <summary>
    /// Compose project name: the repo lowercased with every character outside
    /// compose's allowed set [a-z0-9_-] replaced by '-'.
    ///
    /// Passing this explicitly on every invocation is load-bearing. Left to
    /// itself compose derives the project name from the compose file's
    /// directory basename, which two branches of one repository and two orgs'
    /// same-named repositories both share, and colliding projects tear down
    /// each other's containers. The branch label is in it for the same reason.
    /// </summary>
    public static string ComposeProjectName(string repo, string label)
    {
        var chars = $"{repo}-{label}".ToLowerInvariant().Select(c =>
            c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? c : '-');
        return new string(chars.ToArray());
    }
}

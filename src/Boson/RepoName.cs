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
    /// directory basename — the bare repo name — which two orgs' same-named
    /// repos share, and colliding projects tear down each other's containers.
    /// Deriving from org/name keeps them distinct.
    /// </summary>
    public static string ComposeProjectName(string repo)
    {
        var chars = repo.ToLowerInvariant().Select(c =>
            c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? c : '-');
        return new string(chars.ToArray());
    }
}

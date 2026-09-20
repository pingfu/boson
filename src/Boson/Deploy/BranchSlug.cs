using System.Security.Cryptography;
using System.Text;

namespace Boson.Deploy;

/// <summary>
/// A branch name reduced to something safe to put in a hostname, a directory
/// name and a compose project name, so all three say the same thing. The
/// constraint is a DNS label's, the tightest of the three: `a-z0-9-` and 63
/// characters, where git allows nearly anything in a ref, including uppercase,
/// unicode and `/`. The mapping is therefore lossy and two branches can arrive
/// at one slug. The database holds the real branch name, so this never has to
/// be reversed, only be unique and stable.
/// </summary>
public static class BranchSlug
{
    public const int MaxLength = 63;

    /// <summary>Allow-list rather than escape-list: a branch name is remote input that becomes a path.</summary>
    public static string From(string branch)
    {
        var slug = new StringBuilder(branch.Length);

        foreach (var c in branch.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        var text = slug.ToString().Trim('-');

        if (text.Length == 0) return Suffix(branch);

        // Truncating can collide two long branches, and so can the allow-list
        // itself, so the hash of the true name goes on whenever the slug is
        // not already the branch it came from.
        if (text.Length > MaxLength)
            text = text[..(MaxLength - 7)].TrimEnd('-') + "-" + Suffix(branch);
        else if (text != branch)
            text = Truncate(text, MaxLength - 7).TrimEnd('-') + "-" + Suffix(branch);

        return text;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Suffix(string branch) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(branch)))[..6].ToLowerInvariant();
}

using System.Security.Cryptography;
using System.Text;

namespace Boson.Deploy;

/// <summary>
/// Turns a branch name into the label used for its hostname and its checkout
/// directory. Git allows nearly anything in a ref, including uppercase,
/// unicode and `/`, while a DNS label allows `a-z0-9-` and 63 characters, so
/// the mapping is lossy and two branches can arrive at one label. The database
/// holds the real branch name, so this never has to be reversed, only be
/// unique and stable.
/// </summary>
public static class BranchLabel
{
    public const int MaxLength = 63;

    /// <summary>Allow-list rather than escape-list: a branch name is remote input that becomes a path.</summary>
    public static string From(string branch)
    {
        var label = new StringBuilder(branch.Length);

        foreach (var c in branch.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                label.Append(c);
            else if (label.Length > 0 && label[^1] != '-')
                label.Append('-');
        }

        var text = label.ToString().Trim('-');

        if (text.Length == 0) return Suffix(branch);

        // Truncating can collide two long branches, and so can the allow-list
        // itself, so the hash of the true name goes on whenever the label is
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

using System.Text;
using System.Text.RegularExpressions;

namespace VModLauncher.DevKit;

/// <summary>
/// '/'-separated glob patterns, case-insensitive: <c>*</c> is any run of characters in one path segment, <c>?</c>
/// one character, <c>**</c> any number of segments (none included), so <c>**/Save/**</c> matches "Save/a.txt"
/// and "x/Save/y/b.txt".
/// </summary>
public sealed class Glob
{
    readonly Regex regex;
    public string Pattern { get; }

    public Glob(string pattern)
    {
        Pattern = pattern.Replace('\\', '/').Trim();
        var p = Pattern.TrimStart('/');
        var sb = new StringBuilder("^");
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                bool slashAfter = i + 2 < p.Length && p[i + 2] == '/';
                sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else if (c == '/' && p.AsSpan(i).StartsWith("/**") && i + 3 == p.Length) { sb.Append("(?:/.*)?"); i += 2; }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public bool IsMatch(string relativePath) => regex.IsMatch(relativePath.Replace('\\', '/').TrimStart('/'));

    public static bool Any(IEnumerable<Glob> globs, string relativePath) => globs.Any(g => g.IsMatch(relativePath));
}

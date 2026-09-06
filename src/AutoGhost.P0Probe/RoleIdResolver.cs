using System.Text.RegularExpressions;

namespace AutoGhost.P0Probe;

/// <summary>
/// P0 deliberately treats the title bar as a hypothesis. Without an explicit
/// configured regex, the raw title is evidence only and never becomes identity.
/// </summary>
public sealed class RoleIdResolver
{
    private readonly Regex? _regex;

    public RoleIdResolver(string? roleIdRegex)
    {
        if (!string.IsNullOrWhiteSpace(roleIdRegex))
        {
            _regex = new Regex(roleIdRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
    }

    public string? Resolve(string title)
    {
        if (_regex is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        Match match;
        try
        {
            match = _regex.Match(title);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["roleId"].Success
            ? match.Groups["roleId"].Value
            : match.Groups.Count > 1
                ? match.Groups[1].Value
                : match.Value;

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }
}

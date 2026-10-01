using System.Text.RegularExpressions;

namespace QT_MNQ_Orderflow_Algo.Compliance;

public static class SymbolRules
{
    // Root, then futures month code (F G H J K M N Q U V X Z), then 1-2 year digits.
    private static readonly Regex Contract = new(@"^(?<root>[A-Z]+?)(?<month>[FGHJKMNQUVXZ])(?<year>\d{1,2})$", RegexOptions.Compiled);

    public static string ExtractRoot(string symbolName)
    {
        var s = symbolName.Trim().TrimStart('/').ToUpperInvariant();
        var m = Contract.Match(s);
        return m.Success ? m.Groups["root"].Value : s;
    }

    public static IReadOnlySet<string> ParseAllowed(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(r => r.ToUpperInvariant()).ToHashSet();

    public static bool IsAllowed(string root, IReadOnlySet<string> allowed) => allowed.Contains(root.ToUpperInvariant());
}

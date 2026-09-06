using System.Text;

namespace SMLoader.Core;

/// <summary>Builds the boxed startup splash shown in the game console.</summary>
internal static class Banner
{
    private const int Width = 62;
    private const char Edge = '#';

    public static List<string> Build(string headline, IEnumerable<KeyValuePair<string, string>> facts)
    {
        var lines = new List<string>
        {
            new string(Edge, Width),
            Row(string.Empty),
            Row("  S M L O A D E R"),
            Row("  " + headline),
            Row(string.Empty),
        };

        foreach ((string key, string value) in facts)
            lines.Add(Row($"    {key,-9} {value}"));

        lines.Add(Row(string.Empty));
        lines.Add(new string(Edge, Width));
        return lines;
    }

    /// <summary>One framed row: '#' + padded content + '#'.</summary>
    private static string Row(string content)
    {
        int inner = Width - 2;
        if (content.Length > inner)
            content = content[..(inner - 1)] + "…";

        var builder = new StringBuilder(Width);
        builder.Append(Edge);
        builder.Append(content.PadRight(inner));
        builder.Append(Edge);
        return builder.ToString();
    }
}

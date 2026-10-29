using EnvDeck.Core;

namespace EnvDeck.Cli;

static class Out
{
    static readonly bool Color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Write(string text, ConsoleColor color)
    {
        if (!Color)
        {
            Console.Write(text);
            return;
        }
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }

    public static void Line(string text, ConsoleColor color)
    {
        Write(text, color);
        Console.WriteLine();
    }

    public static void Success(string text) => Line(text, ConsoleColor.Green);

    public static void Dim(string text) => Line(text, ConsoleColor.DarkGray);

    public static void Warn(string text) => Line("warning: " + text, ConsoleColor.Yellow);

    public static void Error(string text)
    {
        if (Color) Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("error: " + text);
        if (Color) Console.ResetColor();
    }

    public static void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, int maxLastColumn = 80)
    {
        var data = rows.ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, data.Select(r => r[i].Length).DefaultIfEmpty(0).Max())).ToArray();
        widths[^1] = Math.Min(widths[^1], maxLastColumn);

        Dim(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (var row in data)
        {
            var cells = row.Select((c, i) => i == row.Count - 1 ? Truncate(c, widths[i]) : c.PadRight(widths[i]));
            Line(string.Join("  ", cells).TrimEnd());
        }
    }

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    public static void Changes(IEnumerable<PlannedChange> changes)
    {
        var any = false;
        foreach (var c in changes)
        {
            any = true;
            var key = $"{c.Scope}\\{c.Name}";
            switch (c.Action)
            {
                case ChangeAction.Create:
                    Line($"  + {key} = {c.After}", ConsoleColor.Green);
                    break;
                case ChangeAction.Delete:
                    Line($"  - {key}", ConsoleColor.Red);
                    break;
                case ChangeAction.Update:
                    Line($"  ~ {key}", ConsoleColor.Yellow);
                    var before = PathList.Split(c.Before);
                    var after = PathList.Split(c.After);
                    if (before.Count > 1 || after.Count > 1)
                    {
                        foreach (var e in after.Where(e => !PathList.Contains(before, e))) Line($"      + {e}", ConsoleColor.Green);
                        foreach (var e in before.Where(e => !PathList.Contains(after, e))) Line($"      - {e}", ConsoleColor.Red);
                        if (after.Count == before.Count && after.All(e => PathList.Contains(before, e))) Dim("      (order changed)");
                    }
                    else
                    {
                        Line($"      {c.Before}  →  {c.After}");
                    }
                    break;
            }
        }
        if (!any) Dim("  (no changes)");
    }
}

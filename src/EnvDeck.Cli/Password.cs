namespace EnvDeck.Cli;

/// <summary>Reads a password from the terminal without echoing it.</summary>
static class Password
{
    public static string Ask(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return text.ToString();
    }
}

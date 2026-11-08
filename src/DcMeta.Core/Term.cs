namespace DcMeta.Core;

/// <summary>Colored console output shared by both executables.</summary>
public static class Term
{
    public static void Info(string text) => Line(Console.Out, ConsoleColor.Cyan, text);
    public static void Ok(string text) => Line(Console.Out, ConsoleColor.Green, text);
    public static void Warn(string text) => Line(Console.Out, ConsoleColor.Yellow, text);
    public static void Dim(string text) => Line(Console.Out, ConsoleColor.DarkGray, text);
    public static void Error(string text) => Line(Console.Error, ConsoleColor.Red, text);

    static void Line(TextWriter w, ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        w.WriteLine(text);
        Console.ResetColor();
    }

    /// <summary>Redraws a progress bar in place. Skipped when output is redirected, so logs stay clean.</summary>
    public static void Progress(string label, int done, int total)
    {
        if (Console.IsOutputRedirected) return;
        const int width = 30;
        int filled = total == 0 ? width : Math.Min(width, done * width / total);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"\r  {label} [{new string('#', filled)}{new string('.', width - filled)}] {done}/{total} ");
        Console.ResetColor();
    }

    public static void ProgressEnd()
    {
        if (!Console.IsOutputRedirected) Console.WriteLine();
    }
}

namespace DcMeta.Core;

/// <summary>Tiny "--key value" / "--flag" argument parser shared by both executables.</summary>
public static class Cli
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string key = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            d[key] = hasValue ? args[++i] : "true";
        }
        return d;
    }
}

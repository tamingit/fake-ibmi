using System.Collections;

namespace FakeIbmI;

/// <summary>
/// Settings from a .env file and the environment. The file is read as Docker's --env-file reads
/// it, so the same file works for both: KEY=VALUE lines, blank lines and # comments, and a value
/// taken as written (quotes included). A variable set in the environment wins over the file.
/// </summary>
public static class Settings
{
    public static Dictionary<string, string> Load(string? envFile, IDictionary environment)
    {
        var settings = envFile is null ? [] : Parse(File.ReadLines(envFile), envFile);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && entry.Value is string value) settings[key] = value;
        }
        return settings;
    }

    public static Dictionary<string, string> Parse(IEnumerable<string> lines, string source = ".env")
    {
        var settings = new Dictionary<string, string>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            // A bare name passes the host's value through in Docker; here the environment does that anyway.
            if (equals < 0) continue;
            var key = line[..equals].Trim();
            if (key.Length == 0 || key.Any(char.IsWhiteSpace))
                throw new ArgumentException($"{source} line {number}: expected NAME=value");
            settings[key] = line[(equals + 1)..];
        }
        return settings;
    }
}

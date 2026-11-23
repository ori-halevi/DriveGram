namespace DriveGram.Host;

/// <summary>Reads KEY=value lines of a .env file: keys in upper case (api_id = API_ID), comments (#) and
/// "export " skipped, quotes and a trailing comment removed. The last value of a key wins.</summary>
public static class EnvFile
{
    public static IEnumerable<(string Key, string Value)> Read(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase)) line = line[7..].TrimStart();
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || kv[0].Length == 0) continue;
            var value = kv[1];
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var c and >= 0) value = value[..c].TrimEnd();
            yield return (kv[0].ToUpperInvariant(), value.Trim());
        }
    }
}

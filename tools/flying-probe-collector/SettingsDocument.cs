using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal sealed class SettingsDocument
{
    private readonly string path;
    private readonly List<string> lines;
    private readonly Dictionary<string, string> values;

    private SettingsDocument(string path, List<string> lines)
    {
        this.path = Path.GetFullPath(path);
        this.lines = lines;
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string original in lines)
        {
            string line = original.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
            int separator = line.IndexOf('=');
            if (separator > 0) values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }
    }

    public static SettingsDocument Load(string path)
    {
        return new SettingsDocument(path, File.Exists(path)
            ? new List<string>(File.ReadAllLines(path, Encoding.UTF8)) : new List<string>());
    }

    public string Get(string key, string fallback)
    {
        string value;
        return values.TryGetValue(key, out value) ? value : fallback;
    }

    public void Set(string key, string value)
    {
        if (key.IndexOfAny(new char[] { '\r', '\n', '=' }) >= 0 || value.IndexOfAny(new char[] { '\r', '\n' }) >= 0)
            throw new ArgumentException("INI keys and values must be single-line.");
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            string line = lines[index].Trim();
            int separator = line.IndexOf('=');
            if (separator > 0 && string.Equals(line.Substring(0, separator).Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = key + "=" + value;
                values[key] = value;
                return;
            }
        }
        lines.Add(key + "=" + value);
        values[key] = value;
    }

    public Settings Validate()
    {
        return Settings.FromValues(path, values);
    }

    public void Save()
    {
        Validate();
        AtomicJson.Write(path, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PerfWidget;

/// <summary>
/// Turns a raw command line into something a human recognises ("gpu-process", "Orbit/server.js").
/// Platforms hand in one string: Windows' native command line, or argv joined with spaces on macOS/Linux
/// (arguments containing whitespace wrapped in double quotes).
/// </summary>
public static class CmdLine
{
    static readonly string[] ScriptExts = { ".js", ".mjs", ".cjs", ".ts", ".py", ".pyw", ".jar", ".ps1", ".bat", ".cmd", ".rb", ".lua", ".sh" };

    public static List<string> Split(string cmd)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in cmd)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { args.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) args.Add(sb.ToString());
        return args;
    }

    /// <summary>Joins argv the way <see cref="Split"/> reads it back.</summary>
    public static string Join(IEnumerable<string> argv) =>
        string.Join(" ", argv.Select(a => a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a));

    static bool IsScript(string a) =>
        ScriptExts.Any(e => a.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && !a.StartsWith("-");

    /// <summary>"@scope/pkg" for anything under node_modules, else "folder/file.ext".</summary>
    public static string ScriptLabel(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int nm = Array.FindLastIndex(parts, p => p.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
        if (nm >= 0 && nm + 1 < parts.Length)
        {
            var pkg = parts[nm + 1];
            if (pkg.StartsWith('@') && nm + 2 < parts.Length) pkg += "/" + parts[nm + 2];
            return pkg;
        }
        return parts.Length >= 2 ? parts[^2] + "/" + parts[^1] : parts.LastOrDefault() ?? path;
    }

    /// <summary>The script a runtime host (node/python/…) is running, if any.</summary>
    public static string? Script(string? cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return null;
        var args = Split(cmd).Skip(1).ToList();
        var s = args.FirstOrDefault(IsScript);
        if (s != null) return ScriptLabel(s);
        int m = args.IndexOf("-m");
        if (m >= 0 && m + 1 < args.Count) return "-m " + args[m + 1];
        return null;
    }

    public static string Summarize(string? cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return "";
        var args = Split(cmd).Skip(1).ToList();
        var type = args.FirstOrDefault(a => a.StartsWith("--type=", StringComparison.Ordinal));
        if (type != null)
        {
            var t = type[7..];
            var sub = args.FirstOrDefault(a => a.StartsWith("--utility-sub-type=", StringComparison.Ordinal));
            if (sub != null) t += ": " + sub[19..].Split('.').Last();
            return t;
        }
        var script = Script(cmd);
        if (script != null) return script;
        var interesting = args.Where(a => a.Length > 0).Take(3)
            .Select(a => a.Contains('\\') || a.Contains('/') ? Path.GetFileName(a.Replace('\\', '/').TrimEnd('/')) : a);
        var s = string.Join(" ", interesting);
        return s.Length > 60 ? s[..60] + "…" : s;
    }
}

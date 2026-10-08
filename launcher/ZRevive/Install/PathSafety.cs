using System.IO;

namespace ZRevive.Launcher.Install;

/// <summary>
/// Manifest entries come off the network and are treated as hostile input: an entry
/// must only ever be able to write to a path *inside* the ZRevive install folder.
///
/// Everything here is pure (no I/O except the optional reparse-point walk), so it is
/// covered by ZRevive.Tests.
/// </summary>
public static class PathSafety
{
    // Windows device names. "nul", "con.txt" etc. resolve to devices, not files.
    static readonly string[] Devices =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Validates a manifest-supplied relative path and resolves it under <paramref name="root"/>.
    /// Returns false with a human-readable reason for anything that could escape the root.
    /// </summary>
    public static bool TryResolve(string root, string? relative, out string fullPath, out string? error)
    {
        fullPath = "";
        error = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            error = "no install folder set";
            return false;
        }
        if (string.IsNullOrWhiteSpace(relative))
        {
            error = "empty target path";
            return false;
        }

        var rel = relative!;

        // No NUL / control characters, no wildcards, no alternate data streams.
        foreach (var c in rel)
        {
            if (char.IsControl(c)) { error = "control character in path"; return false; }
            if (c == '*' || c == '?' || c == '|' || c == '<' || c == '>' || c == '"')
            {
                error = $"illegal character '{c}' in path";
                return false;
            }
        }
        if (rel.Contains(':')) { error = "drive letter or data stream in path"; return false; }

        // Reject anything rooted or UNC before normalising.
        if (rel.StartsWith('/') || rel.StartsWith('\\'))
        {
            error = "absolute path not allowed";
            return false;
        }
        if (Path.IsPathRooted(rel) || Path.IsPathFullyQualified(rel))
        {
            error = "absolute path not allowed";
            return false;
        }

        var segments = rel.Split('/', '\\');
        foreach (var raw in segments)
        {
            if (raw.Length == 0)
            {
                error = "empty path segment";
                return false;
            }
            if (raw == "." || raw == "..")
            {
                error = "relative path segment ('..') not allowed";
                return false;
            }
            // "a.. " and "foo." normalise away on Windows and can shift the target.
            if (raw.TrimEnd('.', ' ') != raw)
            {
                error = "path segment ends with a dot or space";
                return false;
            }
            var stem = raw;
            var dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem[..dot];
            if (Array.Exists(Devices, d => string.Equals(d, stem, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"reserved device name '{stem}'";
                return false;
            }
        }

        string rootFull, candidate;
        try
        {
            rootFull = Path.GetFullPath(root);
            candidate = Path.GetFullPath(Path.Combine(rootFull, rel));
        }
        catch (Exception ex)
        {
            error = "unusable path: " + ex.Message;
            return false;
        }

        // Belt and braces: the normalised result must still live under the root.
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "path escapes the install folder";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Filesystem half of the check: refuse to write through a symlink or junction, which
    /// would otherwise land outside the install folder even for a clean relative path.
    /// Walks from the resolved path up to the root.
    /// </summary>
    public static bool HasReparsePointOnPath(string root, string fullPath)
    {
        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var cur = Path.GetFullPath(fullPath);
            while (true)
            {
                var info = File.Exists(cur) ? new FileInfo(cur) : (FileSystemInfo)new DirectoryInfo(cur);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
                var parent = Path.GetDirectoryName(cur);
                if (parent == null) return false;
                cur = parent.TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(cur, rootFull, StringComparison.OrdinalIgnoreCase)) return false;
                if (cur.Length <= rootFull.Length) return false;
            }
        }
        catch
        {
            return true; // can't tell -> treat as unsafe
        }
    }

    /// <summary>True when <paramref name="inner"/> is the same folder as, or inside, <paramref name="outer"/>.</summary>
    public static bool IsSameOrInside(string outer, string inner)
    {
        try
        {
            var a = Path.GetFullPath(outer).TrimEnd(Path.DirectorySeparatorChar);
            var b = Path.GetFullPath(inner).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Turns user- or YouTube-supplied names (user names, channel names, video
/// titles) into single, safe path segments, and checks that a computed path
/// really stays inside the folder it's meant to live in. Without this a name
/// like ".." resolved to the PARENT folder, and the retention/cleanup passes
/// (which delete whole directories) then ran against the library root itself.
/// </summary>
internal static class PathSafety
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// <paramref name="name"/> as one folder/file name: separators and
    /// invalid characters replaced, trimmed. Empty or dot-only results ("",
    /// ".", "..", "...") become <paramref name="fallback"/>.
    /// </summary>
    public static string Segment(string? name, string fallback)
    {
        name ??= string.Empty;
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        name = name.Replace(':', '_').Replace('/', '_').Replace('\\', '_').Trim();
        return name.Length == 0 || name.All(c => c == '.') ? fallback : name;
    }

    /// <summary>True if <paramref name="child"/> is strictly below <paramref name="parent"/> (not equal to it, not outside it).</summary>
    public static bool IsStrictlyInside(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        return c.Length > p.Length && c.StartsWith(p + Path.DirectorySeparatorChar, PathComparison);
    }

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

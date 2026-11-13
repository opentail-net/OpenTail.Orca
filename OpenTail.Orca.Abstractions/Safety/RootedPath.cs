namespace OpenTail.Orca.Abstractions;

/// <summary>
/// Confines file access to one folder. Rejects absolute paths, drive letters, <c>..</c> escapes and any existing
/// symlink or junction between the root and the target. Shared by every module that touches the file system so the
/// check exists exactly once.
/// </summary>
public sealed class RootedPath
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public RootedPath(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException($"Root folder does not exist: {Root}");
    }

    public string Root { get; }

    /// <summary>Full path for a root-relative path (empty means the root). Throws <see cref="UnauthorizedAccessException"/> if it would leave the root.</summary>
    public string Resolve(string? relative)
    {
        var full = Combine(relative);
        EnsureNoReparsePoints(full);
        return full;
    }

    public string Relative(string full) =>
        full.Length <= Root.Length ? "" : full[(Root.Length + 1)..].Replace('\\', '/');

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; } // cannot tell, so do not trust it
    }

    private string Combine(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return Root;
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new UnauthorizedAccessException("Paths must be relative to the root.");
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.Equals(Root, Comparison) && !full.StartsWith(Root + Path.DirectorySeparatorChar, Comparison))
            throw new UnauthorizedAccessException("Path is outside the allowed root.");
        return full;
    }

    private void EnsureNoReparsePoints(string full)
    {
        var current = full;
        while (current.Length > Root.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                throw new UnauthorizedAccessException("Links are not followed.");
            current = Path.GetDirectoryName(current)!;
        }
    }
}

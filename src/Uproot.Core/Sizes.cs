namespace Uproot.Core;

/// <summary>Disk sizes as a person reads them.</summary>
public static class Sizes
{
    /// <summary>"1.4 GB", "230 MB", "12 KB", "512 B".</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B"
    };
}

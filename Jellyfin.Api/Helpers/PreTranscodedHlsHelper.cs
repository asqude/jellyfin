using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Helper class to detect and work with pre-transcoded HLS format.
/// </summary>
public class PreTranscodedHlsHelper
{
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreTranscodedHlsHelper"/> class.
    /// </summary>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    public PreTranscodedHlsHelper(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Checks if a video item has a pre-transcoded HLS format (master.m3u8 in the folder).
    /// </summary>
    /// <param name="item">The video item to check.</param>
    /// <returns>True if pre-transcoded format is detected, false otherwise.</returns>
    public bool HasPreTranscodedHls(Video item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        // Get the directory containing the video file
        var videoDirectory = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrWhiteSpace(videoDirectory) || !_fileSystem.DirectoryExists(videoDirectory))
        {
            return false;
        }

        // Check if master.m3u8 exists in the same directory as the video file
        var masterPlaylistPath = Path.Combine(videoDirectory, "master.m3u8");
        return _fileSystem.FileExists(masterPlaylistPath);
    }

    /// <summary>
    /// Gets the path to the master.m3u8 file for a pre-transcoded video.
    /// </summary>
    /// <param name="item">The video item.</param>
    /// <returns>The path to master.m3u8, or null if not found.</returns>
    public string? GetMasterPlaylistPath(Video item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        var videoDirectory = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrWhiteSpace(videoDirectory))
        {
            return null;
        }

        var masterPlaylistPath = Path.Combine(videoDirectory, "master.m3u8");
        return _fileSystem.FileExists(masterPlaylistPath) ? masterPlaylistPath : null;
    }

    /// <summary>
    /// Gets the base directory path for a pre-transcoded video (where master.m3u8 is located).
    /// </summary>
    /// <param name="item">The video item.</param>
    /// <returns>The base directory path, or null if not found.</returns>
    public string? GetPreTranscodedBasePath(Video item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        var videoDirectory = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrWhiteSpace(videoDirectory))
        {
            return null;
        }

        var masterPlaylistPath = Path.Combine(videoDirectory, "master.m3u8");
        return _fileSystem.FileExists(masterPlaylistPath) ? videoDirectory : null;
    }

    /// <summary>
    /// Gets the path to a segment file within the pre-transcoded structure.
    /// </summary>
    /// <param name="basePath">The base directory containing master.m3u8.</param>
    /// <param name="segmentPath">The relative segment path from the m3u8 playlist.</param>
    /// <returns>The full path to the segment file, or null if not found.</returns>
    public string? GetSegmentPath(string basePath, string segmentPath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(segmentPath))
        {
            return null;
        }

        // Handle both relative paths (e.g., "audio_de/seg_000.m4s") and absolute paths
        var fullPath = Path.IsPathRooted(segmentPath)
            ? segmentPath
            : Path.Combine(basePath, segmentPath);

        // Normalize the path
        fullPath = Path.GetFullPath(fullPath);

        // Security check: ensure the path is within the base directory
        var baseFullPath = Path.GetFullPath(basePath);
        if (!fullPath.StartsWith(baseFullPath, System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _fileSystem.FileExists(fullPath) ? fullPath : null;
    }
}

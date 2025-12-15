using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Chapters;

/// <summary>
/// The chapter manager.
/// </summary>
public class ChapterManager : IChapterManager
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ChapterManager> _logger;
    private readonly IMediaEncoder _encoder;
    private readonly IChapterRepository _chapterRepository;
    private readonly ILibraryManager _libraryManager;
    private readonly IPathManager _pathManager;
    private readonly PreTranscodedHlsHelper? _preTranscodedHlsHelper;
    private readonly IImageProcessor? _imageProcessor;

    /// <summary>
    /// The first chapter ticks.
    /// </summary>
    private static readonly long _firstChapterTicks = TimeSpan.FromSeconds(15).Ticks;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterManager"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{ChapterManager}"/>.</param>
    /// <param name="fileSystem">The <see cref="IFileSystem"/>.</param>
    /// <param name="encoder">The <see cref="IMediaEncoder"/>.</param>
    /// <param name="chapterRepository">The <see cref="IChapterRepository"/>.</param>
    /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
    /// <param name="pathManager">The <see cref="IPathManager"/>.</param>
    /// <param name="preTranscodedHlsHelper">Optional pre-transcoded HLS helper.</param>
    /// <param name="imageProcessor">Optional image processor for cache tags.</param>
    public ChapterManager(
        ILogger<ChapterManager> logger,
        IFileSystem fileSystem,
        IMediaEncoder encoder,
        IChapterRepository chapterRepository,
        ILibraryManager libraryManager,
        IPathManager pathManager,
        PreTranscodedHlsHelper? preTranscodedHlsHelper = null,
        IImageProcessor? imageProcessor = null)
    {
        _logger = logger;
        _fileSystem = fileSystem;
        _encoder = encoder;
        _chapterRepository = chapterRepository;
        _libraryManager = libraryManager;
        _pathManager = pathManager;
        _preTranscodedHlsHelper = preTranscodedHlsHelper;
        _imageProcessor = imageProcessor;
    }

    /// <summary>
    /// Determines whether [is eligible for chapter image extraction] [the specified video].
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="libraryOptions">The library options for the video.</param>
    /// <returns><c>true</c> if [is eligible for chapter image extraction] [the specified video]; otherwise, <c>false</c>.</returns>
    private bool IsEligibleForChapterImageExtraction(Video video, LibraryOptions libraryOptions)
    {
        if (video.IsPlaceHolder)
        {
            return false;
        }

        if (libraryOptions is null || !libraryOptions.EnableChapterImageExtraction)
        {
            return false;
        }

        if (video.IsShortcut)
        {
            return false;
        }

        if (!video.IsCompleteMedia)
        {
            return false;
        }

        // Can't extract images if there are no video streams
        return video.DefaultVideoStreamIndex.HasValue;
    }

    private long GetAverageDurationBetweenChapters(IReadOnlyList<ChapterInfo> chapters)
    {
        if (chapters.Count < 2)
        {
            return 0;
        }

        long sum = 0;
        for (int i = 1; i < chapters.Count; i++)
        {
            sum += chapters[i].StartPositionTicks - chapters[i - 1].StartPositionTicks;
        }

        return sum / chapters.Count;
    }

    /// <inheritdoc />
    public async Task<bool> RefreshChapterImages(Video video, IDirectoryService directoryService, IReadOnlyList<ChapterInfo> chapters, bool extractImages, bool saveChapters, CancellationToken cancellationToken)
    {
        if (chapters.Count == 0)
        {
            return true;
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(video);

        // Skip extraction for pre-transcoded videos (dummy file)
        if (_preTranscodedHlsHelper is not null && _preTranscodedHlsHelper.HasPreTranscodedHls(video))
        {
            _logger.LogInformation("Skipping chapter image extraction for {Video} because pre-transcoded HLS format is detected. Chapter images should be extracted during pre-transcoding.", video.Name);
            extractImages = false;
        }

        if (!IsEligibleForChapterImageExtraction(video, libraryOptions))
        {
            extractImages = false;
        }

        var averageChapterDuration = GetAverageDurationBetweenChapters(chapters);
        var threshold = TimeSpan.FromSeconds(1).Ticks;
        if (averageChapterDuration < threshold)
        {
            _logger.LogInformation("Skipping chapter image extraction for {Video} as the average chapter duration {AverageDuration} was lower than the minimum threshold {Threshold}", video.Name, averageChapterDuration, threshold);
            extractImages = false;
        }

        var success = true;
        var changesMade = false;

        var runtimeTicks = video.RunTimeTicks ?? 0;

        var currentImages = GetSavedChapterImages(video, directoryService);

        foreach (var chapter in chapters)
        {
            if (chapter.StartPositionTicks >= runtimeTicks)
            {
                _logger.LogInformation("Stopping chapter extraction for {0} because a chapter was found with a position greater than the runtime.", video.Name);
                break;
            }

            var path = _pathManager.GetChapterImagePath(video, chapter.StartPositionTicks);

            if (!currentImages.Contains(path, StringComparison.OrdinalIgnoreCase))
            {
                if (extractImages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        // Add some time for the first chapter to make sure we don't end up with a black image
                        var time = chapter.StartPositionTicks == 0 ? TimeSpan.FromTicks(Math.Min(_firstChapterTicks, video.RunTimeTicks ?? 0)) : TimeSpan.FromTicks(chapter.StartPositionTicks);

                        var inputPath = video.Path;
                        var directoryPath = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(directoryPath))
                        {
                            Directory.CreateDirectory(directoryPath);
                        }

                        var container = video.Container;
                        var mediaSource = new MediaSourceInfo
                        {
                            VideoType = video.VideoType,
                            IsoType = video.IsoType,
                            Protocol = video.PathProtocol ?? MediaProtocol.File,
                        };

                        _logger.LogInformation("Extracting chapter image for {Name} at {Path}", video.Name, inputPath);
                        var tempFile = await _encoder.ExtractVideoImage(inputPath, container, mediaSource, video.GetDefaultVideoStream(), video.Video3DFormat, time, cancellationToken).ConfigureAwait(false);
                        File.Copy(tempFile, path, true);

                        try
                        {
                            _fileSystem.DeleteFile(tempFile);
                        }
                        catch (IOException ex)
                        {
                            _logger.LogError(ex, "Error deleting temporary chapter image encoding file {Path}", tempFile);
                        }

                        chapter.ImagePath = path;
                        chapter.ImageDateModified = _fileSystem.GetLastWriteTimeUtc(path);
                        changesMade = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error extracting chapter images for {0}", string.Join(',', video.Path));
                        success = false;
                        break;
                    }
                }
                else if (!string.IsNullOrEmpty(chapter.ImagePath))
                {
                    chapter.ImagePath = null;
                    changesMade = true;
                }
            }
            else if (!string.Equals(path, chapter.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                chapter.ImagePath = path;
                chapter.ImageDateModified = _fileSystem.GetLastWriteTimeUtc(path);
                changesMade = true;
            }
            else if (libraryOptions?.EnableChapterImageExtraction != true)
            {
                // We have an image for the current chapter but the user has disabled chapter image extraction -> delete this chapter's image
                chapter.ImagePath = null;
                changesMade = true;
            }
        }

        if (saveChapters && changesMade)
        {
            SaveChapters(video, chapters);
        }

        DeleteDeadImages(currentImages, chapters);

        return success;
    }

    /// <inheritdoc />
    public void SaveChapters(Video video, IReadOnlyList<ChapterInfo> chapters)
    {
        // Remove any chapters that are outside of the runtime of the video
        var validChapters = chapters.Where(c => c.StartPositionTicks < video.RunTimeTicks).ToList();
        _chapterRepository.SaveChapters(video.Id, validChapters);
    }

    /// <inheritdoc />
    public ChapterInfo? GetChapter(Guid baseItemId, int index)
    {
        // Check for pre-transcoded format first
        var video = _libraryManager.GetItemById<Video>(baseItemId);
        if (video is not null)
        {
            var preTranscodedChapters = GetPreTranscodedChapters(video);
            if (preTranscodedChapters is not null && index >= 0 && index < preTranscodedChapters.Count)
            {
                _logger.LogInformation(
                    "Returning pre-transcoded chapter {Index} for {Video} (Position={PositionTicks}, Tag={Tag})",
                    index,
                    video.Name,
                    preTranscodedChapters[index].StartPositionTicks,
                    preTranscodedChapters[index].ImageTag);
                return preTranscodedChapters[index];
            }
        }
        else
        {
            _logger.LogWarning("Video not found for baseItemId={BaseItemId}", baseItemId);
        }

        return _chapterRepository.GetChapter(baseItemId, index);
    }

    /// <inheritdoc />
    public IReadOnlyList<ChapterInfo> GetChapters(Guid baseItemId)
    {
        // Check for pre-transcoded format first
        var video = _libraryManager.GetItemById<Video>(baseItemId);
        if (video is not null)
        {
            var preTranscodedChapters = GetPreTranscodedChapters(video);
            if (preTranscodedChapters is not null)
            {
                return preTranscodedChapters;
            }
        }

        return _chapterRepository.GetChapters(baseItemId);
    }

    /// <summary>
    /// Gets chapters from pre-transcoded format by scanning the chapters folder.
    /// Results are cached to avoid repeated file system scans.
    /// </summary>
    /// <param name="video">The video item.</param>
    /// <returns>List of chapters if pre-transcoded format detected and chapters folder exists, null otherwise.</returns>
    private IReadOnlyList<ChapterInfo>? GetPreTranscodedChapters(Video video)
    {
        if (_preTranscodedHlsHelper is null || !_preTranscodedHlsHelper.HasPreTranscodedHls(video))
        {
            return null;
        }

        var basePath = _preTranscodedHlsHelper.GetPreTranscodedBasePath(video);
        if (basePath is null)
        {
            return null;
        }

        var chaptersDir = Path.Combine(basePath, "chapters");
        if (!_fileSystem.DirectoryExists(chaptersDir))
        {
            return null;
        }

        try
        {
            var chapterFiles = _fileSystem.GetFiles(chaptersDir, false)
                .Where(f => f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (chapterFiles.Count == 0)
            {
                return null;
            }

            var chapters = new List<ChapterInfo>();

            foreach (var file in chapterFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file.Name);
                long? chapterPositionTicks = null;

                // Try to parse as simple format: {ChapterPositionTicks}.jpg
                if (long.TryParse(fileName, out var ticks))
                {
                    chapterPositionTicks = ticks;
                }
                else
                {
                    // Try to parse as standard format: {DateModifiedTicks}_{ChapterPositionTicks}.jpg
                    var parts = fileName.Split('_', 2);
                    if (parts.Length == 2 && long.TryParse(parts[1], out ticks))
                    {
                        chapterPositionTicks = ticks;
                    }
                }

                if (chapterPositionTicks.HasValue)
                {
                    // Normalize path to ensure consistent comparisons
                    var fullPath = Path.GetFullPath(file.FullName);
                    var imageDateModified = _fileSystem.GetLastWriteTimeUtc(fullPath);
                    var chapter = new ChapterInfo
                    {
                        StartPositionTicks = chapterPositionTicks.Value,
                        ImagePath = fullPath,
                        ImageDateModified = imageDateModified
                        // Name will be set after sorting
                    };

                    // Set ImageTag for cache invalidation - always set it for pre-transcoded chapters
                    // Use a stable hash based on the file path and chapter position to ensure the tag never changes
                    // This prevents cache misses and flickering
                    // Create a stable tag based on the file path and chapter position
                    // This ensures the tag remains constant as long as the file exists
                    var stableTagSource = $"{fullPath}|{chapterPositionTicks.Value}";
                    chapter.ImageTag = stableTagSource.GetMD5().ToString("N", System.Globalization.CultureInfo.InvariantCulture);

                    chapters.Add(chapter);
                }
            }

            // Sort by position ticks
            chapters.Sort((a, b) => a.StartPositionTicks.CompareTo(b.StartPositionTicks));

            // Set chapter names after sorting to ensure correct indices
            for (int i = 0; i < chapters.Count; i++)
            {
                chapters[i].Name = $"Chapter {i + 1}";
            }

            if (chapters.Count > 0)
            {
                _logger.LogInformation("Found {Count} pre-transcoded chapter images for {Video}. Caching result.", chapters.Count, video.Name);

                // Log chapter details for debugging
                foreach (var chapter in chapters)
                {
                    _logger.LogTrace(
                        "Chapter: Position={PositionTicks} ({PositionSeconds}s), Path={Path}, Tag={Tag}",
                        chapter.StartPositionTicks,
                        TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds,
                        chapter.ImagePath,
                        chapter.ImageTag);
                }

                // Cache the result
                var result = chapters.AsReadOnly();

                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning pre-transcoded chapters folder for {Video}", video.Name);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task DeleteChapterDataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _chapterRepository.DeleteChaptersAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<string> GetSavedChapterImages(Video video, IDirectoryService directoryService)
    {
        var path = _pathManager.GetChapterImageFolderPath(video);
        if (!Directory.Exists(path))
        {
            return [];
        }

        try
        {
            return directoryService.GetFilePaths(path);
        }
        catch (IOException)
        {
            return [];
        }
    }

    private void DeleteDeadImages(IEnumerable<string> images, IEnumerable<ChapterInfo> chapters)
    {
        var existingImages = chapters.Select(i => i.ImagePath).Where(i => !string.IsNullOrEmpty(i));
        var deadImages = images
            .Except(existingImages, StringComparer.OrdinalIgnoreCase)
            .Where(i => BaseItem.SupportedImageExtensions.Contains(Path.GetExtension(i.AsSpan()), StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var image in deadImages)
        {
            _logger.LogDebug("Deleting dead chapter image {Path}", image);

            try
            {
                _fileSystem.DeleteFile(image!);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error deleting {Path}.", image);
            }
        }
    }
}

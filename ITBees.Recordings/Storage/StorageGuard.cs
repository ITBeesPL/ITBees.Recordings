using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.Storage;

internal interface IStorageGuard
{
    /// <summary>Deletes expired clips and overwrites the oldest files above the usage limit. Returns the usage afterwards.</summary>
    DiskUsage Enforce(StorageLocation location);
}

/// <summary>
/// Ring buffer over the storage: when the disk is fuller than <see cref="RecordingsOptions.MaxDiskUsagePercent"/>
/// (or our files exceed <see cref="RecordingsOptions.MaxStorageMegabytes"/>) the oldest files go first -
/// continuous recordings and clips alike, by age. The file a camera is writing is never deleted. A full disk
/// would stop the host application too, so this also protects a shared SD card.
/// </summary>
internal sealed class StorageGuard : IStorageGuard
{
    /// <summary>Clip metadata written next to each clip; deleted together with it.</summary>
    public const string ClipMetadataExtension = ".json";

    // Folder size needs a walk over every file - not on every pass.
    private static readonly TimeSpan SizeCheckInterval = TimeSpan.FromMinutes(5);

    private readonly RecordingsOptions _options;
    private readonly ILinuxSystem _linuxSystem;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StorageGuard> _logger;
    private DateTimeOffset _nextSizeCheck;

    public StorageGuard(IOptions<RecordingsOptions> options, ILinuxSystem linuxSystem, TimeProvider timeProvider, ILogger<StorageGuard> logger)
    {
        _options = options.Value;
        _linuxSystem = linuxSystem;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public DiskUsage Enforce(StorageLocation location)
    {
        DeleteExpiredClips(location.ClipsDirectory);

        var usage = _linuxSystem.GetDiskUsage(location.Root);
        var toFree = Math.Max(BytesOverDiskLimit(usage), BytesOverStorageCap(location));
        if (toFree <= 0)
        {
            return usage;
        }

        var deleted = 0;
        long freed = 0;
        foreach (var file in DeletionCandidates(location))
        {
            if (freed >= toFree)
            {
                break;
            }

            freed += Delete(file);
            deleted++;
        }

        DeleteEmptyDirectories(location.ClipsDirectory);
        var after = _linuxSystem.GetDiskUsage(location.Root);
        _logger.LogInformation("Storage {Root} at {Before:0.0}% (limit {Limit}%): overwrote {Count} oldest files ({Freed} MB), now {After:0.0}%",
            location.Root, usage.UsedPercent, _options.MaxDiskUsagePercent, deleted, freed / 1024 / 1024, after.UsedPercent);
        return after;
    }

    /// <summary>Bytes to delete to get 1 point under the usage limit.</summary>
    internal long BytesOverDiskLimit(DiskUsage usage)
    {
        if (_options.MaxDiskUsagePercent is <= 0 or >= 100 || usage.UsedPercent <= _options.MaxDiskUsagePercent)
        {
            return 0;
        }

        var capacity = usage.UsedBytes + usage.AvailableBytes;
        var target = (long)(capacity * (_options.MaxDiskUsagePercent - 1) / 100.0);
        return Math.Max(0, usage.UsedBytes - target);
    }

    private long BytesOverStorageCap(StorageLocation location)
    {
        var now = _timeProvider.GetUtcNow();
        if (_options.MaxStorageMegabytes <= 0 || now < _nextSizeCheck)
        {
            return 0;
        }

        _nextSizeCheck = now + SizeCheckInterval;
        var cap = _options.MaxStorageMegabytes * 1024 * 1024;
        var size = Directory.Exists(location.Root)
            ? new DirectoryInfo(location.Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length)
            : 0;
        if (size <= cap)
        {
            return 0;
        }

        // Check again on the next pass, until under the cap; free 2 % below it to avoid deleting file by file.
        _nextSizeCheck = now;
        return size - (long)(cap * 0.98);
    }

    /// <summary>Oldest first, recordings and clips together; never the newest recording of a camera (it is being written).</summary>
    internal static IEnumerable<FileInfo> DeletionCandidates(StorageLocation location)
    {
        var files = new List<FileInfo>();
        if (Directory.Exists(location.RecordingsDirectory))
        {
            foreach (var cameraDirectory in new DirectoryInfo(location.RecordingsDirectory).EnumerateDirectories())
            {
                var recordings = cameraDirectory.EnumerateFiles("*.mp4").OrderBy(x => x.LastWriteTimeUtc).ToList();
                files.AddRange(recordings.Take(Math.Max(0, recordings.Count - 1)));
            }
        }

        if (Directory.Exists(location.ClipsDirectory))
        {
            files.AddRange(new DirectoryInfo(location.ClipsDirectory).EnumerateFiles("*.mp4", SearchOption.AllDirectories));
        }

        return files.OrderBy(x => x.LastWriteTimeUtc);
    }

    private void DeleteExpiredClips(string clipsDirectory)
    {
        if (_options.ClipRetention <= TimeSpan.Zero || Directory.Exists(clipsDirectory) == false)
        {
            return;
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - _options.ClipRetention;
        var expired = new DirectoryInfo(clipsDirectory).EnumerateFiles("*.mp4", SearchOption.AllDirectories)
            .Where(x => x.LastWriteTimeUtc < cutoff)
            .ToList();
        foreach (var file in expired)
        {
            Delete(file);
        }

        if (expired.Count > 0)
        {
            DeleteEmptyDirectories(clipsDirectory);
            _logger.LogInformation("Deleted {Count} clips older than {Retention}", expired.Count, _options.ClipRetention);
        }
    }

    /// <summary>Deletes the file (and a clip's metadata next to it); returns the bytes freed.</summary>
    private long Delete(FileInfo file)
    {
        try
        {
            var size = file.Length;
            file.Delete();
            var metadata = Path.ChangeExtension(file.FullName, ClipMetadataExtension);
            if (File.Exists(metadata))
            {
                size += new FileInfo(metadata).Length;
                File.Delete(metadata);
            }

            return size;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not delete {File}", file.FullName);
            return 0;
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        if (Directory.Exists(root) == false)
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any() == false)
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
                // Written to in the meantime.
            }
        }
    }
}

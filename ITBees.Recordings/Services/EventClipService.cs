using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ITBees.Recordings.MediaMtx;
using ITBees.Recordings.Models;
using ITBees.Recordings.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.Services;

internal sealed class EventClipService : IEventClipService
{
    private const string JobsFileName = "clip-jobs.json";
    private static readonly TimeSpan MaxClipLength = TimeSpan.FromMinutes(30);

    // A recording shorter than requested by less than this still counts as complete (key frame alignment).
    private static readonly TimeSpan CoverageTolerance = TimeSpan.FromSeconds(1);

    // While the camera is recording, a clip whose end is not on disk yet waits this long before it is cut short.
    private static readonly TimeSpan TailGrace = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly RecordingsOptions _options;
    private readonly IRecordingManager _recordingManager;
    private readonly IMediaMtxClient _mediaMtxClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventClipService> _logger;
    private readonly object _jobsLock = new();
    private List<ClipJob>? _jobs;
    private bool _persistWarningLogged;

    public EventClipService(
        IOptions<RecordingsOptions> options,
        IRecordingManager recordingManager,
        IMediaMtxClient mediaMtxClient,
        TimeProvider timeProvider,
        ILogger<EventClipService> logger)
    {
        _options = options.Value;
        _recordingManager = recordingManager;
        _mediaMtxClient = mediaMtxClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public event EventHandler<ClipFinishedEventArgs>? ClipFinished;

    private string JobsPath => Path.Combine(_options.ResolvedStateDirectory, JobsFileName);

    public IReadOnlyList<ClipJob> PendingJobs
    {
        get
        {
            lock (_jobsLock)
            {
                return Jobs().ToList();
            }
        }
    }

    public ClipJob Enqueue(ClipRequest request)
    {
        if (_recordingManager.Cameras.Any(x => string.Equals(x.Id, request.CameraId, StringComparison.OrdinalIgnoreCase)) == false)
        {
            throw new ArgumentException($"Camera '{request.CameraId}' is not recorded");
        }

        if (string.IsNullOrWhiteSpace(request.EventId))
        {
            throw new ArgumentException("A clip request needs an EventId");
        }

        var before = request.Before ?? _options.ClipBefore;
        var after = request.After ?? _options.ClipAfter;
        if (before < TimeSpan.Zero || after < TimeSpan.Zero || before + after <= TimeSpan.Zero || before + after > MaxClipLength)
        {
            throw new ArgumentException($"Clip of {before} before and {after} after the event is not allowed (max {MaxClipLength} in total)");
        }

        lock (_jobsLock)
        {
            var jobs = Jobs();
            var existing = jobs.FirstOrDefault(x => string.Equals(x.Request.CameraId, request.CameraId, StringComparison.OrdinalIgnoreCase)
                                                    && x.Request.EventId == request.EventId
                                                    && x.Request.EventTime == request.EventTime);
            if (existing != null)
            {
                return existing;
            }

            var job = Plan(request, before, after, _options.ClipSettleDelay);
            jobs.Add(job);
            Persist(jobs);
            _logger.LogInformation("Clip queued: camera {CameraId}, event {EventId} at {EventTime:O}, {Start:HH:mm:ss}-{End:HH:mm:ss} UTC, due {DueAt:HH:mm:ss}",
                request.CameraId, request.EventId, request.EventTime, job.Start.UtcDateTime, job.End.UtcDateTime, job.DueAt.UtcDateTime);
            return job;
        }
    }

    public Task<IReadOnlyList<RecordedSpan>> ListRecordedAsync(string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        _mediaMtxClient.ListSpansAsync(cameraId, from, to, cancellationToken);

    public async Task<ClipResult> ExportAsync(string cameraId, DateTimeOffset start, TimeSpan duration, string filePath, CancellationToken cancellationToken = default)
    {
        var job = new ClipJob
        {
            Id = Guid.NewGuid(),
            Request = new ClipRequest { CameraId = cameraId, EventId = "export", EventTime = start },
            Start = start,
            Duration = duration,
            DueAt = start + duration,
            FilePath = filePath,
        };

        var spans = await _mediaMtxClient.ListSpansAsync(cameraId, job.Start, job.End, cancellationToken);
        if (Coverage(spans, job.Start, job.End) <= TimeSpan.Zero)
        {
            return new ClipResult { Job = job, Outcome = ClipOutcome.NoRecording };
        }

        return await CutAsync(job, spans, filePath, cancellationToken);
    }

    public async Task ProcessDueJobsAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        List<ClipJob> due;
        lock (_jobsLock)
        {
            due = Jobs().Where(x => x.DueAt <= now).OrderBy(x => x.DueAt).ToList();
        }

        foreach (var job in due)
        {
            var (result, error) = await TryCutAsync(job, now, cancellationToken);
            lock (_jobsLock)
            {
                var jobs = Jobs();
                var index = jobs.FindIndex(x => x.Id == job.Id);
                if (index < 0)
                {
                    continue;
                }

                if (result == null)
                {
                    jobs[index] = job with { Attempts = job.Attempts + 1, LastError = error };
                }
                else
                {
                    jobs.RemoveAt(index);
                }

                Persist(jobs);
            }

            if (result != null)
            {
                Report(result);
            }
        }
    }

    /// <summary>A finished result, or null with the reason to try again later.</summary>
    private async Task<(ClipResult? Result, string? Error)> TryCutAsync(ClipJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var giveUp = now >= job.DueAt + _options.ClipMaxWait;
        var clipsDirectory = _recordingManager.ClipsDirectory;
        if (clipsDirectory == null)
        {
            const string noStorage = "No storage for clips";
            return giveUp ? (Finished(job, ClipOutcome.Failed, noStorage), null) : (null, noStorage);
        }

        try
        {
            var spans = await _mediaMtxClient.ListSpansAsync(job.Request.CameraId, job.Start, job.End, cancellationToken);
            if (Coverage(spans, job.Start, job.End) <= TimeSpan.Zero)
            {
                const string nothing = "Nothing recorded in the range yet";
                return giveUp ? (Finished(job, ClipOutcome.NoRecording, null), null) : (null, nothing);
            }

            // The end may still be in MediaMTX's buffer - wait a little while the camera is recording.
            var recordedUntil = spans.Max(x => x.End);
            if (recordedUntil < job.End - CoverageTolerance && now < job.DueAt + TailGrace && giveUp == false && IsRecording(job.Request.CameraId))
            {
                return (null, $"Recorded only until {recordedUntil:HH:mm:ss}");
            }

            var filePath = Path.Combine(clipsDirectory, RelativePath(job));
            return (await CutAsync(job, spans, filePath, cancellationToken, writeMetadata: true), null);
        }
        catch (Exception e) when (e is not OperationCanceledException || cancellationToken.IsCancellationRequested == false)
        {
            _logger.LogWarning("Clip {EventId} of camera {CameraId}: {Error}{Retry}", job.Request.EventId, job.Request.CameraId, e.Message, giveUp ? "" : " - will retry");
            return giveUp ? (Finished(job, ClipOutcome.Failed, e.Message), null) : (null, e.Message);
        }
    }

    private async Task<ClipResult> CutAsync(ClipJob job, IReadOnlyList<RecordedSpan> spans, string filePath, CancellationToken cancellationToken, bool writeMetadata = false)
    {
        // Start where the recording starts (MediaMTX cannot begin inside a gap) and stop where it ends.
        var start = Max(job.Start, spans.Min(x => x.Start));
        var end = Min(job.End, spans.Max(x => x.End));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporary = filePath + ".part";
        try
        {
            await using (var file = File.Create(temporary))
            {
                await _mediaMtxClient.DownloadAsync(job.Request.CameraId, start, end - start, file, cancellationToken);
            }

            var size = new FileInfo(temporary).Length;
            if (size == 0)
            {
                throw new RecordingsException("MediaMTX returned an empty clip");
            }

            File.Move(temporary, filePath, overwrite: true);
            var recorded = Coverage(spans, job.Start, job.End);
            var outcome = recorded >= job.Duration - CoverageTolerance ? ClipOutcome.Completed : ClipOutcome.Partial;
            var metadataPath = writeMetadata ? WriteMetadata(job, filePath, start, end, recorded, outcome) : null;
            return new ClipResult
            {
                Job = job with { FilePath = filePath },
                Outcome = outcome,
                FilePath = filePath,
                MetadataPath = metadataPath,
                Recorded = recorded,
                SizeBytes = size,
            };
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public IReadOnlyList<ClipPackage> ListPackages(int skip = 0, int take = 50)
    {
        var clipsDirectory = _recordingManager.ClipsDirectory;
        if (clipsDirectory == null || Directory.Exists(clipsDirectory) == false)
        {
            return Array.Empty<ClipPackage>();
        }

        return new DirectoryInfo(clipsDirectory).EnumerateDirectories()
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 500))
            .Select(ToPackage)
            .Where(x => x.ClipCount > 0)
            .ToList();
    }

    public IReadOnlyList<ClipInfo> ListClips(string package)
    {
        var clipsDirectory = _recordingManager.ClipsDirectory;
        if (clipsDirectory == null || string.IsNullOrWhiteSpace(package) || package != SafeName(package))
        {
            return Array.Empty<ClipInfo>();
        }

        var directory = new DirectoryInfo(Path.Combine(clipsDirectory, package));
        if (directory.Exists == false)
        {
            return Array.Empty<ClipInfo>();
        }

        return directory.EnumerateFiles("*.mp4")
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new ClipInfo
            {
                RelativePath = $"{package}/{x.Name}",
                Package = package,
                SizeBytes = x.Length,
                Metadata = ReadMetadata(Path.ChangeExtension(x.FullName, StorageGuard.ClipMetadataExtension)),
            })
            .ToList();
    }

    public string? GetClipPath(string relativePath)
    {
        var clipsDirectory = _recordingManager.ClipsDirectory;
        if (clipsDirectory == null || string.IsNullOrWhiteSpace(relativePath)
                                   || relativePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) == false)
        {
            return null;
        }

        // Only files inside the clips directory - the path comes from a panel request.
        var root = Path.GetFullPath(clipsDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(clipsDirectory, relativePath));
        return fullPath.StartsWith(root, StringComparison.Ordinal) && File.Exists(fullPath) ? fullPath : null;
    }

    private static ClipPackage ToPackage(DirectoryInfo directory)
    {
        var files = directory.EnumerateFiles("*.mp4").ToList();
        var times = files.Select(x => EventTimeFromName(x.Name)).Where(x => x != null).Select(x => x!.Value).ToList();
        return new ClipPackage
        {
            Name = directory.Name,
            ClipCount = files.Count,
            SizeBytes = files.Sum(x => x.Length),
            FirstEventTime = times.Count > 0 ? times.Min() : null,
            LastEventTime = times.Count > 0 ? times.Max() : null,
        };
    }

    /// <summary>Clip names start with the event time, <c>yyyyMMddTHHmmssZ</c>.</summary>
    internal static DateTimeOffset? EventTimeFromName(string fileName) =>
        fileName.Length >= 16 && DateTimeOffset.TryParseExact(fileName[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;

    private static ClipMetadata? ReadMetadata(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ClipMetadata>(File.ReadAllText(path), MetadataJsonOptions) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private bool IsRecording(string cameraId) =>
        _recordingManager.GetStatus().Cameras.Any(x => string.Equals(x.CameraId, cameraId, StringComparison.OrdinalIgnoreCase) && x.State == RecordingCameraState.Recording);

    private void Report(ClipResult result)
    {
        var request = result.Job.Request;
        if (result.Outcome is ClipOutcome.Completed or ClipOutcome.Partial)
        {
            _logger.LogInformation("Clip {Outcome}: camera {CameraId}, event {EventId}, {Recorded:0} s recorded, {Size} kB -> {File}",
                result.Outcome, request.CameraId, request.EventId, result.Recorded.TotalSeconds, result.SizeBytes / 1024, result.FilePath);
        }
        else
        {
            _logger.LogWarning("Clip {Outcome}: camera {CameraId}, event {EventId} at {EventTime:O} ({Error})",
                result.Outcome, request.CameraId, request.EventId, request.EventTime, result.Error);
        }

        try
        {
            ClipFinished?.Invoke(this, new ClipFinishedEventArgs(result));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Clip handler failed");
        }
    }

    internal static ClipJob Plan(ClipRequest request, TimeSpan before, TimeSpan after, TimeSpan settleDelay)
    {
        var job = new ClipJob
        {
            Id = Guid.NewGuid(),
            Request = request,
            Start = request.EventTime - before,
            Duration = before + after,
            DueAt = request.EventTime + after + settleDelay,
        };
        return job with { FilePath = RelativePath(job) };
    }

    /// <summary>
    /// <c>{group}/{yyyyMMddTHHmmssZ}_{camera}_{label}_{event}.mp4</c>, or <c>{yyyy-MM-dd}/…</c> without a group.
    /// UTC, so names never repeat at a DST change; names sort by time inside a package.
    /// </summary>
    internal static string RelativePath(ClipJob job)
    {
        var request = job.Request;
        var time = request.EventTime.UtcDateTime;
        var name = new StringBuilder(time.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        name.Append('_').Append(SafeName(request.CameraId));
        if (string.IsNullOrWhiteSpace(request.Label) == false)
        {
            name.Append('_').Append(SafeName(request.Label));
        }

        name.Append('_').Append(SafeName(request.EventId));
        var folder = string.IsNullOrWhiteSpace(request.GroupId)
            ? time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : SafeName(request.GroupId);
        return Path.Combine(folder, name + ".mp4");
    }

    private string WriteMetadata(ClipJob job, string filePath, DateTimeOffset recordedStart, DateTimeOffset recordedEnd, TimeSpan recorded, ClipOutcome outcome)
    {
        var request = job.Request;
        var metadata = new ClipMetadata
        {
            CameraId = request.CameraId,
            EventId = request.EventId,
            Label = request.Label,
            GroupId = request.GroupId,
            EventTime = request.EventTime.ToUniversalTime(),
            EventTimeLocal = TimeZoneInfo.ConvertTime(request.EventTime, _timeProvider.LocalTimeZone),
            ClipStart = job.Start.ToUniversalTime(),
            ClipEnd = job.End.ToUniversalTime(),
            RecordedStart = recordedStart.ToUniversalTime(),
            RecordedEnd = recordedEnd.ToUniversalTime(),
            RecordedSeconds = Math.Round(recorded.TotalSeconds, 1),
            Outcome = outcome,
            Metadata = request.Metadata ?? new Dictionary<string, string>(),
        };

        var path = Path.ChangeExtension(filePath, StorageGuard.ClipMetadataExtension);
        File.WriteAllText(path, JsonSerializer.Serialize(metadata, MetadataJsonOptions));
        return path;
    }

    internal static TimeSpan Coverage(IEnumerable<RecordedSpan> spans, DateTimeOffset from, DateTimeOffset to)
    {
        var total = TimeSpan.Zero;
        foreach (var span in spans)
        {
            var overlap = Min(span.End, to) - Max(span.Start, from);
            if (overlap > TimeSpan.Zero)
            {
                total += overlap;
            }
        }

        return total;
    }

    private static string SafeName(string text)
    {
        var safe = new string(text.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return safe.Length <= 64 ? safe : safe[..64];
    }

    private static ClipResult Finished(ClipJob job, ClipOutcome outcome, string? error) =>
        new() { Job = job, Outcome = outcome, Error = error };

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    // Caller holds _jobsLock.
    private List<ClipJob> Jobs()
    {
        if (_jobs != null)
        {
            return _jobs;
        }

        _jobs = new List<ClipJob>();
        try
        {
            if (File.Exists(JobsPath))
            {
                _jobs = JsonSerializer.Deserialize<List<ClipJob>>(File.ReadAllText(JobsPath), JsonOptions) ?? new List<ClipJob>();
                if (_jobs.Count > 0)
                {
                    _logger.LogInformation("{Count} queued clips restored from {Path}", _jobs.Count, JobsPath);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(e, "Queued clips could not be read from {Path}", JobsPath);
        }

        return _jobs;
    }

    // Caller holds _jobsLock. Losing the file only loses the queue, so a failure is logged, not thrown.
    private void Persist(List<ClipJob> jobs)
    {
        try
        {
            Directory.CreateDirectory(_options.ResolvedStateDirectory);
            var temporary = JobsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(jobs, JsonOptions));
            File.Move(temporary, JobsPath, overwrite: true);
            _persistWarningLogged = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_persistWarningLogged == false)
            {
                _logger.LogWarning(e, "Queued clips cannot be saved to {Path} - they are kept in memory only", JobsPath);
                _persistWarningLogged = true;
            }
        }
    }
}

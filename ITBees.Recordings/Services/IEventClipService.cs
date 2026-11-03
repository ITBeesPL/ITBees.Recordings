using ITBees.Recordings.Models;

namespace ITBees.Recordings.Services;

/// <summary>
/// Cuts clips around events out of the continuous recording. A request is queued (and kept on disk) until
/// the recording past its end is surely written, then the range is fetched from the MediaMTX playback
/// server as a standard MP4. Every event gets its own file, so two events closer than the clip length
/// share part of the footage.
/// </summary>
public interface IEventClipService
{
    /// <summary>Raised for every finished request, also when no clip could be made.</summary>
    event EventHandler<ClipFinishedEventArgs>? ClipFinished;

    /// <summary>Queues a clip of <c>EventTime - Before … EventTime + After</c>. The same camera, event id and time queue only once.</summary>
    ClipJob Enqueue(ClipRequest request);

    IReadOnlyList<ClipJob> PendingJobs { get; }

    /// <summary>Recorded stretches of a camera in the range (gaps = camera offline or recorder stopped).</summary>
    Task<IReadOnlyList<RecordedSpan>> ListRecordedAsync(string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>Writes whatever is recorded in the range to <paramref name="filePath"/> now (e.g. on demand from a panel).</summary>
    Task<ClipResult> ExportAsync(string cameraId, DateTimeOffset start, TimeSpan duration, string filePath, CancellationToken cancellationToken = default);

    /// <summary>Cuts every queued clip that is due. Called by the background worker.</summary>
    Task ProcessDueJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>Clip packages (groups and days), the most recently written first; empty without storage.</summary>
    IReadOnlyList<ClipPackage> ListPackages(int skip = 0, int take = 50);

    /// <summary>Clips of one package, oldest event first; empty when the package does not exist.</summary>
    IReadOnlyList<ClipInfo> ListClips(string package);

    /// <summary>
    /// Full path of a clip from its <see cref="ClipInfo.RelativePath"/>, or null when it does not exist or points
    /// outside the clips directory.
    /// </summary>
    string? GetClipPath(string relativePath);
}

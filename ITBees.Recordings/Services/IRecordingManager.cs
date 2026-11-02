using ITBees.Recordings.Models;

namespace ITBees.Recordings.Services;

/// <summary>
/// The continuous recorder: keeps MediaMTX installed, configured and running as a systemd service and
/// records every configured camera into fixed-length files. The service survives restarts of the host
/// application; this manager only brings it to the wanted state on each maintenance pass.
/// </summary>
public interface IRecordingManager
{
    /// <summary>Raised on every change of a camera's <see cref="RecordingCameraState"/>.</summary>
    event EventHandler<RecordingCameraStatusChangedEventArgs>? StatusChanged;

    IReadOnlyCollection<RecordingCameraDefinition> Cameras { get; }

    /// <summary>Replaces the set of recorded cameras; applied by the next maintenance pass, which starts at once.</summary>
    void Configure(IEnumerable<RecordingCameraDefinition> cameras);

    bool Enabled { get; }

    /// <summary>
    /// Switches recording on or off; applied by the next maintenance pass, which starts at once. Off stops the
    /// recorder service; queued clips stay queued.
    /// </summary>
    void SetEnabled(bool enabled);

    RecordingStatus GetStatus();

    /// <summary>Directory clips are written to, null while there is no storage (e.g. no USB disk).</summary>
    string? ClipsDirectory { get; }

    /// <summary>One pass: install, storage, cameras, configuration, service, free space, status. Called by the background service.</summary>
    Task RunMaintenanceAsync(CancellationToken cancellationToken);
}

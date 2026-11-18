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

    /// <summary>Changes <see cref="RecordingsOptions.MaxDiskUsagePercent"/> at runtime (1-99); applied on the next maintenance pass.</summary>
    void SetMaxDiskUsagePercent(int percent);

    int MaxDiskUsagePercent { get; }

    /// <summary>
    /// Changes <see cref="RecordingsOptions.UsbStorage"/> at runtime: true = a USB disk while one is usable,
    /// false = <see cref="RecordingsOptions.StorageDirectory"/> on the device even with a disk plugged in. Applied by
    /// the next maintenance pass, which starts at once; files already recorded stay where they are.
    /// </summary>
    void SetUsbStorage(bool usbStorage);

    bool UsbStorage { get; }

    /// <summary>
    /// Changes <see cref="RecordingsOptions.StorageDirectory"/> at runtime - the folder used when no USB disk is
    /// (e.g. a disk mounted by the system, <c>/mnt/data/recordings</c>); null goes back to the configured one. The
    /// path must be absolute, without <c>..</c>. Created (with sudo when its parent is root-owned) on the next
    /// maintenance pass, which starts at once; files already recorded stay where they are.
    /// </summary>
    void SetStorageDirectory(string? directory);

    /// <summary>The folder used when no USB disk is (see <see cref="SetStorageDirectory"/>).</summary>
    string StorageDirectory { get; }

    RecordingStatus GetStatus();

    /// <summary>Directory clips are written to, null while there is no storage (e.g. no USB disk).</summary>
    string? ClipsDirectory { get; }

    /// <summary>One pass: install, storage, cameras, configuration, service, free space, status. Called by the background service.</summary>
    Task RunMaintenanceAsync(CancellationToken cancellationToken);
}

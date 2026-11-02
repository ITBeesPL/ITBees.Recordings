namespace ITBees.Recordings.Models;

public class RecordingsOptions
{
    public const string SectionName = "Recordings";

    /// <summary>
    /// Recording at startup. The host can switch it later with <c>IRecordingManager.SetEnabled</c> (e.g. only the
    /// device acting as the recorder records); while disabled nothing is installed and a running service is stopped.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true (default) the library installs MediaMTX, writes its configuration and keeps the systemd
    /// service running (Linux only). When false it only talks to a MediaMTX already running at
    /// <see cref="ApiAddress"/> / <see cref="PlaybackAddress"/> - handy on a developer machine.
    /// </summary>
    public bool ManageMediaMtx { get; set; } = true;

    /// <summary>Where the MediaMTX binary, its configuration and the version marker live.</summary>
    public string InstallDirectory { get; set; } = "/opt/itbees-recordings";

    /// <summary>Pending clip jobs survive a restart of the host application here. Defaults to <c>{InstallDirectory}/state</c>.</summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// Name of the systemd unit running MediaMTX. It runs as the account of the host application, so the
    /// application can read, cut and delete the files without sudo.
    /// </summary>
    public string ServiceName { get; set; } = "itbees-recordings";

    /// <summary>
    /// Offline installation: path to <c>mediamtx_{version}_linux_{arch}.tar.gz</c> of the pinned release.
    /// When null the archive is downloaded from GitHub. Either way its SHA-256 is checked.
    /// </summary>
    public string? MediaMtxArchivePath { get; set; }

    /// <summary>Root of <c>recordings/</c> and <c>clips/</c>. Ignored when <see cref="UsbStorage"/> is on.</summary>
    public string StorageDirectory { get; set; } = "/var/lib/itbees-recordings";

    /// <summary>
    /// Record onto a USB disk (local-server column). The first USB partition (or the one matching
    /// <see cref="UsbLabel"/>) is used where it is mounted, otherwise it is mounted at <see cref="UsbMountPoint"/>.
    /// Without the disk nothing is recorded - the SD card is never used as a fallback.
    /// </summary>
    public bool UsbStorage { get; set; }

    public string UsbMountPoint { get; set; } = "/mnt/itbees-recordings";

    /// <summary>Optional filesystem label of the USB partition to use when several disks are plugged in.</summary>
    public string? UsbLabel { get; set; }

    /// <summary>Length of one continuous recording file.</summary>
    public TimeSpan SegmentDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Data written to disk at least this often (fMP4 part). After a power cut at most this much is lost,
    /// and a clip can be cut once this much has passed after its end.
    /// </summary>
    public TimeSpan PartDuration { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Disk usage limit of the file system holding the recordings (like <c>df</c> Use%). Above it the oldest
    /// files - continuous recordings and clips alike, oldest first - are overwritten (deleted) until the usage
    /// is 1 point under the limit. The file each camera is writing is never touched.
    /// </summary>
    public int MaxDiskUsagePercent { get; set; } = 90;

    /// <summary>Optional cap on the size of the recordings and clips together; 0 = only <see cref="MaxDiskUsagePercent"/> applies.</summary>
    public long MaxStorageMegabytes { get; set; }

    /// <summary>Optional age limit of continuous recordings (deleted by MediaMTX). <see cref="TimeSpan.Zero"/> = kept until overwritten.</summary>
    public TimeSpan RecordingRetention { get; set; } = TimeSpan.Zero;

    /// <summary>Optional age limit of event clips. <see cref="TimeSpan.Zero"/> = kept until overwritten.</summary>
    public TimeSpan ClipRetention { get; set; } = TimeSpan.Zero;

    /// <summary>Default part of an event clip before the event.</summary>
    public TimeSpan ClipBefore { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Default part of an event clip after the event.</summary>
    public TimeSpan ClipAfter { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Extra wait after the end of a clip, so the last part is surely on disk.</summary>
    public TimeSpan ClipSettleDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a clip keeps being retried (recording not there yet, MediaMTX restarting) before it is given up.</summary>
    public TimeSpan ClipMaxWait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>RTSP re-stream for live view. Bound to localhost by default; ":8554" exposes it to the LAN.</summary>
    public string RtspAddress { get; set; } = "127.0.0.1:8554";

    /// <summary>MediaMTX playback server used to cut clips. ":9996" exposes the recordings to the LAN.</summary>
    public string PlaybackAddress { get; set; } = "127.0.0.1:9996";

    /// <summary>MediaMTX control API - keep it on localhost.</summary>
    public string ApiAddress { get; set; } = "127.0.0.1:9997";

    /// <summary>How often the service, the storage and the cameras are checked.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cameras known at startup; the host may replace the set later with <c>IRecordingManager.Configure</c>.</summary>
    public List<RecordingCameraDefinition> Cameras { get; set; } = new();

    internal string ResolvedStateDirectory => StateDirectory ?? Path.Combine(InstallDirectory, "state");
}

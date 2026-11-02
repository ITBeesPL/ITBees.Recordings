namespace ITBees.Recordings.Models;

public enum RecordingCameraState
{
    /// <summary>Not looked at yet.</summary>
    Pending = 0,

    /// <summary>MediaMTX is configured for the camera but has no picture yet (connecting or reconnecting).</summary>
    Connecting = 1,

    Recording = 2,

    /// <summary>The stream address could not be resolved or the encoder could not be configured - see the error.</summary>
    Error = 3,

    /// <summary>No storage (e.g. the USB disk is missing), so nothing is recorded.</summary>
    NoStorage = 4,

    /// <summary>Recording runs only on Linux.</summary>
    NotSupported = 5,
}

/// <summary>Point-in-time snapshot of one camera, safe to hand out.</summary>
public sealed record RecordingCameraStatus
{
    public string CameraId { get; init; } = string.Empty;
    public RecordingCameraState State { get; init; }
    public string? Error { get; init; }

    /// <summary>Since when MediaMTX has had the stream without a break.</summary>
    public DateTimeOffset? OnlineSince { get; init; }

    public long InboundBytes { get; init; }
    public long FramesInError { get; init; }

    /// <summary>What is recorded, e.g. <c>profile2: H264 704x576 25 fps 768 kb/s GOP 50</c> (null with an explicit RtspUrl).</summary>
    public string? StreamDescription { get; init; }
}

/// <summary>Point-in-time snapshot of the whole recorder.</summary>
public sealed record RecordingStatus
{
    public bool Enabled { get; init; }
    public bool Supported { get; init; }
    public bool Installed { get; init; }
    public bool ServiceActive { get; init; }

    /// <summary>Root of recordings and clips, null when there is no storage (e.g. no USB disk).</summary>
    public string? StorageDirectory { get; init; }

    public long? FreeSpaceBytes { get; init; }

    /// <summary>Usage of the storage file system in percent (as <c>df</c> shows it).</summary>
    public double? DiskUsedPercent { get; init; }

    public string? Error { get; init; }
    public IReadOnlyList<RecordingCameraStatus> Cameras { get; init; } = Array.Empty<RecordingCameraStatus>();
}

public sealed class RecordingCameraStatusChangedEventArgs : EventArgs
{
    public RecordingCameraStatusChangedEventArgs(RecordingCameraState previousState, RecordingCameraStatus status)
    {
        PreviousState = previousState;
        Status = status;
    }

    public RecordingCameraState PreviousState { get; }
    public RecordingCameraStatus Status { get; }
}

/// <summary>A continuous stretch of recording (no gap) as reported by the MediaMTX playback server.</summary>
public sealed record RecordedSpan(DateTimeOffset Start, TimeSpan Duration)
{
    public DateTimeOffset End => Start + Duration;
}

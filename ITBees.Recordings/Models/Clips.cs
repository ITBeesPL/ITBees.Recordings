namespace ITBees.Recordings.Models;

/// <summary>Cut a clip around an event out of the continuous recording of one camera.</summary>
public sealed record ClipRequest
{
    public string CameraId { get; init; } = string.Empty;

    /// <summary>Identifier of the event in the host system (e.g. a journal event guid); part of the file name.</summary>
    public string EventId { get; init; } = string.Empty;

    public DateTimeOffset EventTime { get; init; }

    /// <summary>Short kind of the event for the file name, e.g. <c>entry</c>, <c>exit</c>, <c>manual-open</c>.</summary>
    public string? Label { get; init; }

    /// <summary>Overrides <see cref="RecordingsOptions.ClipBefore"/>.</summary>
    public TimeSpan? Before { get; init; }

    /// <summary>Overrides <see cref="RecordingsOptions.ClipAfter"/>.</summary>
    public TimeSpan? After { get; init; }

    /// <summary>
    /// Package the clip belongs to (e.g. a parking session, a vehicle passage): clips of one group land in one
    /// folder <c>clips/{GroupId}/</c>, across cameras. Without it the clip goes to <c>clips/{yyyy-MM-dd}/</c>.
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>Free-form facts saved next to the clip in its <c>.json</c> file (session id, plate, device name…).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>Contents of the <c>.json</c> file written next to every clip.</summary>
public sealed record ClipMetadata
{
    public string CameraId { get; init; } = string.Empty;
    public string EventId { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? GroupId { get; init; }

    /// <summary>Event time in UTC.</summary>
    public DateTimeOffset EventTime { get; init; }

    /// <summary>Event time in the device's time zone, for people reading the file.</summary>
    public DateTimeOffset EventTimeLocal { get; init; }

    /// <summary>Requested range in UTC.</summary>
    public DateTimeOffset ClipStart { get; init; }

    public DateTimeOffset ClipEnd { get; init; }

    /// <summary>What the file really holds (it starts on a key frame and may be shorter at a gap).</summary>
    public DateTimeOffset RecordedStart { get; init; }

    public DateTimeOffset RecordedEnd { get; init; }
    public double RecordedSeconds { get; init; }
    public ClipOutcome Outcome { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>A queued clip. Kept on disk, so it is still cut after a restart of the host application.</summary>
public sealed record ClipJob
{
    public Guid Id { get; init; }
    public ClipRequest Request { get; init; } = new();
    public DateTimeOffset Start { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>Earliest moment the clip can be cut: its end plus the settle delay.</summary>
    public DateTimeOffset DueAt { get; init; }

    /// <summary>While queued: the path relative to the clips directory. In a finished <see cref="ClipResult"/>: the full path.</summary>
    public string FilePath { get; init; } = string.Empty;

    public int Attempts { get; init; }
    public string? LastError { get; init; }

    public DateTimeOffset End => Start + Duration;
}

public enum ClipOutcome
{
    /// <summary>The whole requested range was recorded.</summary>
    Completed = 0,

    /// <summary>A clip was written, but the recording has gaps in the requested range (camera offline, restart).</summary>
    Partial = 1,

    /// <summary>Nothing was recorded in the requested range - no file.</summary>
    NoRecording = 2,

    /// <summary>The clip could not be cut (playback server unreachable, disk error) - no file.</summary>
    Failed = 3,
}

public sealed record ClipResult
{
    public ClipJob Job { get; init; } = new();
    public ClipOutcome Outcome { get; init; }

    /// <summary>Written file (Completed / Partial), otherwise null.</summary>
    public string? FilePath { get; init; }

    /// <summary>The <c>.json</c> with <see cref="ClipMetadata"/> next to an event clip, otherwise null.</summary>
    public string? MetadataPath { get; init; }

    /// <summary>Recorded time inside the requested range.</summary>
    public TimeSpan Recorded { get; init; }

    public long SizeBytes { get; init; }
    public string? Error { get; init; }
}

public sealed class ClipFinishedEventArgs : EventArgs
{
    public ClipFinishedEventArgs(ClipResult result)
    {
        Result = result;
    }

    public ClipResult Result { get; }
}

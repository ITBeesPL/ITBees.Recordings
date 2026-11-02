using System.Text.RegularExpressions;

namespace ITBees.Recordings.Models;

/// <summary>Which ONVIF profile is recorded when the camera has no explicit <see cref="RecordingCameraDefinition.RtspUrl"/>.</summary>
public enum RecordingStream
{
    /// <summary>The profile with the highest resolution.</summary>
    Main = 0,

    /// <summary>The second-highest resolution (falls back to the main one on single-stream cameras).</summary>
    Sub = 1,

    /// <summary>The profile with the lowest resolution.</summary>
    Lowest = 2,
}

/// <summary>
/// Recording quality = which camera stream is stored, optionally with the camera encoder re-configured
/// over ONVIF. Nothing is re-encoded on the device, so this is the only way to control file size:
/// one hour takes about <c>bitrate [kb/s] x 0.45 MB</c>.
/// </summary>
public sealed record RecordingQuality
{
    public RecordingStream Stream { get; set; } = RecordingStream.Main;

    /// <summary>Explicit ONVIF profile token (e.g. <c>profile2</c>); wins over <see cref="Stream"/>.</summary>
    public string? ProfileToken { get; set; }

    /// <summary>Written to the camera encoder of the recorded profile when set (must be a resolution the camera offers).</summary>
    public int? Width { get; set; }

    public int? Height { get; set; }
    public int? FrameRate { get; set; }
    public int? BitrateKbps { get; set; }

    /// <summary>
    /// Frames between key frames (ONVIF GovLength). A clip can only start on a key frame, so this decides
    /// how precisely a clip starts; 2 x frame rate (2 s) is a good value.
    /// </summary>
    public int? KeyframeInterval { get; set; }

    internal bool HasEncoderSettings => Width != null || Height != null || FrameRate != null || BitrateKbps != null || KeyframeInterval != null;

    public override string ToString()
    {
        var stream = ProfileToken ?? Stream.ToString();
        var parts = new List<string> { stream };
        if (Width != null && Height != null) parts.Add($"{Width}x{Height}");
        if (FrameRate != null) parts.Add($"{FrameRate} fps");
        if (BitrateKbps != null) parts.Add($"{BitrateKbps} kb/s");
        if (KeyframeInterval != null) parts.Add($"GOP {KeyframeInterval}");
        return string.Join(", ", parts);
    }
}

/// <summary>One camera to record continuously.</summary>
public sealed record RecordingCameraDefinition
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    /// <summary>Stable identifier; also the directory and MediaMTX path name, so letters, digits, '-' and '_' only.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Camera address for ONVIF, e.g. <c>192.168.10.3</c> or <c>192.168.10.3:8080</c>. The stream address is
    /// read with ONVIF GetStreamUri unless <see cref="RtspUrl"/> is given.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>Stream address used as is (credentials below are added when the URL has none).</summary>
    public string? RtspUrl { get; set; }

    /// <summary>
    /// Account for ONVIF and RTSP. On IPOX/TVT cameras this is the account from the camera's
    /// Network → Advanced → Onvif tab, which also opens RTSP.
    /// </summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    public RecordingQuality Quality { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || IdPattern.IsMatch(Id) == false)
        {
            throw new ArgumentException($"Recording camera id '{Id}' must be 1-64 letters, digits, '-' or '_'");
        }

        if (string.IsNullOrWhiteSpace(Host) && string.IsNullOrWhiteSpace(RtspUrl))
        {
            throw new ArgumentException($"Recording camera '{Id}' needs a Host (ONVIF) or an RtspUrl");
        }

        if (RtspUrl != null && (Uri.TryCreate(RtspUrl, UriKind.Absolute, out var uri) == false || uri.Scheme is not ("rtsp" or "rtsps")))
        {
            throw new ArgumentException($"Recording camera '{Id}' has an invalid RtspUrl");
        }

        if (Quality.HasEncoderSettings && string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException($"Recording camera '{Id}' sets encoder values, which needs ONVIF - give it a Host");
        }

        if ((Quality.Width == null) != (Quality.Height == null))
        {
            throw new ArgumentException($"Recording camera '{Id}' must set Width and Height together");
        }
    }

    // Never print the password.
    public override string ToString() => $"{Id} ({Host ?? RedactedRtspUrl()})";

    private string RedactedRtspUrl() =>
        Uri.TryCreate(RtspUrl, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}{uri.PathAndQuery}" : "?";
}

using System.Globalization;
using System.Text;
using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.Models;

namespace ITBees.Recordings.MediaMtx;

/// <summary>A camera ready to be recorded: its MediaMTX path name and the source URL with credentials.</summary>
internal sealed record MediaMtxSource(string PathName, string SourceUrl);

/// <summary>Renders the MediaMTX configuration file and the systemd unit. Pure functions, so they are easy to test.</summary>
internal static class MediaMtxConfiguration
{
    public const string RecordingsFolder = "recordings";
    public const string ClipsFolder = "clips";

    // Segment names: %path/2026-11-02_12-00-00-123456.mp4 - sortable, the start time is in the name.
    public const string SegmentNamePattern = "%Y-%m-%d_%H-%M-%S-%f";

    public static string BuildConfig(RecordingsOptions options, string storageRoot, IEnumerable<MediaMtxSource> sources)
    {
        var recordPath = $"{storageRoot.TrimEnd('/')}/{RecordingsFolder}/%path/{SegmentNamePattern}";
        var builder = new StringBuilder();
        builder.AppendLine("# Written by ITBees.Recordings - manual changes are overwritten.");
        builder.AppendLine("logLevel: info");
        builder.AppendLine("logDestinations: [stdout]");
        builder.AppendLine("api: true");
        builder.AppendLine($"apiAddress: {Quote(options.ApiAddress)}");
        builder.AppendLine("metrics: false");
        builder.AppendLine("pprof: false");
        builder.AppendLine("playback: true");
        builder.AppendLine($"playbackAddress: {Quote(options.PlaybackAddress)}");
        builder.AppendLine("rtsp: true");
        builder.AppendLine("rtspTransports: [tcp]");
        builder.AppendLine($"rtspAddress: {Quote(options.RtspAddress)}");
        builder.AppendLine("rtmp: false");
        builder.AppendLine("hls: false");
        builder.AppendLine("webrtc: false");
        builder.AppendLine("srt: false");
        builder.AppendLine("authMethod: internal");
        builder.AppendLine("authInternalUsers:");
        // Reading and playback for whoever can reach the (by default local-only) ports, never publishing;
        // the control API only from this machine.
        builder.AppendLine("  - user: any");
        builder.AppendLine("    pass:");
        builder.AppendLine("    ips: []");
        builder.AppendLine("    permissions:");
        builder.AppendLine("      - action: read");
        builder.AppendLine("      - action: playback");
        builder.AppendLine("  - user: any");
        builder.AppendLine("    pass:");
        builder.AppendLine("    ips: ['127.0.0.1', '::1']");
        builder.AppendLine("    permissions:");
        builder.AppendLine("      - action: api");
        builder.AppendLine("pathDefaults:");
        builder.AppendLine("  sourceOnDemand: false");
        builder.AppendLine("  rtspTransport: tcp");
        builder.AppendLine("  record: true");
        builder.AppendLine($"  recordPath: {Quote(recordPath)}");
        builder.AppendLine("  recordFormat: fmp4");
        builder.AppendLine($"  recordPartDuration: {Duration(options.PartDuration)}");
        builder.AppendLine($"  recordSegmentDuration: {Duration(options.SegmentDuration)}");
        builder.AppendLine($"  recordDeleteAfter: {Duration(options.RecordingRetention)}");

        var ordered = sources.OrderBy(x => x.PathName, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0)
        {
            builder.AppendLine("paths: {}");
        }
        else
        {
            builder.AppendLine("paths:");
            foreach (var source in ordered)
            {
                builder.AppendLine($"  {source.PathName}:");
                builder.AppendLine($"    source: {Quote(source.SourceUrl)}");
            }
        }

        return builder.ToString();
    }

    /// <param name="mountPoint">USB mount the recordings go to; the service refuses to start while it is not mounted,
    /// so MediaMTX never fills the SD card with an empty mount directory.</param>
    public static string BuildSystemdUnit(RecordingsOptions options, string binaryPath, string configPath, LinuxIdentity identity, string? mountPoint)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Written by ITBees.Recordings - manual changes are overwritten.");
        builder.AppendLine("[Unit]");
        builder.AppendLine("Description=ITBees.Recordings camera recorder (MediaMTX)");
        builder.AppendLine("After=network-online.target");
        builder.AppendLine("Wants=network-online.target");
        // Keep retrying forever (e.g. while the USB disk is missing).
        builder.AppendLine("StartLimitIntervalSec=0");
        builder.AppendLine();
        builder.AppendLine("[Service]");
        builder.AppendLine($"User={identity.User}");
        builder.AppendLine($"Group={identity.Group}");
        if (mountPoint != null)
        {
            builder.AppendLine($"ExecStartPre=/bin/sh -c 'mountpoint -q \"{mountPoint}\"'");
        }

        builder.AppendLine($"ExecStart={binaryPath} {configPath}");
        builder.AppendLine("Restart=always");
        builder.AppendLine("RestartSec=10");
        // The host application (barrier, payments) always wins over recording.
        builder.AppendLine("Nice=10");
        builder.AppendLine("IOSchedulingClass=best-effort");
        builder.AppendLine("IOSchedulingPriority=7");
        builder.AppendLine("MemoryMax=300M");
        builder.AppendLine("NoNewPrivileges=yes");
        builder.AppendLine();
        builder.AppendLine("[Install]");
        builder.AppendLine("WantedBy=multi-user.target");
        return builder.ToString();
    }

    // Single-quoted YAML scalar: nothing is special inside except the quote itself.
    internal static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    internal static string Duration(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (long)Math.Round(value.TotalSeconds))}s");
}

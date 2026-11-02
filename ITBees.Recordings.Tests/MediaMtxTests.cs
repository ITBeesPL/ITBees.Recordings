using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.MediaMtx;
using ITBees.Recordings.Models;

namespace ITBees.Recordings.Tests;

public class MediaMtxTests
{
    private static readonly LinuxIdentity Identity = new("octopark", "octopark", 999, 999);

    [Fact]
    public void Config_records_every_camera_in_fixed_segments_with_quoted_sources()
    {
        var options = new RecordingsOptions { SegmentDuration = TimeSpan.FromMinutes(15), RecordingRetention = TimeSpan.FromHours(36) };

        var config = MediaMtxConfiguration.BuildConfig(options, "/mnt/usb/itbees-recordings", new[]
        {
            new MediaMtxSource("wjazd", "rtsp://admin:it's%23@192.168.10.3:554/profile1"),
            new MediaMtxSource("wyjazd", "rtsp://admin:x@192.168.10.4:554/profile2"),
        });

        Assert.Contains("  recordPath: '/mnt/usb/itbees-recordings/recordings/%path/%Y-%m-%d_%H-%M-%S-%f'", config);
        Assert.Contains("  recordFormat: fmp4", config);
        Assert.Contains("  recordSegmentDuration: 900s", config);
        Assert.Contains("  recordDeleteAfter: 129600s", config);
        Assert.Contains("  wjazd:\n    source: 'rtsp://admin:it''s%23@192.168.10.3:554/profile1'", config.ReplaceLineEndings("\n"));
        Assert.Contains("apiAddress: '127.0.0.1:9997'", config);
        Assert.Contains("rtmp: false", config);
        Assert.True(config.IndexOf("  wjazd:", StringComparison.Ordinal) < config.IndexOf("  wyjazd:", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_without_cameras_has_an_empty_path_map()
    {
        var config = MediaMtxConfiguration.BuildConfig(new RecordingsOptions(), "/var/lib/itbees-recordings", Array.Empty<MediaMtxSource>());

        Assert.Contains("paths: {}", config);
    }

    [Fact]
    public void Unit_requires_the_usb_mount_only_in_usb_mode()
    {
        var options = new RecordingsOptions();

        var usb = MediaMtxConfiguration.BuildSystemdUnit(options, "/opt/x/mediamtx", "/opt/x/mediamtx.yml", Identity, "/mnt/itbees-recordings");
        var local = MediaMtxConfiguration.BuildSystemdUnit(options, "/opt/x/mediamtx", "/opt/x/mediamtx.yml", Identity, null);

        Assert.Contains("ExecStartPre=/bin/sh -c 'mountpoint -q \"/mnt/itbees-recordings\"'", usb);
        Assert.DoesNotContain("ExecStartPre", local);
        Assert.Contains("ExecStart=/opt/x/mediamtx /opt/x/mediamtx.yml", local);
        Assert.Contains("User=octopark", local);
        Assert.Contains("Restart=always", local);
    }

    [Theory]
    [InlineData("aarch64", "arm64")]
    [InlineData("armv7l", "armv7")]
    [InlineData("armv8l", "armv7")]
    [InlineData("armv6l", "armv6")]
    [InlineData("x86_64", "amd64")]
    [InlineData("riscv64", null)]
    public void Machine_maps_to_the_release_architecture(string machine, string? expected)
    {
        Assert.Equal(expected, MediaMtxRelease.MapArchitecture(machine));
    }

    [Fact]
    public void Binary_is_extracted_from_the_release_archive_and_a_wrong_checksum_is_refused()
    {
        using var temp = new TempDirectory();
        var archive = Path.Combine(temp.Path, "mediamtx.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (name, content) in new[] { ("LICENSE", "MIT"), ("mediamtx", "#!binary"), ("mediamtx.yml", "logLevel: info") })
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(content)) };
                tar.WriteEntry(entry);
            }
        }

        var target = Path.Combine(temp.Path, "mediamtx");
        MediaMtxHost.ExtractBinary(archive, target);

        Assert.Equal("#!binary", File.ReadAllText(target));
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
        MediaMtxHost.VerifyChecksum(archive, sha.ToLowerInvariant());
        Assert.Throws<RecordingsException>(() => MediaMtxHost.VerifyChecksum(archive, new string('0', 64)));
    }

    [Fact]
    public void Paths_are_read_from_current_and_older_api_fields()
    {
        using var document = JsonDocument.Parse("""
            {"itemCount":2,"pageCount":1,"items":[
              {"name":"wjazd","confName":"wjazd","online":true,"onlineTime":"2026-11-02T12:00:00.5+01:00","inboundBytes":123456,"inboundFramesInError":2},
              {"name":"old","ready":false,"readyTime":null,"bytesReceived":7}
            ]}
            """);

        var paths = MediaMtxClient.ParsePaths(document.RootElement);

        Assert.Equal(2, paths.Count);
        Assert.Equal(new MediaMtxPathInfo("wjazd", true, new DateTimeOffset(2026, 11, 2, 11, 0, 0, 500, TimeSpan.Zero), 123456, 2), paths[0]);
        Assert.Equal(new MediaMtxPathInfo("old", false, null, 7, 0), paths[1]);
    }

    [Fact]
    public void Spans_are_parsed_and_sorted()
    {
        using var document = JsonDocument.Parse("""
            [{"start":"2026-11-02T12:15:00+01:00","duration":60.5,"url":"x"},
             {"start":"2026-11-02T12:00:00+01:00","duration":900,"url":"y"}]
            """);

        var spans = MediaMtxClient.ParseSpans(document.RootElement);

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero), spans[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(900), spans[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(60.5), spans[1].Duration);
    }

    [Theory]
    [InlineData("127.0.0.1:9996", "http://127.0.0.1:9996/")]
    [InlineData(":9996", "http://127.0.0.1:9996/")]
    [InlineData("0.0.0.0:9997", "http://127.0.0.1:9997/")]
    [InlineData("192.168.10.2:9996", "http://192.168.10.2:9996/")]
    public void Listen_address_becomes_a_local_url(string address, string expected)
    {
        Assert.Equal(expected, MediaMtxClient.LocalBaseUri(address).ToString());
    }

    [Fact]
    public void Times_are_sent_in_utc_rfc3339()
    {
        Assert.Equal("2026-11-02T11:59:15.250Z", MediaMtxClient.FormatTime(new DateTimeOffset(2026, 11, 2, 12, 59, 15, 250, TimeSpan.FromHours(1))));
    }

    [Fact]
    public async Task Clip_download_asks_for_standard_mp4()
    {
        var handler = new StubHttpHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        using var clients = new RecordingsHttpClients(handler);
        var client = new MediaMtxClient(clients, Microsoft.Extensions.Options.Options.Create(new RecordingsOptions()));
        using var output = new MemoryStream();

        await client.DownloadAsync("wjazd", new DateTimeOffset(2026, 11, 2, 11, 59, 15, TimeSpan.Zero), TimeSpan.FromSeconds(90), output, CancellationToken.None);

        Assert.Equal(3, output.Length);
        Assert.Equal("http://127.0.0.1:9996/get?path=wjazd&start=2026-11-02T11%3A59%3A15.000Z&duration=90&format=mp4", handler.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public void Mount_paths_with_escaped_spaces_are_decoded()
    {
        Assert.Equal("/media/pi/My Disk", LinuxSystem.DecodeMountPath("/media/pi/My\\040Disk"));
        Assert.Equal("/mnt/plain", LinuxSystem.DecodeMountPath("/mnt/plain"));
    }
}

using ITBees.Recordings.MediaMtx;
using ITBees.Recordings.Models;
using ITBees.Recordings.Services;
using ITBees.Recordings.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.Tests;

public class RecordingManagerTests
{
    private readonly FakeLinuxSystem _linux = new();
    private readonly FakeMediaMtxHost _host = new();
    private readonly FakeMediaMtxClient _client = new();
    private readonly FakeStreamSourceResolver _resolver = new();
    private readonly FakeStorageLocator _storage = new() { Location = new StorageLocation("/var/lib/itbees-recordings", null) };
    private readonly FakeStorageGuard _guard = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero));
    private readonly List<RecordingCameraStatusChangedEventArgs> _changes = new();

    private static RecordingCameraDefinition Camera(string id, string host) => new() { Id = id, Host = host, Username = "admin", Password = "x" };

    [Fact]
    public async Task First_pass_writes_the_config_installs_the_unit_and_starts_the_service()
    {
        var manager = CreateManager(Camera("wjazd", "192.168.10.3"));

        await manager.RunMaintenanceAsync(CancellationToken.None);

        Assert.Contains("source: 'rtsp://user:pass@192.168.10.3:554/profile1'", _host.Config);
        Assert.Contains("recordPath: '/var/lib/itbees-recordings/recordings/%path/", _host.Config);
        Assert.Contains("User=octopark", _host.Unit);
        Assert.Equal(1, _host.Restarts);
        Assert.Equal(1, _guard.Calls);
        Assert.Equal(RecordingCameraState.Connecting, manager.GetStatus().Cameras.Single().State);
        Assert.Equal("/var/lib/itbees-recordings/clips".Replace('/', Path.DirectorySeparatorChar), manager.ClipsDirectory!.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Camera_online_in_mediamtx_is_recording_and_nothing_is_restarted_again()
    {
        var manager = CreateManager(Camera("wjazd", "192.168.10.3"));
        await manager.RunMaintenanceAsync(CancellationToken.None);
        _client.Paths.Add(new MediaMtxPathInfo("wjazd", true, _time.Now, 1000, 0));

        await manager.RunMaintenanceAsync(CancellationToken.None);

        var status = manager.GetStatus().Cameras.Single();
        Assert.Equal(RecordingCameraState.Recording, status.State);
        Assert.Equal("profile1: H264 1920x1080", status.StreamDescription);
        Assert.Equal(1, _host.Restarts);
        Assert.Equal(1, _resolver.Calls);
        Assert.Equal(new[] { RecordingCameraState.Connecting, RecordingCameraState.Recording }, _changes.Select(x => x.Status.State));
    }

    [Fact]
    public async Task Camera_that_cannot_be_resolved_is_left_out_and_retried_later()
    {
        _resolver.Errors["wyjazd"] = new OnvifException("GetProfiles", 400, "ter:NotAuthorized", "Sender not Authorized");
        var manager = CreateManager(Camera("wjazd", "192.168.10.3"), Camera("wyjazd", "192.168.10.4"));

        await manager.RunMaintenanceAsync(CancellationToken.None);

        Assert.DoesNotContain("wyjazd", _host.Config);
        var failed = manager.GetStatus().Cameras.Single(x => x.CameraId == "wyjazd");
        Assert.Equal(RecordingCameraState.Error, failed.State);
        Assert.Contains("NotAuthorized", failed.Error);

        _resolver.Errors.Clear();
        await manager.RunMaintenanceAsync(CancellationToken.None);
        Assert.DoesNotContain("wyjazd", _host.Config);

        _time.Advance(TimeSpan.FromMinutes(1));
        await manager.RunMaintenanceAsync(CancellationToken.None);
        Assert.Contains("wyjazd", _host.Config);
    }

    [Fact]
    public async Task Missing_usb_disk_stops_the_recorder()
    {
        var manager = CreateManager(Camera("wjazd", "192.168.10.3"));
        await manager.RunMaintenanceAsync(CancellationToken.None);
        _storage.Location = null;
        _storage.Problem = "No USB disk with a file system is plugged in";

        await manager.RunMaintenanceAsync(CancellationToken.None);

        Assert.Equal(1, _host.Stops);
        Assert.Null(manager.ClipsDirectory);
        var status = manager.GetStatus();
        Assert.Equal(RecordingCameraState.NoStorage, status.Cameras.Single().State);
        Assert.Equal("No USB disk with a file system is plugged in", status.Error);
    }

    [Fact]
    public async Task Without_storage_nothing_is_downloaded()
    {
        _storage.Location = null;
        var manager = CreateManager(Camera("wjazd", "192.168.10.3"));

        await manager.RunMaintenanceAsync(CancellationToken.None);

        Assert.Equal(0, _host.Installs);
        Assert.Null(_host.Config);
    }

    [Fact]
    public async Task Disabling_stops_the_recorder_once_and_enabling_starts_it_again()
    {
        var manager = CreateManager(Camera("wjazd", "192.168.10.3"));
        await manager.RunMaintenanceAsync(CancellationToken.None);
        Assert.True(_host.Active);

        manager.SetEnabled(false);
        await manager.RunMaintenanceAsync(CancellationToken.None);
        await manager.RunMaintenanceAsync(CancellationToken.None);

        Assert.Equal(1, _host.Stops);
        Assert.False(manager.GetStatus().Enabled);
        Assert.Null(manager.ClipsDirectory);

        manager.SetEnabled(true);
        await manager.RunMaintenanceAsync(CancellationToken.None);
        Assert.True(_host.Active);
        Assert.Equal(2, _host.Restarts);
    }

    [Fact]
    public async Task Disabled_at_start_installs_nothing_but_stops_a_leftover_service()
    {
        _host.Active = true;
        var options = new RecordingsOptions { Enabled = false, Cameras = new List<RecordingCameraDefinition> { Camera("wjazd", "192.168.10.3") } };
        var manager = new RecordingManager(Options.Create(options), _linux, _host, _client, _resolver, _storage, _guard, _time, NullLogger<RecordingManager>.Instance);

        await manager.RunMaintenanceAsync(CancellationToken.None);

        Assert.Equal(0, _host.Installs);
        Assert.Equal(1, _host.Stops);
        Assert.Equal(0, _resolver.Calls);
    }

    [Fact]
    public async Task Missed_config_reload_restarts_the_service()
    {
        var manager = CreateManager(Camera("wjazd", "192.168.10.3"));
        await manager.RunMaintenanceAsync(CancellationToken.None);
        _client.Paths.Add(new MediaMtxPathInfo("wjazd", true, _time.Now, 1000, 0));
        await manager.RunMaintenanceAsync(CancellationToken.None);

        manager.Configure(new[] { Camera("wjazd", "192.168.10.3"), Camera("wyjazd", "192.168.10.4") });
        await manager.RunMaintenanceAsync(CancellationToken.None);
        Assert.Equal(1, _host.Restarts);

        _time.Advance(TimeSpan.FromSeconds(61));
        await manager.RunMaintenanceAsync(CancellationToken.None);
        Assert.Equal(2, _host.Restarts);
    }

    [Fact]
    public async Task Not_linux_is_reported_without_touching_anything()
    {
        _linux.IsLinux = false;
        var manager = CreateManager(Camera("wjazd", "192.168.10.3"));

        await manager.RunMaintenanceAsync(CancellationToken.None);

        Assert.Equal(RecordingCameraState.NotSupported, manager.GetStatus().Cameras.Single().State);
        Assert.Null(_host.Config);
    }

    [Fact]
    public void Configuration_is_validated()
    {
        var manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.Configure(new[] { Camera("a", "1.1.1.1"), Camera("A", "1.1.1.2") }));
        Assert.Throws<ArgumentException>(() => manager.Configure(new[] { Camera("bad id", "1.1.1.1") }));
        Assert.Throws<ArgumentException>(() => manager.Configure(new[] { new RecordingCameraDefinition { Id = "a" } }));
        Assert.Throws<ArgumentException>(() => manager.Configure(new[]
        {
            new RecordingCameraDefinition { Id = "a", RtspUrl = "rtsp://1.1.1.1/x", Quality = new RecordingQuality { BitrateKbps = 512 } },
        }));
    }

    [Fact]
    public void Password_never_appears_in_the_camera_text()
    {
        var camera = new RecordingCameraDefinition { Id = "a", RtspUrl = "rtsp://admin:secret@1.1.1.1/x", Password = "secret" };

        Assert.DoesNotContain("secret", camera.ToString());
    }

    private RecordingManager CreateManager(params RecordingCameraDefinition[] cameras)
    {
        var options = new RecordingsOptions { Cameras = cameras.ToList() };
        var manager = new RecordingManager(Options.Create(options), _linux, _host, _client, _resolver, _storage, _guard, _time, NullLogger<RecordingManager>.Instance);
        manager.StatusChanged += (_, e) => _changes.Add(e);
        return manager;
    }
}

using System.Net;
using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.MediaMtx;
using ITBees.Recordings.Models;
using ITBees.Recordings.Onvif;
using ITBees.Recordings.Services;
using ITBees.Recordings.Storage;

namespace ITBees.Recordings.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Temporary directory deleted after the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "itbees-recordings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeLinuxSystem : ILinuxSystem
{
    public bool IsLinux { get; set; } = true;
    public string Machine { get; set; } = "aarch64";
    public LinuxIdentity Identity { get; set; } = new("octopark", "octopark", 999, 999);
    public bool ServiceActive { get; set; }
    public List<string[]> SystemctlCalls { get; } = new();
    public string BlockDevicesJson { get; set; } = "{\"blockdevices\":[]}";
    public HashSet<string> MountPoints { get; } = new();
    public List<(string Device, string MountPoint, string? FileSystem)> Mounts { get; } = new();
    public Func<string, DiskUsage> Usage { get; set; } = _ => new DiskUsage(1, long.MaxValue / 2);

    public Task<string> GetMachineAsync(CancellationToken cancellationToken) => Task.FromResult(Machine);
    public Task<LinuxIdentity> GetIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(Identity);

    public Task EnsureWritableDirectoryAsync(string path, LinuxIdentity owner, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task InstallRootFileAsync(string sourcePath, string targetPath, string mode, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> IsServiceActiveAsync(string serviceName, CancellationToken cancellationToken) => Task.FromResult(ServiceActive);

    public Task SystemctlAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        SystemctlCalls.Add(arguments);
        return Task.CompletedTask;
    }

    public Task<string> ListBlockDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(BlockDevicesJson);

    public Exception? MountError { get; set; }

    public Task MountAsync(string device, string mountPoint, string? fileSystemType, LinuxIdentity owner, CancellationToken cancellationToken)
    {
        if (MountError != null)
        {
            return Task.FromException(MountError);
        }

        Mounts.Add((device, mountPoint, fileSystemType));
        MountPoints.Add(mountPoint);
        Directory.CreateDirectory(mountPoint);
        return Task.CompletedTask;
    }

    public bool IsMountPoint(string path) => MountPoints.Contains(path);
    public DiskUsage GetDiskUsage(string path) => Usage(path);
}

internal sealed class FakeMediaMtxHost : IMediaMtxHost
{
    public string BinaryPath => "/opt/itbees-recordings/mediamtx";
    public string ConfigPath => "/opt/itbees-recordings/mediamtx.yml";
    public bool Active { get; set; }
    public string? Config { get; private set; }
    public string? Unit { get; private set; }
    public int Restarts { get; private set; }
    public int Stops { get; private set; }
    public Exception? InstallError { get; set; }

    public int Installs { get; private set; }

    public Task<bool> EnsureBinaryAsync(CancellationToken cancellationToken)
    {
        Installs++;
        return InstallError != null ? Task.FromException<bool>(InstallError) : Task.FromResult(false);
    }

    public Task<bool> WriteConfigAsync(string content, CancellationToken cancellationToken)
    {
        var changed = Config != content;
        Config = content;
        return Task.FromResult(changed);
    }

    public Task<bool> EnsureUnitAsync(string content, CancellationToken cancellationToken)
    {
        var changed = Unit != content;
        Unit = content;
        return Task.FromResult(changed);
    }

    public Task<bool> IsActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Active);

    public Task RestartAsync(CancellationToken cancellationToken)
    {
        Restarts++;
        Active = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stops++;
        Active = false;
        return Task.CompletedTask;
    }
}

internal sealed class FakeMediaMtxClient : IMediaMtxClient
{
    public List<MediaMtxPathInfo> Paths { get; } = new();
    public Dictionary<string, List<RecordedSpan>> Spans { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, DateTimeOffset Start, TimeSpan Duration)> Downloads { get; } = new();
    public Exception? ListError { get; set; }

    public Task<IReadOnlyList<MediaMtxPathInfo>> GetPathsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaMtxPathInfo>>(Paths.ToList());

    public Task<IReadOnlyList<RecordedSpan>> ListSpansAsync(string pathName, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (ListError != null)
        {
            return Task.FromException<IReadOnlyList<RecordedSpan>>(ListError);
        }

        var spans = Spans.TryGetValue(pathName, out var list) ? list : new List<RecordedSpan>();
        return Task.FromResult<IReadOnlyList<RecordedSpan>>(spans.Where(x => x.End > from && x.Start < to).ToList());
    }

    public async Task DownloadAsync(string pathName, DateTimeOffset start, TimeSpan duration, Stream destination, CancellationToken cancellationToken)
    {
        Downloads.Add((pathName, start, duration));
        var content = System.Text.Encoding.ASCII.GetBytes($"mp4 {pathName} {start:O} {duration.TotalSeconds}");
        await destination.WriteAsync(content, cancellationToken);
    }
}

internal sealed class FakeStreamSourceResolver : IStreamSourceResolver
{
    public Dictionary<string, Exception> Errors { get; } = new();
    public int Calls { get; private set; }

    public Task<ResolvedStream> ResolveAsync(RecordingCameraDefinition camera, CancellationToken cancellationToken)
    {
        Calls++;
        return Errors.TryGetValue(camera.Id, out var error)
            ? Task.FromException<ResolvedStream>(error)
            : Task.FromResult(new ResolvedStream($"rtsp://user:pass@{camera.Host}:554/profile1", $"profile1: H264 1920x1080"));
    }
}

internal sealed class FakeStorageLocator : IStorageLocator
{
    public StorageLocation? Location { get; set; }
    public string? Problem { get; set; }
    public Task<StorageLocation?> LocateAsync(LinuxIdentity identity, CancellationToken cancellationToken) => Task.FromResult(Location);
}

internal sealed class FakeStorageGuard : IStorageGuard
{
    public int Calls { get; private set; }

    public DiskUsage Enforce(StorageLocation location)
    {
        Calls++;
        return new DiskUsage(20L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024);
    }
}

/// <summary>Only what the clip service reads from the manager.</summary>
internal sealed class FakeRecordingManager : IRecordingManager
{
    public event EventHandler<RecordingCameraStatusChangedEventArgs>? StatusChanged;
    public List<RecordingCameraDefinition> CameraList { get; } = new();
    public IReadOnlyCollection<RecordingCameraDefinition> Cameras => CameraList;
    public string? ClipsDirectory { get; set; }
    public bool Enabled { get; private set; } = true;
    public void SetEnabled(bool enabled) => Enabled = enabled;
    public int MaxDiskUsagePercent { get; private set; } = 90;
    public void SetMaxDiskUsagePercent(int percent) => MaxDiskUsagePercent = percent;
    public bool UsbStorage { get; private set; }
    public void SetUsbStorage(bool usbStorage) => UsbStorage = usbStorage;
    public RecordingCameraState State { get; set; } = RecordingCameraState.Recording;

    public void Configure(IEnumerable<RecordingCameraDefinition> cameras)
    {
        CameraList.Clear();
        CameraList.AddRange(cameras);
        StatusChanged?.Invoke(this, new RecordingCameraStatusChangedEventArgs(RecordingCameraState.Pending, new RecordingCameraStatus()));
    }

    public RecordingStatus GetStatus() => new()
    {
        Cameras = CameraList.Select(x => new RecordingCameraStatus { CameraId = x.Id, State = State }).ToList(),
    };

    public Task RunMaintenanceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Answers HTTP requests with a function and records them.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

    public StubHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<(Uri Uri, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
        Requests.Add((request.RequestUri!, body));
        return _respond(request, body);
    }

    public static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/soap+xml") };
}

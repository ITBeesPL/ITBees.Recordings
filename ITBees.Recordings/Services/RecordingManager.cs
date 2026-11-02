using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.MediaMtx;
using ITBees.Recordings.Models;
using ITBees.Recordings.Onvif;
using ITBees.Recordings.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.Services;

internal sealed class RecordingManager : IRecordingManager
{
    private static readonly TimeSpan InstallRetryInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxResolveBackoff = TimeSpan.FromMinutes(10);

    // MediaMTX reloads its configuration by itself; when a camera still has no path this long after a
    // write, the reload was missed and the service is restarted.
    private static readonly TimeSpan ConfigApplyTimeout = TimeSpan.FromSeconds(60);

    private readonly RecordingsOptions _options;
    private readonly ILinuxSystem _linuxSystem;
    private readonly IMediaMtxHost _mediaMtxHost;
    private readonly IMediaMtxClient _mediaMtxClient;
    private readonly IStreamSourceResolver _streamSourceResolver;
    private readonly IStorageLocator _storageLocator;
    private readonly IStorageGuard _storageGuard;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecordingManager> _logger;
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);
    private readonly object _configurationLock = new();

    // Replaced as a whole on reconfiguration, so readers need no lock.
    private volatile Dictionary<string, CameraSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LinuxIdentity? _identity;
    private bool _binaryReady;
    private bool _binaryChanged;
    private DateTimeOffset _installRetryAt;
    private volatile StorageLocation? _storage;
    private DateTimeOffset? _configWrittenAt;
    private bool _serviceActive;
    private DiskUsage? _diskUsage;
    private string? _error;
    private volatile bool _enabled;
    private bool _disabledHandled;

    public RecordingManager(
        IOptions<RecordingsOptions> options,
        ILinuxSystem linuxSystem,
        IMediaMtxHost mediaMtxHost,
        IMediaMtxClient mediaMtxClient,
        IStreamSourceResolver streamSourceResolver,
        IStorageLocator storageLocator,
        IStorageGuard storageGuard,
        TimeProvider timeProvider,
        ILogger<RecordingManager> logger)
    {
        _options = options.Value;
        _linuxSystem = linuxSystem;
        _mediaMtxHost = mediaMtxHost;
        _mediaMtxClient = mediaMtxClient;
        _streamSourceResolver = streamSourceResolver;
        _storageLocator = storageLocator;
        _storageGuard = storageGuard;
        _timeProvider = timeProvider;
        _logger = logger;
        _enabled = _options.Enabled;
        Configure(_options.Cameras);
    }

    public event EventHandler<RecordingCameraStatusChangedEventArgs>? StatusChanged;

    public IReadOnlyCollection<RecordingCameraDefinition> Cameras => _slots.Values.Select(x => x.Definition).ToList();

    public string? ClipsDirectory => _storage?.ClipsDirectory;

    /// <summary>Completes when <see cref="Configure"/> changed something - wakes the maintenance loop early.</summary>
    internal Task Changed => _changed.Task;

    public void Configure(IEnumerable<RecordingCameraDefinition> cameras)
    {
        var definitions = cameras.ToList();
        foreach (var definition in definitions)
        {
            definition.Validate();
        }

        var duplicate = definitions.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null)
        {
            throw new ArgumentException($"Recording camera id '{duplicate.Key}' is used more than once");
        }

        lock (_configurationLock)
        {
            var slots = new Dictionary<string, CameraSlot>(StringComparer.OrdinalIgnoreCase);
            var changed = definitions.Count != _slots.Count;
            foreach (var definition in definitions)
            {
                if (_slots.TryGetValue(definition.Id, out var existing) && existing.Definition == definition)
                {
                    slots[definition.Id] = existing;
                    continue;
                }

                slots[definition.Id] = new CameraSlot(definition with { Quality = definition.Quality with { } });
                changed = true;
                _logger.LogInformation("Recording camera {Camera} configured ({Quality})", definition, definition.Quality);
            }

            foreach (var removed in _slots.Keys.Where(x => slots.ContainsKey(x) == false))
            {
                _logger.LogInformation("Recording camera {CameraId} removed", removed);
            }

            _slots = slots;
            if (changed)
            {
                SignalChange();
            }
        }
    }

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        _logger.LogInformation("Recording {State}", enabled ? "enabled" : "disabled");
        SignalChange();
    }

    private void SignalChange() =>
        Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public RecordingStatus GetStatus() => new()
    {
        Enabled = _enabled,
        Supported = _options.ManageMediaMtx == false || _linuxSystem.IsLinux,
        Installed = _binaryReady,
        ServiceActive = _serviceActive,
        StorageDirectory = _storage?.Root,
        FreeSpaceBytes = _diskUsage?.AvailableBytes,
        DiskUsedPercent = _diskUsage?.UsedPercent,
        Error = _error,
        Cameras = _slots.Values.Select(x => x.Status).OrderBy(x => x.CameraId, StringComparer.OrdinalIgnoreCase).ToList(),
    };

    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            if (_enabled == false)
            {
                await HandleDisabledAsync(cancellationToken);
                return;
            }

            _disabledHandled = false;
            if (_options.ManageMediaMtx)
            {
                if (await EnsureRecorderAsync(cancellationToken) == false)
                {
                    return;
                }
            }
            else
            {
                _storage ??= UnmanagedStorage();
                await ResolveCamerasAsync(cancellationToken);
            }

            await RefreshStatusAsync(cancellationToken);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    /// <summary>Brings install, storage, configuration and service to the wanted state. False = cannot record now.</summary>
    private async Task<bool> EnsureRecorderAsync(CancellationToken cancellationToken)
    {
        if (_linuxSystem.IsLinux == false)
        {
            _error = "Recording runs on Linux only";
            SetAll(RecordingCameraState.NotSupported, _error);
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        _identity ??= await _linuxSystem.GetIdentityAsync(cancellationToken);

        // Storage first: a device without the USB disk downloads nothing.
        StorageLocation? storage;
        try
        {
            storage = await _storageLocator.LocateAsync(_identity, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Preparing the recording storage failed");
            storage = null;
        }

        if (storage == null)
        {
            await HandleMissingStorageAsync(cancellationToken);
            return false;
        }

        try
        {
            if (_binaryReady == false)
            {
                if (now < _installRetryAt)
                {
                    return false;
                }

                _binaryChanged = await _mediaMtxHost.EnsureBinaryAsync(cancellationToken);
                _binaryReady = true;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _installRetryAt = now + InstallRetryInterval;
            _error = $"Installing MediaMTX failed: {e.Message}";
            _logger.LogError(e, "Installing MediaMTX failed, next attempt at {RetryAt}", _installRetryAt);
            SetAll(RecordingCameraState.Error, _error);
            return false;
        }

        if (_storage?.Root != storage.Root)
        {
            _logger.LogInformation("Recording to {Root}", storage.Root);
        }

        _storage = storage;
        await ResolveCamerasAsync(cancellationToken);

        var sources = _slots.Values.Where(x => x.Stream != null).Select(x => new MediaMtxSource(x.Definition.Id, x.Stream!.SourceUrl));
        var configChanged = await _mediaMtxHost.WriteConfigAsync(MediaMtxConfiguration.BuildConfig(_options, storage.Root, sources), cancellationToken);
        var unit = MediaMtxConfiguration.BuildSystemdUnit(_options, _mediaMtxHost.BinaryPath, _mediaMtxHost.ConfigPath, _identity, storage.MountPoint);
        var unitChanged = await _mediaMtxHost.EnsureUnitAsync(unit, cancellationToken);

        _serviceActive = await _mediaMtxHost.IsActiveAsync(cancellationToken);
        if (_serviceActive == false || unitChanged || _binaryChanged)
        {
            _logger.LogInformation("Starting the recorder service {Service}", _options.ServiceName);
            await _mediaMtxHost.RestartAsync(cancellationToken);
            _binaryChanged = false;
            _configWrittenAt = null;
            _serviceActive = true;
        }
        else if (configChanged)
        {
            _configWrittenAt = now;
        }

        _diskUsage = _storageGuard.Enforce(storage);
        _error = null;
        return true;
    }

    /// <summary>Once per switch-off (and once after a start disabled): stop a recorder left running, e.g. by a device that was the recorder before.</summary>
    private async Task HandleDisabledAsync(CancellationToken cancellationToken)
    {
        if (_disabledHandled)
        {
            return;
        }

        _disabledHandled = true;
        _storage = null;
        _diskUsage = null;
        _error = null;
        _serviceActive = false;
        if (_options.ManageMediaMtx == false || _linuxSystem.IsLinux == false)
        {
            return;
        }

        try
        {
            if (await _mediaMtxHost.IsActiveAsync(cancellationToken))
            {
                _logger.LogInformation("Recording disabled - stopping the recorder service {Service}", _options.ServiceName);
                await _mediaMtxHost.StopAsync(cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _disabledHandled = false;
            _logger.LogWarning("Stopping the recorder service failed: {Error}", e.Message);
        }
    }

    private async Task HandleMissingStorageAsync(CancellationToken cancellationToken)
    {
        _storage = null;
        _diskUsage = null;
        _error = _storageLocator.Problem ?? "No storage for recordings";
        SetAll(RecordingCameraState.NoStorage, _error);

        // The unit refuses to start without the USB mount, but a disk pulled out while recording leaves
        // MediaMTX writing into the empty mount directory on the SD card - stop it.
        _serviceActive = await _mediaMtxHost.IsActiveAsync(cancellationToken);
        if (_serviceActive)
        {
            _logger.LogWarning("Stopping the recorder: {Problem}", _error);
            await _mediaMtxHost.StopAsync(cancellationToken);
            _serviceActive = false;
        }
    }

    private StorageLocation? UnmanagedStorage()
    {
        var location = new StorageLocation(_options.StorageDirectory, null);
        try
        {
            Directory.CreateDirectory(location.ClipsDirectory);
            return location;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Clip directory {Directory} cannot be created", location.ClipsDirectory);
            return null;
        }
    }

    private async Task ResolveCamerasAsync(CancellationToken cancellationToken)
    {
        foreach (var slot in _slots.Values)
        {
            var now = _timeProvider.GetUtcNow();
            if (slot.Stream != null || now < slot.NextResolveAt)
            {
                continue;
            }

            try
            {
                slot.Stream = await _streamSourceResolver.ResolveAsync(slot.Definition, cancellationToken);
                slot.Error = null;
                slot.Failures = 0;
                _logger.LogInformation("Recording camera {Camera}: {Stream}", slot.Definition, slot.Stream.Description ?? RtspUrls.Redact(slot.Stream.SourceUrl));
            }
            catch (Exception e) when (e is not OperationCanceledException || cancellationToken.IsCancellationRequested == false)
            {
                slot.Failures++;
                slot.Error = e.Message;
                var backoff = TimeSpan.FromTicks(Math.Min(MaxResolveBackoff.Ticks, TimeSpan.FromMinutes(1).Ticks << Math.Min(slot.Failures - 1, 4)));
                slot.NextResolveAt = now + backoff;
                _logger.LogWarning("Recording camera {Camera} not ready ({Error}), next attempt in {Backoff}", slot.Definition, e.Message, backoff);
            }
        }
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaMtxPathInfo>? paths = null;
        string? apiError = null;
        try
        {
            paths = await _mediaMtxClient.GetPathsAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || cancellationToken.IsCancellationRequested == false)
        {
            apiError = $"MediaMTX API not reachable: {e.Message}";
        }

        var missingPath = false;
        foreach (var slot in _slots.Values)
        {
            var path = paths?.FirstOrDefault(x => string.Equals(x.Name, slot.Definition.Id, StringComparison.OrdinalIgnoreCase));
            if (slot.Error != null)
            {
                Update(slot, RecordingCameraState.Error, slot.Error);
                continue;
            }

            if (slot.Stream == null)
            {
                Update(slot, RecordingCameraState.Pending, null);
                continue;
            }

            missingPath |= paths != null && path == null;
            Update(slot, path?.Online == true ? RecordingCameraState.Recording : RecordingCameraState.Connecting, path == null ? apiError : null, path);
        }

        if (missingPath && _options.ManageMediaMtx && _configWrittenAt != null && _timeProvider.GetUtcNow() - _configWrittenAt > ConfigApplyTimeout)
        {
            _logger.LogWarning("MediaMTX did not pick up the new configuration, restarting {Service}", _options.ServiceName);
            _configWrittenAt = null;
            await _mediaMtxHost.RestartAsync(cancellationToken);
        }
    }

    private void SetAll(RecordingCameraState state, string? error)
    {
        foreach (var slot in _slots.Values)
        {
            Update(slot, state, error);
        }
    }

    private void Update(CameraSlot slot, RecordingCameraState state, string? error, MediaMtxPathInfo? path = null)
    {
        var previous = slot.Status;
        slot.Status = previous with
        {
            State = state,
            Error = error,
            OnlineSince = state == RecordingCameraState.Recording ? path?.OnlineTime : null,
            InboundBytes = path?.InboundBytes ?? 0,
            FramesInError = path?.FramesInError ?? 0,
            StreamDescription = slot.Stream?.Description,
        };

        if (previous.State == state)
        {
            return;
        }

        if (state is RecordingCameraState.Error or RecordingCameraState.NoStorage)
        {
            _logger.LogWarning("Recording camera {CameraId}: {Previous} -> {State} ({Error})", slot.Definition.Id, previous.State, state, error);
        }
        else
        {
            _logger.LogInformation("Recording camera {CameraId}: {Previous} -> {State}", slot.Definition.Id, previous.State, state);
        }

        try
        {
            StatusChanged?.Invoke(this, new RecordingCameraStatusChangedEventArgs(previous.State, slot.Status));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Recording status handler failed");
        }
    }

    private sealed class CameraSlot
    {
        public CameraSlot(RecordingCameraDefinition definition)
        {
            Definition = definition;
            Status = new RecordingCameraStatus { CameraId = definition.Id };
        }

        public RecordingCameraDefinition Definition { get; }
        public ResolvedStream? Stream { get; set; }
        public string? Error { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset NextResolveAt { get; set; }
        public RecordingCameraStatus Status { get; set; }
    }
}

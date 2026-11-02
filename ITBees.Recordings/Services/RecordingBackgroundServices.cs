using ITBees.Recordings.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.Services;

/// <summary>
/// Keeps the recorder (the MediaMTX systemd service) in the wanted state. Runs at once, then every
/// <see cref="RecordingsOptions.MaintenanceInterval"/> and right after the camera set changes.
/// </summary>
internal sealed class RecordingMaintenanceService : BackgroundService
{
    private readonly IRecordingManager _recordingManager;
    private readonly TimeSpan _interval;
    private readonly ILogger<RecordingMaintenanceService> _logger;

    public RecordingMaintenanceService(IRecordingManager recordingManager, IOptions<RecordingsOptions> options, ILogger<RecordingMaintenanceService> logger)
    {
        _recordingManager = recordingManager;
        _interval = options.Value.MaintenanceInterval > TimeSpan.Zero ? options.Value.MaintenanceInterval : TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            // A replaced manager (tests, host override) has no change signal - it is then only polled.
            var changed = _recordingManager is RecordingManager manager ? manager.Changed : Task.Delay(Timeout.Infinite, stoppingToken);
            try
            {
                await _recordingManager.RunMaintenanceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Recording maintenance pass failed");
            }

            try
            {
                await Task.WhenAny(Task.Delay(_interval, stoppingToken), changed);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>The clipper: cuts queued event clips as soon as they are due.</summary>
internal sealed class EventClipWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    private readonly IEventClipService _eventClipService;
    private readonly ILogger<EventClipWorker> _logger;

    public EventClipWorker(IEventClipService eventClipService, ILogger<EventClipWorker> logger)
    {
        _eventClipService = eventClipService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _eventClipService.ProcessDueJobsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Clip pass failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
    }
}

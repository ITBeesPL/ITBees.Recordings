using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.MediaMtx;
using ITBees.Recordings.Models;
using ITBees.Recordings.Onvif;
using ITBees.Recordings.Services;
using ITBees.Recordings.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ITBees.Recordings.Setup;

public static class RecordingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRecordingManager"/> and <see cref="IEventClipService"/> (singletons) and their two
    /// background services: the recorder maintenance (MediaMTX install, configuration, systemd service,
    /// free space) and the clipper.
    /// </summary>
    /// <param name="configurationSection">Optional section bound to <see cref="RecordingsOptions"/>, usually <c>Recordings</c>.</param>
    /// <param name="configure">Optional code configuration, applied after the section.</param>
    public static IServiceCollection AddRecordings(
        this IServiceCollection services,
        IConfiguration? configurationSection = null,
        Action<RecordingsOptions>? configure = null)
    {
        var options = services.AddOptions<RecordingsOptions>();
        if (configurationSection != null)
        {
            options.Bind(configurationSection);
        }

        if (configure != null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICommandRunner, CommandRunner>();
        services.TryAddSingleton<ILinuxSystem, LinuxSystem>();
        services.TryAddSingleton<RecordingsHttpClients>();
        services.TryAddSingleton<IMediaMtxHost, MediaMtxHost>();
        services.TryAddSingleton<IMediaMtxClient, MediaMtxClient>();
        services.TryAddSingleton<IOnvifClientFactory, OnvifClientFactory>();
        services.TryAddSingleton<IStreamSourceResolver, StreamSourceResolver>();
        services.TryAddSingleton<IStorageLocator, StorageLocator>();
        services.TryAddSingleton<IStorageGuard, StorageGuard>();
        services.TryAddSingleton<IRecordingManager, RecordingManager>();
        services.TryAddSingleton<IEventClipService, EventClipService>();
        services.AddHostedService<RecordingMaintenanceService>();
        services.AddHostedService<EventClipWorker>();
        return services;
    }
}

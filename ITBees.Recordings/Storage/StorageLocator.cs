using System.Text.Json;
using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.MediaMtx;
using ITBees.Recordings.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.Storage;

/// <summary>Where recordings and clips go. <see cref="MountPoint"/> is set for a USB disk.</summary>
internal sealed record StorageLocation(string Root, string? MountPoint)
{
    public string RecordingsDirectory => Path.Combine(Root, MediaMtxConfiguration.RecordingsFolder);
    public string ClipsDirectory => Path.Combine(Root, MediaMtxConfiguration.ClipsFolder);
}

/// <summary>A partition from <c>lsblk</c>.</summary>
internal sealed record BlockPartition(string Device, string? FileSystem, string? Label, string? MountPoint, long Size, bool IsUsb);

internal interface IStorageLocator
{
    /// <summary>The storage to use, or null with <see cref="Problem"/> set (e.g. no USB disk).</summary>
    Task<StorageLocation?> LocateAsync(LinuxIdentity identity, CancellationToken cancellationToken);

    string? Problem { get; }
}

internal sealed class StorageLocator : IStorageLocator
{
    private const string UsbRootFolder = "itbees-recordings";
    private static readonly HashSet<string> UnusableFileSystems = new(StringComparer.OrdinalIgnoreCase) { "swap", "linux_raid_member", "LVM2_member", "crypto_LUKS", "iso9660" };

    private readonly RecordingsOptions _options;
    private readonly ILinuxSystem _linuxSystem;
    private readonly ILogger<StorageLocator> _logger;

    // Null until the first pass; the fallback to the device is logged only when it starts.
    private bool? _usbInUse;

    public StorageLocator(IOptions<RecordingsOptions> options, ILinuxSystem linuxSystem, ILogger<StorageLocator> logger)
    {
        _options = options.Value;
        _linuxSystem = linuxSystem;
        _logger = logger;
    }

    public string? Problem { get; private set; }

    public async Task<StorageLocation?> LocateAsync(LinuxIdentity identity, CancellationToken cancellationToken)
    {
        var location = new StorageLocation(_options.StorageDirectory, null);
        if (_options.UsbStorage)
        {
            string? mountPoint;
            try
            {
                mountPoint = await FindUsbMountAsync(identity, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException && _options.UsbFallbackToStorageDirectory)
            {
                Problem = $"USB disk cannot be used: {e.Message}";
                mountPoint = null;
            }

            if (mountPoint != null)
            {
                location = new StorageLocation(Path.Combine(mountPoint, UsbRootFolder), mountPoint);
            }
            else if (_options.UsbFallbackToStorageDirectory == false)
            {
                return null;
            }
            else if (_usbInUse != false)
            {
                _logger.LogWarning("{Problem} - recording to {Directory} on the device until a USB disk is plugged in", Problem, _options.StorageDirectory);
            }

            _usbInUse = mountPoint != null;
        }

        await _linuxSystem.EnsureWritableDirectoryAsync(location.Root, identity, cancellationToken);
        Directory.CreateDirectory(location.RecordingsDirectory);
        Directory.CreateDirectory(location.ClipsDirectory);
        Problem = null;
        return location;
    }

    private async Task<string?> FindUsbMountAsync(LinuxIdentity identity, CancellationToken cancellationToken)
    {
        var partitions = ParseLsblk(await _linuxSystem.ListBlockDevicesAsync(cancellationToken));
        var partition = ChoosePartition(partitions, _options.UsbLabel);
        if (partition == null)
        {
            Problem = _options.UsbLabel == null ? "No USB disk with a file system is plugged in" : $"No USB partition labelled '{_options.UsbLabel}'";
            return null;
        }

        if (partition.MountPoint != null)
        {
            return partition.MountPoint;
        }

        _logger.LogInformation("Mounting USB partition {Device} ({FileSystem}, {Label}) at {MountPoint}", partition.Device, partition.FileSystem, partition.Label, _options.UsbMountPoint);
        await _linuxSystem.MountAsync(partition.Device, _options.UsbMountPoint, partition.FileSystem, identity, cancellationToken);
        if (_linuxSystem.IsMountPoint(_options.UsbMountPoint) == false)
        {
            Problem = $"Mounting {partition.Device} at {_options.UsbMountPoint} did not succeed";
            return null;
        }

        return _options.UsbMountPoint;
    }

    /// <summary>Mounted partitions first (the desktop may have mounted the disk already), then the largest.</summary>
    internal static BlockPartition? ChoosePartition(IReadOnlyList<BlockPartition> partitions, string? label) =>
        partitions
            .Where(x => x.IsUsb && x.FileSystem != null && UnusableFileSystems.Contains(x.FileSystem) == false)
            .Where(x => label == null || string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.MountPoint is null || IsSystemMount(x.MountPoint) == false)
            .OrderByDescending(x => x.MountPoint != null)
            .ThenByDescending(x => x.Size)
            .FirstOrDefault();

    // A USB-booted device has its root on USB - never treat that as the recording disk.
    private static bool IsSystemMount(string mountPoint) => mountPoint is "/" or "/boot" or "/boot/firmware" || mountPoint.StartsWith("[SWAP]", StringComparison.Ordinal);

    /// <summary>
    /// Parses <c>lsblk -J -b -o NAME,PATH,TRAN,TYPE,FSTYPE,MOUNTPOINT,LABEL,UUID,SIZE</c>. Only disks carry
    /// TRAN, so partitions inherit it from their disk.
    /// </summary>
    internal static IReadOnlyList<BlockPartition> ParseLsblk(string json)
    {
        var result = new List<BlockPartition>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("blockdevices", out var devices) == false)
        {
            return result;
        }

        void Walk(JsonElement device, bool parentUsb)
        {
            var usb = parentUsb || string.Equals(GetString(device, "tran"), "usb", StringComparison.OrdinalIgnoreCase);
            var fileSystem = GetString(device, "fstype");
            if (fileSystem != null)
            {
                var name = GetString(device, "name") ?? string.Empty;
                result.Add(new BlockPartition(
                    GetString(device, "path") ?? "/dev/" + name,
                    fileSystem,
                    GetString(device, "label"),
                    GetString(device, "mountpoint"),
                    GetSize(device),
                    usb));
            }

            if (device.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    Walk(child, usb);
                }
            }
        }

        foreach (var device in devices.EnumerateArray())
        {
            Walk(device, false);
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    // Older lsblk prints numbers as strings in JSON.
    private static long GetSize(JsonElement element)
    {
        if (element.TryGetProperty("size", out var value) == false)
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => 0,
        };
    }
}

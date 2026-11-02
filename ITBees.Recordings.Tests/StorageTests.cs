using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.Models;
using ITBees.Recordings.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.Tests;

public class StorageTests
{
    // lsblk 2.36 on Raspberry Pi OS: SD card + a USB disk with one partition; TRAN only on disks.
    private const string Lsblk = """
        {"blockdevices": [
          {"name":"sda","path":"/dev/sda","tran":"usb","type":"disk","fstype":null,"mountpoint":null,"label":null,"uuid":null,"size":"1000204886016",
           "children":[{"name":"sda1","path":"/dev/sda1","tran":null,"type":"part","fstype":"exfat","mountpoint":null,"label":"NAGRANIA","uuid":"1234-ABCD","size":"1000202788864"}]},
          {"name":"mmcblk0","path":"/dev/mmcblk0","tran":null,"type":"disk","fstype":null,"mountpoint":null,"label":null,"uuid":null,"size":31914983424,
           "children":[
             {"name":"mmcblk0p1","path":"/dev/mmcblk0p1","tran":null,"type":"part","fstype":"vfat","mountpoint":"/boot","label":"boot","uuid":"A1","size":268435456},
             {"name":"mmcblk0p2","path":"/dev/mmcblk0p2","tran":null,"type":"part","fstype":"ext4","mountpoint":"/","label":"rootfs","uuid":"B2","size":31646547968}]}
        ]}
        """;

    [Fact]
    public void Usb_partitions_inherit_the_transport_of_their_disk()
    {
        var partitions = StorageLocator.ParseLsblk(Lsblk);

        var usb = Assert.Single(partitions, x => x.IsUsb);
        Assert.Equal(new BlockPartition("/dev/sda1", "exfat", "NAGRANIA", null, 1000202788864, true), usb);
        Assert.Equal(3, partitions.Count);
    }

    [Fact]
    public void Mounted_usb_partition_wins_and_system_mounts_are_never_used()
    {
        var partitions = new[]
        {
            new BlockPartition("/dev/sda1", "ext4", "BIG", null, 2_000, true),
            new BlockPartition("/dev/sdb1", "exfat", "SMALL", "/media/pi/SMALL", 1_000, true),
            new BlockPartition("/dev/sdc2", "ext4", "rootfs", "/", 5_000, true),
            new BlockPartition("/dev/sdd1", "swap", null, null, 9_000, true),
            new BlockPartition("/dev/mmcblk0p2", "ext4", "rootfs", null, 9_000, false),
        };

        Assert.Equal("/dev/sdb1", StorageLocator.ChoosePartition(partitions, null)!.Device);
        Assert.Equal("/dev/sda1", StorageLocator.ChoosePartition(partitions, "big")!.Device);
        Assert.Null(StorageLocator.ChoosePartition(partitions, "missing"));
    }

    [Fact]
    public async Task Unmounted_usb_disk_is_mounted_at_the_configured_point()
    {
        using var temp = new TempDirectory();
        var mountPoint = Path.Combine(temp.Path, "mnt");
        var linux = new FakeLinuxSystem { BlockDevicesJson = Lsblk };
        var locator = new StorageLocator(Options.Create(new RecordingsOptions { UsbStorage = true, UsbMountPoint = mountPoint }), linux, NullLogger<StorageLocator>.Instance);

        var location = await locator.LocateAsync(linux.Identity, CancellationToken.None);

        Assert.Equal(("/dev/sda1", mountPoint, "exfat"), linux.Mounts.Single());
        Assert.Equal(Path.Combine(mountPoint, "itbees-recordings"), location!.Root);
        Assert.Equal(mountPoint, location.MountPoint);
        Assert.True(Directory.Exists(location.RecordingsDirectory));
        Assert.True(Directory.Exists(location.ClipsDirectory));
    }

    [Fact]
    public async Task Without_a_usb_disk_there_is_no_storage_and_no_sd_card_fallback()
    {
        var linux = new FakeLinuxSystem { BlockDevicesJson = """{"blockdevices":[{"name":"mmcblk0","tran":null,"fstype":"ext4","mountpoint":"/"}]}""" };
        var locator = new StorageLocator(Options.Create(new RecordingsOptions { UsbStorage = true }), linux, NullLogger<StorageLocator>.Instance);

        Assert.Null(await locator.LocateAsync(linux.Identity, CancellationToken.None));
        Assert.Equal("No USB disk with a file system is plugged in", locator.Problem);
        Assert.Empty(linux.Mounts);
    }

    private const long Mb = 1024 * 1024;

    [Fact]
    public void Over_the_limit_the_oldest_files_are_overwritten_clips_and_recordings_alike()
    {
        using var temp = new TempDirectory();
        var location = new StorageLocation(temp.Path, null);
        var cam1Old = CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "a.mp4"), Mb, hoursAgo: 5);
        var cam1New = CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "b.mp4"), Mb, hoursAgo: 0);
        var cam2Old = CreateFile(Path.Combine(location.RecordingsDirectory, "cam2", "a.mp4"), Mb, hoursAgo: 4);
        var cam2Mid = CreateFile(Path.Combine(location.RecordingsDirectory, "cam2", "b.mp4"), Mb, hoursAgo: 3);
        var cam2New = CreateFile(Path.Combine(location.RecordingsDirectory, "cam2", "c.mp4"), Mb, hoursAgo: 0);
        var clip = CreateFile(Path.Combine(location.ClipsDirectory, "2026-11-02", "clip.mp4"), Mb, hoursAgo: 6);
        var clipMetadata = CreateFile(Path.ChangeExtension(clip, ".json"), 100, hoursAgo: 6);

        // 95 % of 100 MB used, limit 90 %: down to 89 % = 6 MB to free - more than all candidates hold.
        var linux = new FakeLinuxSystem { Usage = _ => new DiskUsage(95 * Mb, 5 * Mb) };
        var guard = new StorageGuard(Options.Create(new RecordingsOptions { MaxDiskUsagePercent = 90 }), linux, TimeProvider.System, NullLogger<StorageGuard>.Instance);

        var order = StorageGuard.DeletionCandidates(location).Select(x => x.FullName).ToList();
        Assert.Equal(new[] { clip, cam1Old, cam2Old, cam2Mid }, order);

        guard.Enforce(location);

        Assert.All(new[] { clip, clipMetadata, cam1Old, cam2Old, cam2Mid }, x => Assert.False(File.Exists(x)));
        Assert.True(File.Exists(cam1New));
        Assert.True(File.Exists(cam2New));
    }

    [Fact]
    public void Overwriting_stops_one_point_under_the_limit()
    {
        using var temp = new TempDirectory();
        var location = new StorageLocation(temp.Path, null);
        var first = CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "a.mp4"), Mb, hoursAgo: 5);
        var second = CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "b.mp4"), Mb, hoursAgo: 4);
        var third = CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "c.mp4"), Mb, hoursAgo: 3);
        CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "d.mp4"), Mb, hoursAgo: 0);

        // 91 MB of 100 MB, limit 90 %: 2 MB to free.
        var linux = new FakeLinuxSystem { Usage = _ => new DiskUsage(91 * Mb, 9 * Mb) };
        var guard = new StorageGuard(Options.Create(new RecordingsOptions { MaxDiskUsagePercent = 90 }), linux, TimeProvider.System, NullLogger<StorageGuard>.Instance);

        guard.Enforce(location);

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(third));
    }

    [Fact]
    public void Under_the_limit_nothing_is_deleted()
    {
        using var temp = new TempDirectory();
        var location = new StorageLocation(temp.Path, null);
        var old = CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "a.mp4"), Mb, hoursAgo: 500);
        CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "b.mp4"), Mb, hoursAgo: 0);
        var linux = new FakeLinuxSystem { Usage = _ => new DiskUsage(89 * Mb, 11 * Mb) };
        var guard = new StorageGuard(Options.Create(new RecordingsOptions { MaxDiskUsagePercent = 90 }), linux, TimeProvider.System, NullLogger<StorageGuard>.Instance);

        var usage = guard.Enforce(location);

        Assert.True(File.Exists(old));
        Assert.Equal(89, usage.UsedPercent, 3);
    }

    [Fact]
    public void Storage_cap_overwrites_even_on_an_empty_disk()
    {
        using var temp = new TempDirectory();
        var location = new StorageLocation(temp.Path, null);
        var first = CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "a.mp4"), 2 * Mb, hoursAgo: 5);
        var second = CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "b.mp4"), 2 * Mb, hoursAgo: 4);
        CreateFile(Path.Combine(location.RecordingsDirectory, "cam1", "c.mp4"), 2 * Mb, hoursAgo: 0);
        var guard = new StorageGuard(Options.Create(new RecordingsOptions { MaxStorageMegabytes = 5 }), new FakeLinuxSystem(), TimeProvider.System, NullLogger<StorageGuard>.Instance);

        guard.Enforce(location);

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void Expired_clips_are_deleted_with_their_empty_folders()
    {
        using var temp = new TempDirectory();
        var location = new StorageLocation(temp.Path, null);
        var expired = CreateFile(Path.Combine(location.ClipsDirectory, "cam1", "2026-10-01", "old.mp4"), 10, hoursAgo: 24 * 40);
        var fresh = CreateFile(Path.Combine(location.ClipsDirectory, "cam1", "2026-11-01", "new.mp4"), 10, hoursAgo: 24);
        var guard = new StorageGuard(Options.Create(new RecordingsOptions { ClipRetention = TimeSpan.FromDays(30) }), new FakeLinuxSystem(), TimeProvider.System, NullLogger<StorageGuard>.Instance);

        guard.Enforce(location);

        Assert.False(File.Exists(expired));
        Assert.False(Directory.Exists(Path.GetDirectoryName(expired)));
        Assert.True(File.Exists(fresh));
    }

    private static string CreateFile(string path, long size, double hoursAgo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path))
        {
            stream.SetLength(size);
        }

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hoursAgo));
        return path;
    }
}

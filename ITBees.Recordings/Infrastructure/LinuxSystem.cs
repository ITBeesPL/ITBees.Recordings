using System.Globalization;
using ITBees.Recordings.Models;

namespace ITBees.Recordings.Infrastructure;

/// <summary>Account the host application (and therefore MediaMTX) runs as.</summary>
internal sealed record LinuxIdentity(string User, string Group, int Uid, int Gid);

/// <summary>Space of a file system as <c>df</c> shows it (the root-reserved blocks count neither as used nor as available).</summary>
internal sealed record DiskUsage(long UsedBytes, long AvailableBytes)
{
    public double UsedPercent => UsedBytes + AvailableBytes == 0 ? 0 : 100.0 * UsedBytes / (UsedBytes + AvailableBytes);
}

/// <summary>
/// The few operating-system operations the recorder needs. Privileged ones go through non-interactive
/// <c>sudo -n</c>, like the other device helpers of the host application (it has passwordless sudo).
/// </summary>
internal interface ILinuxSystem
{
    bool IsLinux { get; }
    Task<string> GetMachineAsync(CancellationToken cancellationToken);
    Task<LinuxIdentity> GetIdentityAsync(CancellationToken cancellationToken);

    /// <summary>Creates the directory owned by <paramref name="owner"/> unless it already exists and is writable.</summary>
    Task EnsureWritableDirectoryAsync(string path, LinuxIdentity owner, CancellationToken cancellationToken);

    /// <summary>Copies a file to a root-owned location (e.g. a systemd unit) with the given octal mode.</summary>
    Task InstallRootFileAsync(string sourcePath, string targetPath, string mode, CancellationToken cancellationToken);

    Task<bool> IsServiceActiveAsync(string serviceName, CancellationToken cancellationToken);

    /// <summary><c>sudo -n systemctl ...</c>; throws when it fails.</summary>
    Task SystemctlAsync(CancellationToken cancellationToken, params string[] arguments);

    Task<string> ListBlockDevicesAsync(CancellationToken cancellationToken);
    Task MountAsync(string device, string mountPoint, string? fileSystemType, LinuxIdentity owner, CancellationToken cancellationToken);
    bool IsMountPoint(string path);
    DiskUsage GetDiskUsage(string path);
}

internal sealed class LinuxSystem : ILinuxSystem
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SystemctlTimeout = TimeSpan.FromSeconds(60);

    // File systems without Unix owners: the owner is given as mount options instead.
    private static readonly HashSet<string> ForeignFileSystems = new(StringComparer.OrdinalIgnoreCase) { "vfat", "exfat", "ntfs", "ntfs3", "msdos" };

    private readonly ICommandRunner _commandRunner;

    public LinuxSystem(ICommandRunner commandRunner)
    {
        _commandRunner = commandRunner;
    }

    public bool IsLinux => OperatingSystem.IsLinux();

    public async Task<string> GetMachineAsync(CancellationToken cancellationToken) =>
        (await RunCheckedAsync("uname", new[] { "-m" }, ShortTimeout, cancellationToken)).Trim();

    public async Task<LinuxIdentity> GetIdentityAsync(CancellationToken cancellationToken)
    {
        var user = (await RunCheckedAsync("id", new[] { "-un" }, ShortTimeout, cancellationToken)).Trim();
        var group = (await RunCheckedAsync("id", new[] { "-gn" }, ShortTimeout, cancellationToken)).Trim();
        var uid = int.Parse((await RunCheckedAsync("id", new[] { "-u" }, ShortTimeout, cancellationToken)).Trim(), CultureInfo.InvariantCulture);
        var gid = int.Parse((await RunCheckedAsync("id", new[] { "-g" }, ShortTimeout, cancellationToken)).Trim(), CultureInfo.InvariantCulture);
        return new LinuxIdentity(user, group, uid, gid);
    }

    public async Task EnsureWritableDirectoryAsync(string path, LinuxIdentity owner, CancellationToken cancellationToken)
    {
        if (IsWritableDirectory(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            if (IsWritableDirectory(path))
            {
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Parent is root-owned - fall through to sudo.
        }

        await SudoAsync(cancellationToken, ShortTimeout, "install", "-d", "-m", "755", "-o", owner.User, "-g", owner.Group, path);
        if (IsWritableDirectory(path) == false)
        {
            throw new RecordingsException($"Directory {path} is still not writable for {owner.User}");
        }
    }

    public Task InstallRootFileAsync(string sourcePath, string targetPath, string mode, CancellationToken cancellationToken) =>
        SudoAsync(cancellationToken, ShortTimeout, "install", "-m", mode, "-o", "root", "-g", "root", sourcePath, targetPath);

    public async Task<bool> IsServiceActiveAsync(string serviceName, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync("systemctl", new[] { "is-active", "--quiet", serviceName }, ShortTimeout, cancellationToken);
        return result.Succeeded;
    }

    public Task SystemctlAsync(CancellationToken cancellationToken, params string[] arguments) =>
        SudoAsync(cancellationToken, SystemctlTimeout, new[] { "systemctl" }.Concat(arguments).ToArray());

    public Task<string> ListBlockDevicesAsync(CancellationToken cancellationToken) =>
        RunCheckedAsync("lsblk", new[] { "-J", "-b", "-o", "NAME,PATH,TRAN,TYPE,FSTYPE,MOUNTPOINT,LABEL,UUID,SIZE" }, ShortTimeout, cancellationToken);

    public async Task MountAsync(string device, string mountPoint, string? fileSystemType, LinuxIdentity owner, CancellationToken cancellationToken)
    {
        await SudoAsync(cancellationToken, ShortTimeout, "mkdir", "-p", mountPoint);
        if (fileSystemType != null && ForeignFileSystems.Contains(fileSystemType))
        {
            var options = string.Create(CultureInfo.InvariantCulture, $"uid={owner.Uid},gid={owner.Gid},umask=022,noatime");
            await SudoAsync(cancellationToken, TimeSpan.FromMinutes(2), "mount", "-o", options, device, mountPoint);
        }
        else
        {
            await SudoAsync(cancellationToken, TimeSpan.FromMinutes(2), "mount", "-o", "noatime", device, mountPoint);
        }
    }

    public bool IsMountPoint(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var line in File.ReadLines("/proc/mounts"))
        {
            var fields = line.Split(' ');
            if (fields.Length > 1 && DecodeMountPath(fields[1]) == normalized)
            {
                return true;
            }
        }

        return false;
    }

    public DiskUsage GetDiskUsage(string path)
    {
        var drive = new DriveInfo(path);
        return new DiskUsage(drive.TotalSize - drive.TotalFreeSpace, drive.AvailableFreeSpace);
    }

    // /proc/mounts escapes space, tab, newline and backslash as \ooo.
    internal static string DecodeMountPath(string field)
    {
        if (field.Contains('\\') == false)
        {
            return field;
        }

        var builder = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && IsOctal(field, i + 1))
            {
                builder.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(field[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctal(string text, int start) =>
        start + 3 <= text.Length && text.Substring(start, 3).All(c => c is >= '0' and <= '7');

    private static bool IsWritableDirectory(string path)
    {
        if (Directory.Exists(path) == false)
        {
            return false;
        }

        var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private Task SudoAsync(CancellationToken cancellationToken, TimeSpan timeout, params string[] arguments) =>
        RunCheckedAsync("sudo", new[] { "-n" }.Concat(arguments).ToArray(), timeout, cancellationToken);

    private async Task<string> RunCheckedAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(fileName, arguments, timeout, cancellationToken);
        if (result.Succeeded == false)
        {
            throw new RecordingsException($"'{fileName} {string.Join(' ', arguments)}' failed ({result.Describe()})");
        }

        return result.StandardOutput;
    }
}

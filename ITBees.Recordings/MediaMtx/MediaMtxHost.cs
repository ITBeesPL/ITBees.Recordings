using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.MediaMtx;

/// <summary>Installs MediaMTX at runtime and keeps its configuration and systemd service in the wanted state.</summary>
internal interface IMediaMtxHost
{
    string BinaryPath { get; }
    string ConfigPath { get; }

    /// <summary>Installs the pinned release when missing or outdated. True when the binary changed.</summary>
    Task<bool> EnsureBinaryAsync(CancellationToken cancellationToken);

    /// <summary>Writes the configuration when it differs (MediaMTX reloads it by itself). True when written.</summary>
    Task<bool> WriteConfigAsync(string content, CancellationToken cancellationToken);

    /// <summary>Installs/updates and enables the systemd unit. True when it changed.</summary>
    Task<bool> EnsureUnitAsync(string content, CancellationToken cancellationToken);

    Task<bool> IsActiveAsync(CancellationToken cancellationToken);
    Task RestartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class MediaMtxHost : IMediaMtxHost
{
    private const string BinaryName = "mediamtx";

    private readonly RecordingsOptions _options;
    private readonly ILinuxSystem _linuxSystem;
    private readonly HttpClient _downloadClient;
    private readonly ILogger<MediaMtxHost> _logger;

    public MediaMtxHost(IOptions<RecordingsOptions> options, ILinuxSystem linuxSystem, RecordingsHttpClients httpClients, ILogger<MediaMtxHost> logger)
    {
        _options = options.Value;
        _linuxSystem = linuxSystem;
        _downloadClient = httpClients.Download;
        _logger = logger;
    }

    public string BinaryPath => Path.Combine(_options.InstallDirectory, BinaryName);
    public string ConfigPath => Path.Combine(_options.InstallDirectory, "mediamtx.yml");
    private string VersionPath => Path.Combine(_options.InstallDirectory, "mediamtx.version");
    private string UnitPath => $"/etc/systemd/system/{_options.ServiceName}.service";

    public async Task<bool> EnsureBinaryAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(BinaryPath) && ReadText(VersionPath)?.Trim() == MediaMtxRelease.Version)
        {
            return false;
        }

        var machine = await _linuxSystem.GetMachineAsync(cancellationToken);
        var architecture = MediaMtxRelease.MapArchitecture(machine)
                           ?? throw new RecordingsException($"MediaMTX has no build for machine '{machine}'");
        var identity = await _linuxSystem.GetIdentityAsync(cancellationToken);
        await _linuxSystem.EnsureWritableDirectoryAsync(_options.InstallDirectory, identity, cancellationToken);

        var archivePath = _options.MediaMtxArchivePath;
        var downloaded = false;
        if (archivePath == null)
        {
            archivePath = Path.Combine(_options.InstallDirectory, MediaMtxRelease.ArchiveName(architecture) + ".part");
            _logger.LogInformation("Downloading MediaMTX {Version} ({Architecture})", MediaMtxRelease.Version, architecture);
            await DownloadAsync(MediaMtxRelease.DownloadUri(architecture), archivePath, cancellationToken);
            downloaded = true;
        }

        try
        {
            VerifyChecksum(archivePath, MediaMtxRelease.Sha256(architecture));
            var newBinary = BinaryPath + ".new";
            ExtractBinary(archivePath, newBinary);
            SetMode(newBinary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                               | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                               | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // A rename keeps a running process on the old inode - the caller restarts the service.
            File.Move(newBinary, BinaryPath, overwrite: true);
            await File.WriteAllTextAsync(VersionPath, MediaMtxRelease.Version, cancellationToken);
        }
        finally
        {
            if (downloaded)
            {
                File.Delete(archivePath);
            }
        }

        _logger.LogInformation("MediaMTX {Version} installed in {Directory}", MediaMtxRelease.Version, _options.InstallDirectory);
        return true;
    }

    public async Task<bool> WriteConfigAsync(string content, CancellationToken cancellationToken)
    {
        if (ReadText(ConfigPath) == content)
        {
            return false;
        }

        // The file holds camera passwords: owner only. Written next to the target and renamed, so
        // MediaMTX never reloads a half-written file.
        var temporary = ConfigPath + ".tmp";
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        SetMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, ConfigPath, overwrite: true);
        return true;
    }

    public async Task<bool> EnsureUnitAsync(string content, CancellationToken cancellationToken)
    {
        if (ReadText(UnitPath) == content)
        {
            return false;
        }

        var temporary = Path.Combine(_options.InstallDirectory, $"{_options.ServiceName}.service.tmp");
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        try
        {
            await _linuxSystem.InstallRootFileAsync(temporary, UnitPath, "644", cancellationToken);
        }
        finally
        {
            File.Delete(temporary);
        }

        await _linuxSystem.SystemctlAsync(cancellationToken, "daemon-reload");
        await _linuxSystem.SystemctlAsync(cancellationToken, "enable", _options.ServiceName);
        _logger.LogInformation("Systemd unit {Unit} installed", UnitPath);
        return true;
    }

    public Task<bool> IsActiveAsync(CancellationToken cancellationToken) =>
        _linuxSystem.IsServiceActiveAsync(_options.ServiceName, cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken) =>
        _linuxSystem.SystemctlAsync(cancellationToken, "restart", _options.ServiceName);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _linuxSystem.SystemctlAsync(cancellationToken, "stop", _options.ServiceName);

    private async Task DownloadAsync(Uri uri, string targetPath, CancellationToken cancellationToken)
    {
        using var response = await _downloadClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode == false)
        {
            throw new RecordingsException($"Downloading {uri} answered HTTP {(int)response.StatusCode}");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    internal static void VerifyChecksum(string archivePath, string expectedSha256)
    {
        using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase) == false)
        {
            throw new RecordingsException($"MediaMTX archive {archivePath} has SHA-256 {actual.ToLowerInvariant()}, expected {expectedSha256}");
        }
    }

    internal static void ExtractBinary(string archivePath, string targetPath)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                && Path.GetFileName(entry.Name) == BinaryName)
            {
                entry.ExtractToFile(targetPath, overwrite: true);
                return;
            }
        }

        throw new RecordingsException($"MediaMTX archive {archivePath} has no '{BinaryName}' file");
    }

    // The host only manages MediaMTX on Linux; the guard keeps the code buildable everywhere.
    private static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, mode);
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

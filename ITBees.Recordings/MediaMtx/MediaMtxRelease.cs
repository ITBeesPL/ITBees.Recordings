namespace ITBees.Recordings.MediaMtx;

/// <summary>The pinned MediaMTX release. A new version = new constants + checksums from the release's checksums.sha256.</summary>
internal static class MediaMtxRelease
{
    public const string Version = "v1.21.3";

    private static readonly Dictionary<string, string> Sha256ByArchitecture = new()
    {
        ["amd64"] = "0a2ea7eba96023631efa1bea7804823a9145b043b6646a8a05798d4ac24ed635",
        ["arm64"] = "27f038237cb5f77a724f6afc58eca2798f2bc4edf9989895631a80eeb41a4fe2",
        ["armv6"] = "cbacd828dd7c8bb67954eee2b0b5590b1f1f6994faa6fcd86a3cf0ba50213095",
        ["armv7"] = "134b53bfdb26583d02e1c27fc256d587cf3482e80c9e3b49083595e19a096284",
    };

    /// <summary>
    /// Maps <c>uname -m</c> to the release architecture. MediaMTX is a static binary, so the kernel decides:
    /// a 64-bit kernel with a 32-bit userland (Raspberry Pi OS) still runs the arm64 build.
    /// </summary>
    public static string? MapArchitecture(string machine) => machine.Trim().ToLowerInvariant() switch
    {
        "aarch64" or "arm64" => "arm64",
        "armv7l" or "armv8l" => "armv7",
        "armv6l" => "armv6",
        "x86_64" or "amd64" => "amd64",
        _ => null,
    };

    public static string ArchiveName(string architecture) => $"mediamtx_{Version}_linux_{architecture}.tar.gz";

    public static Uri DownloadUri(string architecture) =>
        new($"https://github.com/bluenviron/mediamtx/releases/download/{Version}/{ArchiveName(architecture)}");

    public static string Sha256(string architecture) => Sha256ByArchitecture[architecture];
}

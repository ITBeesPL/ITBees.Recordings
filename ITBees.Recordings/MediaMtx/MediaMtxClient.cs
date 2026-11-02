using System.Globalization;
using System.Net;
using System.Text.Json;
using ITBees.Recordings.Infrastructure;
using ITBees.Recordings.Models;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.MediaMtx;

/// <summary>State of one MediaMTX path (= one camera) from the control API.</summary>
internal sealed record MediaMtxPathInfo(string Name, bool Online, DateTimeOffset? OnlineTime, long InboundBytes, long FramesInError);

internal interface IMediaMtxClient
{
    /// <summary>Configured paths; throws when the API cannot be reached.</summary>
    Task<IReadOnlyList<MediaMtxPathInfo>> GetPathsAsync(CancellationToken cancellationToken);

    /// <summary>Recorded stretches of a path overlapping the range (empty when nothing is recorded).</summary>
    Task<IReadOnlyList<RecordedSpan>> ListSpansAsync(string pathName, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Writes the recording from <paramref name="start"/> as a standard MP4.</summary>
    Task DownloadAsync(string pathName, DateTimeOffset start, TimeSpan duration, Stream destination, CancellationToken cancellationToken);
}

internal sealed class MediaMtxClient : IMediaMtxClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _apiBase;
    private readonly Uri _playbackBase;

    public MediaMtxClient(RecordingsHttpClients httpClients, IOptions<RecordingsOptions> options)
    {
        _httpClient = httpClients.Local;
        _apiBase = LocalBaseUri(options.Value.ApiAddress);
        _playbackBase = LocalBaseUri(options.Value.PlaybackAddress);
    }

    public async Task<IReadOnlyList<MediaMtxPathInfo>> GetPathsAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(new Uri(_apiBase, "v3/paths/list?itemsPerPage=1000"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParsePaths(document.RootElement);
    }

    public async Task<IReadOnlyList<RecordedSpan>> ListSpansAsync(string pathName, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var uri = new Uri(_playbackBase, $"list?path={Uri.EscapeDataString(pathName)}&start={Uri.EscapeDataString(FormatTime(from))}&end={Uri.EscapeDataString(FormatTime(to))}");
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        // 404 = no recording of the path in the range.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<RecordedSpan>();
        }

        await EnsureSuccessAsync(response, "list", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseSpans(document.RootElement);
    }

    public async Task DownloadAsync(string pathName, DateTimeOffset start, TimeSpan duration, Stream destination, CancellationToken cancellationToken)
    {
        var seconds = duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var uri = new Uri(_playbackBase, $"get?path={Uri.EscapeDataString(pathName)}&start={Uri.EscapeDataString(FormatTime(start))}&duration={seconds}&format=mp4");
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "get", cancellationToken);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(destination, cancellationToken);
    }

    internal static IReadOnlyList<MediaMtxPathInfo> ParsePaths(JsonElement root)
    {
        var result = new List<MediaMtxPathInfo>();
        if (root.TryGetProperty("items", out var items) == false || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            // "online"/"inboundBytes" since MediaMTX 1.15; "ready"/"bytesReceived" before.
            var online = GetBool(item, "online") ?? GetBool(item, "ready") ?? false;
            var onlineTime = GetTime(item, "onlineTime") ?? GetTime(item, "readyTime");
            result.Add(new MediaMtxPathInfo(
                GetString(item, "confName") ?? GetString(item, "name") ?? string.Empty,
                online,
                onlineTime,
                GetLong(item, "inboundBytes") ?? GetLong(item, "bytesReceived") ?? 0,
                GetLong(item, "inboundFramesInError") ?? 0));
        }

        return result;
    }

    internal static IReadOnlyList<RecordedSpan> ParseSpans(JsonElement root)
    {
        var result = new List<RecordedSpan>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in root.EnumerateArray())
        {
            var start = GetTime(item, "start");
            if (start == null || item.TryGetProperty("duration", out var duration) == false || duration.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            result.Add(new RecordedSpan(start.Value, TimeSpan.FromSeconds(duration.GetDouble())));
        }

        return result.OrderBy(x => x.Start).ToList();
    }

    /// <summary>RFC 3339 in UTC with milliseconds, as MediaMTX expects.</summary>
    internal static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>"127.0.0.1:9996" or ":9996" (all interfaces) → http://127.0.0.1:9996/.</summary>
    internal static Uri LocalBaseUri(string listenAddress)
    {
        var separator = listenAddress.LastIndexOf(':');
        var host = separator > 0 ? listenAddress[..separator] : string.Empty;
        var port = separator >= 0 ? listenAddress[(separator + 1)..] : listenAddress;
        if (host is "" or "0.0.0.0" or "[::]")
        {
            host = "127.0.0.1";
        }

        return new Uri($"http://{host}:{port}/");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new RecordingsException($"MediaMTX playback {operation} answered HTTP {(int)response.StatusCode}: {body.Trim()}");
    }

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static long? GetLong(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static DateTimeOffset? GetTime(JsonElement item, string name) =>
        GetString(item, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}

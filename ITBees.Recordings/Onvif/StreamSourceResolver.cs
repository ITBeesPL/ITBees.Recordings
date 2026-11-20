using ITBees.Recordings.Models;
using Microsoft.Extensions.Logging;

namespace ITBees.Recordings.Onvif;

/// <summary>What MediaMTX records for a camera: the source URL with credentials and a readable description.</summary>
internal sealed record ResolvedStream(string SourceUrl, string? Description);

internal interface IStreamSourceResolver
{
    /// <summary>
    /// Finds the stream to record (explicit URL or ONVIF profile chosen by quality) and applies the requested
    /// encoder values to the camera over ONVIF.
    /// </summary>
    Task<ResolvedStream> ResolveAsync(RecordingCameraDefinition camera, CancellationToken cancellationToken);
}

internal sealed class StreamSourceResolver : IStreamSourceResolver
{
    private readonly IOnvifClientFactory _onvifClientFactory;
    private readonly ILogger<StreamSourceResolver> _logger;

    public StreamSourceResolver(IOnvifClientFactory onvifClientFactory, ILogger<StreamSourceResolver> logger)
    {
        _onvifClientFactory = onvifClientFactory;
        _logger = logger;
    }

    public async Task<ResolvedStream> ResolveAsync(RecordingCameraDefinition camera, CancellationToken cancellationToken)
    {
        if (camera.RtspUrl != null && camera.Quality.HasEncoderSettings == false)
        {
            return new ResolvedStream(RtspUrls.WithCredentials(camera.RtspUrl, camera.Username, camera.Password), null);
        }

        using var client = _onvifClientFactory.Create(camera.Host!, camera.Username, camera.Password);
        var profiles = await client.GetProfilesAsync(cancellationToken);
        var profile = SelectProfile(profiles, camera.Quality)
                      ?? throw new RecordingsException($"Camera {camera.Id} has no ONVIF profile '{camera.Quality.ProfileToken}' (has: {string.Join(", ", profiles.Select(x => x.Token))})");

        profile = await EnsureBrowserPlayableAsync(client, camera, profile, profiles, cancellationToken);

        if (camera.Quality.HasEncoderSettings)
        {
            profile = await ApplyEncoderAsync(client, camera, profile, cancellationToken);
        }

        profile = await RemoveAudioAsync(client, camera, profile, cancellationToken);

        var url = camera.RtspUrl ?? await client.GetStreamUriAsync(profile.Token, cancellationToken);
        var description = profile.VideoEncoder != null ? $"{profile.Token}: {profile.VideoEncoder}" : profile.Token;
        if (profile.VideoEncoder != null && profile.IsBrowserPlayable == false)
        {
            description += " - not playable in browsers";
        }

        return new ResolvedStream(RtspUrls.WithCredentials(url, camera.Username, camera.Password), description);
    }

    /// <summary>
    /// Recordings are played in the panels' browser player, which decodes only H.264. A profile streaming JPEG
    /// (MJPEG) or MPEG4 is switched to H.264 on the camera (resolution, frame rate and bitrate kept); when the camera
    /// refuses, another H.264 profile of the nearest resolution is recorded instead. Only when the camera has no
    /// H.264 at all is the stream recorded as it is (logged, and marked in the description).
    /// </summary>
    private async Task<OnvifProfile> EnsureBrowserPlayableAsync(IOnvifClient client, RecordingCameraDefinition camera,
        OnvifProfile profile, IReadOnlyList<OnvifProfile> profiles, CancellationToken cancellationToken)
    {
        if (profile.VideoEncoder == null || profile.IsBrowserPlayable)
        {
            return profile;
        }

        var encoder = profile.VideoEncoder;
        try
        {
            var h264Profiles = await client.GetH264ProfilesAsync(encoder.Token, profile.Token, cancellationToken);
            if (h264Profiles.Count > 0)
            {
                var h264Profile = PickH264Profile(h264Profiles);
                var changed = OnvifClient.WithH264Encoding(encoder, h264Profile)!;
                await client.SetVideoEncoderAsync(changed, cancellationToken);
                var readBack = (await client.GetProfilesAsync(cancellationToken)).FirstOrDefault(x => x.Token == profile.Token);
                if (readBack?.IsBrowserPlayable == true)
                {
                    _logger.LogInformation("Camera {CameraId}: encoder of {Profile} switched from {Before} to {After} (browsers play only H.264)",
                        camera.Id, profile.Token, encoder, readBack.VideoEncoder);
                    return readBack;
                }

                _logger.LogWarning("Camera {CameraId}: {Profile} still streams {Encoding} after the switch to H.264",
                    camera.Id, profile.Token, readBack?.VideoEncoder?.Encoding);
            }
        }
        catch (Exception e) when (e is OnvifException or RecordingsException or HttpRequestException or TaskCanceledException && cancellationToken.IsCancellationRequested == false)
        {
            _logger.LogWarning("Camera {CameraId}: could not switch {Profile} from {Encoding} to H.264: {Error}",
                camera.Id, profile.Token, encoder.Encoding, e.Message);
        }

        var alternative = SelectPlayableAlternative(profiles, profile);
        if (alternative != null)
        {
            _logger.LogWarning("Camera {CameraId}: {Profile} streams {Encoding}, which browsers do not play - recording {Alternative} ({Encoder}) instead",
                camera.Id, profile.Token, encoder.Encoding, alternative.Token, alternative.VideoEncoder);
            return alternative;
        }

        _logger.LogWarning("Camera {CameraId}: no H.264 profile - recording {Profile} as {Encoding}; the clips will not play in browsers",
            camera.Id, profile.Token, encoder.Encoding);
        return profile;
    }

    /// <summary>Main plays everywhere and compresses better than Baseline; High is not always decoded by older players.</summary>
    internal static string PickH264Profile(IReadOnlyList<string> supported) =>
        supported.FirstOrDefault(x => string.Equals(x, "Main", StringComparison.OrdinalIgnoreCase))
        ?? supported.FirstOrDefault(x => string.Equals(x, "Baseline", StringComparison.OrdinalIgnoreCase))
        ?? supported[0];

    /// <summary>
    /// The smallest H.264 profile at least as sharp as the one chosen (a plate must stay readable); when every
    /// H.264 profile is smaller, the largest of them.
    /// </summary>
    internal static OnvifProfile? SelectPlayableAlternative(IReadOnlyList<OnvifProfile> profiles, OnvifProfile chosen)
    {
        var playable = profiles.Where(x => x.Token != chosen.Token && x.IsBrowserPlayable).ToList();
        return playable.Where(x => x.Pixels >= chosen.Pixels).OrderBy(x => x.Pixels).FirstOrDefault()
               ?? playable.OrderByDescending(x => x.Pixels).FirstOrDefault();
    }

    /// <summary>
    /// Parking cameras record video only: the audio track (G.711, stored as raw PCM that browsers cannot play)
    /// is useless at a barrier and records conversations. A camera that refuses keeps its audio - logged only.
    /// </summary>
    private async Task<OnvifProfile> RemoveAudioAsync(IOnvifClient client, RecordingCameraDefinition camera, OnvifProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.AudioEncoderToken == null)
        {
            return profile;
        }

        try
        {
            await client.RemoveAudioEncoderAsync(profile.Token, cancellationToken);
            _logger.LogInformation("Camera {CameraId}: audio removed from {Profile}, recording video only", camera.Id, profile.Token);
            return profile with { AudioEncoderToken = null };
        }
        catch (Exception e) when (e is OnvifException or HttpRequestException or TaskCanceledException && cancellationToken.IsCancellationRequested == false)
        {
            _logger.LogWarning("Camera {CameraId}: could not remove audio from {Profile}: {Error}", camera.Id, profile.Token, e.Message);
            return profile;
        }
    }

    /// <summary>Explicit token wins; otherwise profiles are ranked by resolution (cameras list them in any order).</summary>
    internal static OnvifProfile? SelectProfile(IReadOnlyList<OnvifProfile> profiles, RecordingQuality quality)
    {
        if (quality.ProfileToken != null)
        {
            return profiles.FirstOrDefault(x => string.Equals(x.Token, quality.ProfileToken, StringComparison.OrdinalIgnoreCase));
        }

        var ranked = profiles.Where(x => x.VideoEncoder != null).OrderByDescending(x => x.Pixels).ToList();
        if (ranked.Count == 0)
        {
            return profiles.FirstOrDefault();
        }

        return quality.Stream switch
        {
            RecordingStream.Sub => ranked.ElementAtOrDefault(1) ?? ranked[0],
            RecordingStream.Lowest => ranked[^1],
            _ => ranked[0],
        };
    }

    private async Task<OnvifProfile> ApplyEncoderAsync(IOnvifClient client, RecordingCameraDefinition camera, OnvifProfile profile, CancellationToken cancellationToken)
    {
        var encoder = profile.VideoEncoder ?? throw new RecordingsException($"Camera {camera.Id} profile {profile.Token} has no video encoder to configure");
        var quality = camera.Quality;
        if (quality.Width != null && (quality.Width != encoder.Width || quality.Height != encoder.Height))
        {
            var resolutions = await client.GetResolutionsAsync(encoder.Token, profile.Token, cancellationToken);
            if (resolutions.Count > 0 && resolutions.Contains(new OnvifResolution(quality.Width.Value, quality.Height!.Value)) == false)
            {
                throw new RecordingsException($"Camera {camera.Id} profile {profile.Token} cannot do {quality.Width}x{quality.Height} (can: {string.Join(", ", resolutions)})");
            }
        }

        var changed = OnvifClient.WithEncoderValues(encoder, quality);
        if (changed == null)
        {
            return profile;
        }

        await client.SetVideoEncoderAsync(changed, cancellationToken);
        _logger.LogInformation("Camera {CameraId}: encoder of {Profile} changed from {Before} to {Quality}", camera.Id, profile.Token, encoder, quality);

        // Read back what the camera really keeps (it may round values).
        var profiles = await client.GetProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(x => x.Token == profile.Token) ?? profile;
    }
}

internal static class RtspUrls
{
    /// <summary>Adds URL-encoded credentials unless the URL already carries some.</summary>
    public static string WithCredentials(string url, string? username, string? password)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (username == null || schemeEnd < 0)
        {
            return url;
        }

        var rest = url[(schemeEnd + 3)..];
        var authorityEnd = rest.IndexOfAny(new[] { '/', '?', '#' });
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        if (authority.Contains('@'))
        {
            return url;
        }

        var credentials = Uri.EscapeDataString(username) + (password != null ? ":" + Uri.EscapeDataString(password) : string.Empty);
        return $"{url[..(schemeEnd + 3)]}{credentials}@{rest}";
    }

    /// <summary>The URL without credentials, safe for logs.</summary>
    public static string Redact(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.IndexOf('@');
        var slash = schemeEnd < 0 ? -1 : url.IndexOf('/', schemeEnd + 3);
        return schemeEnd >= 0 && at > schemeEnd && (slash < 0 || at < slash) ? url[..(schemeEnd + 3)] + url[(at + 1)..] : url;
    }
}

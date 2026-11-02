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

        if (camera.Quality.HasEncoderSettings)
        {
            profile = await ApplyEncoderAsync(client, camera, profile, cancellationToken);
        }

        var url = camera.RtspUrl ?? await client.GetStreamUriAsync(profile.Token, cancellationToken);
        var description = profile.VideoEncoder != null ? $"{profile.Token}: {profile.VideoEncoder}" : profile.Token;
        return new ResolvedStream(RtspUrls.WithCredentials(url, camera.Username, camera.Password), description);
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

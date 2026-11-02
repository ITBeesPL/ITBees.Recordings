using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ITBees.Recordings.Models;

namespace ITBees.Recordings.Onvif;

internal sealed record OnvifDeviceInformation(string? Manufacturer, string? Model, string? FirmwareVersion, string? SerialNumber);

/// <summary>Video encoder of a profile. <see cref="Element"/> is the camera's own XML, sent back modified on Set.</summary>
internal sealed record OnvifVideoEncoder(string Token, string? Encoding, int? Width, int? Height, int? FrameRate, int? BitrateKbps, int? GovLength, XElement Element)
{
    public override string ToString() =>
        $"{Encoding} {Width}x{Height} {FrameRate} fps {BitrateKbps} kb/s GOP {GovLength}";
}

internal sealed record OnvifProfile(string Token, string? Name, OnvifVideoEncoder? VideoEncoder)
{
    public int Pixels => (VideoEncoder?.Width ?? 0) * (VideoEncoder?.Height ?? 0);
}

internal sealed record OnvifResolution(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}

internal interface IOnvifClient : IDisposable
{
    Task<OnvifDeviceInformation> GetDeviceInformationAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<OnvifProfile>> GetProfilesAsync(CancellationToken cancellationToken);
    Task<string> GetStreamUriAsync(string profileToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<OnvifResolution>> GetResolutionsAsync(string encoderToken, string profileToken, CancellationToken cancellationToken);
    Task SetVideoEncoderAsync(XElement configuration, CancellationToken cancellationToken);
}

internal interface IOnvifClientFactory
{
    IOnvifClient Create(string host, string? username, string? password);
}

internal sealed class OnvifClientFactory : IOnvifClientFactory
{
    private readonly TimeProvider _timeProvider;

    public OnvifClientFactory(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public IOnvifClient Create(string host, string? username, string? password) => new OnvifClient(host, username, password, _timeProvider);
}

/// <summary>
/// Minimal ONVIF (Device + Media ver10) client over SOAP 1.2. Authenticates with WS-UsernameToken
/// (password digest signed with the camera's clock) and, for cameras that want it, HTTP Digest.
/// </summary>
internal sealed class OnvifClient : IOnvifClient
{
    internal static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    internal static readonly XNamespace Device = "http://www.onvif.org/ver10/device/wsdl";
    internal static readonly XNamespace Media = "http://www.onvif.org/ver10/media/wsdl";
    internal static readonly XNamespace Schema = "http://www.onvif.org/ver10/schema";
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string PasswordDigestType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    private const string Base64EncodingType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    private readonly HttpClient _httpClient;
    private readonly Uri _deviceUri;
    private readonly string? _username;
    private readonly string? _password;
    private readonly TimeProvider _timeProvider;
    private TimeSpan? _clockOffset;
    private Uri? _mediaUri;

    public OnvifClient(string host, string? username, string? password, TimeProvider timeProvider, HttpMessageHandler? handler = null)
    {
        _deviceUri = new Uri($"http://{host}/onvif/device_service");
        _username = username;
        _password = password;
        _timeProvider = timeProvider;
        handler ??= new SocketsHttpHandler
        {
            Credentials = username != null ? new NetworkCredential(username, password) : null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<OnvifDeviceInformation> GetDeviceInformationAsync(CancellationToken cancellationToken)
    {
        var body = await CallAsync(_deviceUri, "GetDeviceInformation", new XElement(Device + "GetDeviceInformation"), cancellationToken);
        return new OnvifDeviceInformation(Text(body, "Manufacturer"), Text(body, "Model"), Text(body, "FirmwareVersion"), Text(body, "SerialNumber"));
    }

    public async Task<IReadOnlyList<OnvifProfile>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        var body = await CallAsync(await GetMediaUriAsync(cancellationToken), "GetProfiles", new XElement(Media + "GetProfiles"), cancellationToken);
        return ParseProfiles(body);
    }

    public async Task<string> GetStreamUriAsync(string profileToken, CancellationToken cancellationToken)
    {
        var request = new XElement(Media + "GetStreamUri",
            new XElement(Media + "StreamSetup",
                new XElement(Schema + "Stream", "RTP-Unicast"),
                new XElement(Schema + "Transport", new XElement(Schema + "Protocol", "RTSP"))),
            new XElement(Media + "ProfileToken", profileToken));
        var body = await CallAsync(await GetMediaUriAsync(cancellationToken), "GetStreamUri", request, cancellationToken);
        var uri = Text(body, "Uri") ?? throw new RecordingsException($"Camera {_deviceUri.Authority} returned no stream URI for {profileToken}");
        return ReplaceHost(uri, _deviceUri.Host);
    }

    public async Task<IReadOnlyList<OnvifResolution>> GetResolutionsAsync(string encoderToken, string profileToken, CancellationToken cancellationToken)
    {
        var request = new XElement(Media + "GetVideoEncoderConfigurationOptions",
            new XElement(Media + "ConfigurationToken", encoderToken),
            new XElement(Media + "ProfileToken", profileToken));
        var body = await CallAsync(await GetMediaUriAsync(cancellationToken), "GetVideoEncoderConfigurationOptions", request, cancellationToken);
        return body.Descendants().Where(x => x.Name.LocalName == "ResolutionsAvailable")
            .Select(x => new OnvifResolution(Int(x, "Width") ?? 0, Int(x, "Height") ?? 0))
            .Where(x => x.Width > 0 && x.Height > 0)
            .Distinct()
            .ToList();
    }

    public async Task SetVideoEncoderAsync(XElement configuration, CancellationToken cancellationToken)
    {
        var request = new XElement(Media + "SetVideoEncoderConfiguration",
            new XElement(Media + "Configuration", configuration.Attributes(), configuration.Nodes()),
            new XElement(Media + "ForcePersistence", "true"));
        await CallAsync(await GetMediaUriAsync(cancellationToken), "SetVideoEncoderConfiguration", request, cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<Uri> GetMediaUriAsync(CancellationToken cancellationToken)
    {
        if (_mediaUri != null)
        {
            return _mediaUri;
        }

        var request = new XElement(Device + "GetCapabilities", new XElement(Device + "Category", "Media"));
        var body = await CallAsync(_deviceUri, "GetCapabilities", request, cancellationToken);
        var xaddr = body.Descendants().FirstOrDefault(x => x.Name.LocalName == "Media")?
            .Elements().FirstOrDefault(x => x.Name.LocalName == "XAddr")?.Value.Trim();
        // The camera may report an address we do not reach it by (NAT, temporary address) - keep only its path.
        _mediaUri = xaddr != null && Uri.TryCreate(xaddr, UriKind.Absolute, out var reported)
            ? new UriBuilder(reported) { Host = _deviceUri.Host, Port = _deviceUri.Port }.Uri
            : new Uri(_deviceUri, "/onvif/media_service");
        return _mediaUri;
    }

    private async Task<XElement> CallAsync(Uri uri, string operation, XElement request, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(uri, operation, request, cancellationToken);
        }
        catch (OnvifException e) when (e.IsNotAuthorized && _clockOffset != null)
        {
            // The camera clock may have jumped (NTP, reboot) - sign again with a fresh offset once.
            _clockOffset = null;
            return await SendAsync(uri, operation, request, cancellationToken);
        }
    }

    private async Task<XElement> SendAsync(Uri uri, string operation, XElement request, CancellationToken cancellationToken)
    {
        XElement? header = null;
        if (_username != null)
        {
            _clockOffset ??= await GetClockOffsetAsync(cancellationToken);
            header = BuildSecurityHeader(_username, _password ?? string.Empty, _timeProvider.GetUtcNow() + _clockOffset.Value, RandomNumberGenerator.GetBytes(16));
        }

        return await PostAsync(uri, operation, request, header, cancellationToken);
    }

    private async Task<TimeSpan> GetClockOffsetAsync(CancellationToken cancellationToken)
    {
        // GetSystemDateAndTime needs no login, by the standard.
        var body = await PostAsync(_deviceUri, "GetSystemDateAndTime", new XElement(Device + "GetSystemDateAndTime"), null, cancellationToken);
        var cameraUtc = ParseCameraUtc(body);
        return cameraUtc == null ? TimeSpan.Zero : cameraUtc.Value - _timeProvider.GetUtcNow();
    }

    private async Task<XElement> PostAsync(Uri uri, string operation, XElement request, XElement? header, CancellationToken cancellationToken)
    {
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap),
            header == null ? null : new XElement(Soap + "Header", header),
            new XElement(Soap + "Body", request));
        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), envelope).ToString(SaveOptions.DisableFormatting);
        using var content = new StringContent(xml, Encoding.UTF8, "application/soap+xml");
        using var response = await _httpClient.PostAsync(uri, content, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        XElement? body = null;
        try
        {
            body = XDocument.Parse(text).Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "Body");
        }
        catch (System.Xml.XmlException)
        {
            // Not SOAP (e.g. an HTML error page) - reported below.
        }

        var fault = body?.Elements().FirstOrDefault(x => x.Name.LocalName == "Fault");
        if (fault != null || response.IsSuccessStatusCode == false || body == null)
        {
            var code = fault?.Descendants().Where(x => x.Name.LocalName == "Value").Select(x => x.Value.Trim()).LastOrDefault();
            var reason = fault?.Descendants().FirstOrDefault(x => x.Name.LocalName == "Text")?.Value.Trim()
                         ?? (body == null ? Truncate(text, 200) : null);
            throw new OnvifException(operation, (int)response.StatusCode, code, reason);
        }

        return body;
    }

    internal static XElement BuildSecurityHeader(string username, string password, DateTimeOffset created, byte[] nonce)
    {
        var createdText = created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return new XElement(Wsse + "Security",
            new XAttribute(Soap + "mustUnderstand", "1"),
            new XElement(Wsse + "UsernameToken",
                new XElement(Wsse + "Username", username),
                new XElement(Wsse + "Password", new XAttribute("Type", PasswordDigestType), PasswordDigest(nonce, createdText, password)),
                new XElement(Wsse + "Nonce", new XAttribute("EncodingType", Base64EncodingType), Convert.ToBase64String(nonce)),
                new XElement(Wsu + "Created", createdText)));
    }

    /// <summary>WS-UsernameToken digest: Base64(SHA1(nonce + created + password)).</summary>
    internal static string PasswordDigest(byte[] nonce, string created, string password)
    {
        var text = Encoding.UTF8.GetBytes(created + password);
        var buffer = new byte[nonce.Length + text.Length];
        nonce.CopyTo(buffer, 0);
        text.CopyTo(buffer, nonce.Length);
        return Convert.ToBase64String(SHA1.HashData(buffer));
    }

    internal static DateTimeOffset? ParseCameraUtc(XElement body)
    {
        var utc = body.Descendants().FirstOrDefault(x => x.Name.LocalName == "UTCDateTime");
        if (utc == null)
        {
            return null;
        }

        // UTCDateTime holds Date (Year, Month, Day) and Time (Hour, Minute, Second).
        int? Part(string name) => int.TryParse(utc.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        var year = Part("Year");
        var month = Part("Month");
        var day = Part("Day");
        if (year == null || month == null || day == null)
        {
            return null;
        }

        return new DateTimeOffset(year.Value, month.Value, day.Value, Part("Hour") ?? 0, Part("Minute") ?? 0, Part("Second") ?? 0, TimeSpan.Zero);
    }

    internal static IReadOnlyList<OnvifProfile> ParseProfiles(XElement body)
    {
        var profiles = new List<OnvifProfile>();
        foreach (var profile in body.Descendants().Where(x => x.Name.LocalName == "Profiles"))
        {
            var token = profile.Attribute("token")?.Value;
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            var encoderElement = profile.Elements().FirstOrDefault(x => x.Name.LocalName == "VideoEncoderConfiguration");
            OnvifVideoEncoder? encoder = null;
            if (encoderElement != null)
            {
                var rateControl = Child(encoderElement, "RateControl");
                var codec = Child(encoderElement, "H264") ?? Child(encoderElement, "MPEG4");
                encoder = new OnvifVideoEncoder(
                    encoderElement.Attribute("token")?.Value ?? string.Empty,
                    Child(encoderElement, "Encoding")?.Value.Trim(),
                    Int(Child(encoderElement, "Resolution"), "Width"),
                    Int(Child(encoderElement, "Resolution"), "Height"),
                    Int(rateControl, "FrameRateLimit"),
                    Int(rateControl, "BitrateLimit"),
                    Int(codec, "GovLength"),
                    new XElement(encoderElement));
            }

            profiles.Add(new OnvifProfile(token, Child(profile, "Name")?.Value.Trim(), encoder));
        }

        return profiles;
    }

    /// <summary>Copy of the camera's encoder XML with the requested values replaced; null when nothing changes.</summary>
    internal static XElement? WithEncoderValues(OnvifVideoEncoder encoder, RecordingQuality quality)
    {
        var copy = new XElement(encoder.Element);
        var changed = false;

        void Set(XElement? parent, string name, int? value)
        {
            var element = Child(parent, name);
            if (value == null || element == null || element.Value.Trim() == value.Value.ToString(CultureInfo.InvariantCulture))
            {
                return;
            }

            element.Value = value.Value.ToString(CultureInfo.InvariantCulture);
            changed = true;
        }

        Set(Child(copy, "Resolution"), "Width", quality.Width);
        Set(Child(copy, "Resolution"), "Height", quality.Height);
        Set(Child(copy, "RateControl"), "FrameRateLimit", quality.FrameRate);
        Set(Child(copy, "RateControl"), "BitrateLimit", quality.BitrateKbps);
        Set(Child(copy, "H264") ?? Child(copy, "MPEG4"), "GovLength", quality.KeyframeInterval);
        return changed ? copy : null;
    }

    /// <summary>Puts <paramref name="host"/> into the URI, keeping its port (the camera may report another address than we use).</summary>
    internal static string ReplaceHost(string uri, string host)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) == false || parsed.Host == host)
        {
            return uri;
        }

        return new UriBuilder(parsed) { Host = host }.Uri.AbsoluteUri;
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

    private static string? Text(XElement body, string localName) =>
        body.Descendants().FirstOrDefault(x => x.Name.LocalName == localName)?.Value.Trim();

    private static int? Int(XElement? parent, string localName) =>
        int.TryParse(Child(parent, localName)?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Truncate(string text, int length) => text.Length <= length ? text.Trim() : text[..length].Trim() + "…";
}

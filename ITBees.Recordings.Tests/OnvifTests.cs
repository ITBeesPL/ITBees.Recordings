using System.Net;
using System.Xml.Linq;
using ITBees.Recordings.Models;
using ITBees.Recordings.Onvif;
using Microsoft.Extensions.Logging.Abstractions;

namespace ITBees.Recordings.Tests;

public class OnvifTests
{
    // Shape of the answer of the IPOX PX-TZIP2012IR7LPR-WG (fw 5.1) on the dev stand, trimmed.
    private const string ProfilesResponse = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope" xmlns:tt="http://www.onvif.org/ver10/schema" xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
          <SOAP-ENV:Body>
            <trt:GetProfilesResponse>
              <trt:Profiles token="profile3" fixed="true">
                <tt:Name>profile3</tt:Name>
                <tt:VideoEncoderConfiguration token="VideoEncoderToken3">
                  <tt:Name>VideoEncoder3</tt:Name><tt:UseCount>1</tt:UseCount><tt:Encoding>H264</tt:Encoding>
                  <tt:Resolution><tt:Width>480</tt:Width><tt:Height>240</tt:Height></tt:Resolution>
                  <tt:Quality>4</tt:Quality>
                  <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit><tt:EncodingInterval>1</tt:EncodingInterval><tt:BitrateLimit>512</tt:BitrateLimit></tt:RateControl>
                  <tt:H264><tt:GovLength>100</tt:GovLength><tt:H264Profile>Main</tt:H264Profile></tt:H264>
                </tt:VideoEncoderConfiguration>
              </trt:Profiles>
              <trt:Profiles token="profile1" fixed="true">
                <tt:Name>profile1</tt:Name>
                <tt:VideoEncoderConfiguration token="VideoEncoderToken1">
                  <tt:Name>VideoEncoder1</tt:Name><tt:UseCount>1</tt:UseCount><tt:Encoding>H264</tt:Encoding>
                  <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
                  <tt:Quality>4</tt:Quality>
                  <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit><tt:EncodingInterval>1</tt:EncodingInterval><tt:BitrateLimit>3072</tt:BitrateLimit></tt:RateControl>
                  <tt:H264><tt:GovLength>100</tt:GovLength><tt:H264Profile>Main</tt:H264Profile></tt:H264>
                </tt:VideoEncoderConfiguration>
              </trt:Profiles>
              <trt:Profiles token="profile2" fixed="true">
                <tt:Name>profile2</tt:Name>
                <tt:VideoEncoderConfiguration token="VideoEncoderToken2">
                  <tt:Name>VideoEncoder2</tt:Name><tt:UseCount>1</tt:UseCount><tt:Encoding>H264</tt:Encoding>
                  <tt:Resolution><tt:Width>704</tt:Width><tt:Height>576</tt:Height></tt:Resolution>
                  <tt:Quality>4</tt:Quality>
                  <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit><tt:EncodingInterval>1</tt:EncodingInterval><tt:BitrateLimit>768</tt:BitrateLimit></tt:RateControl>
                  <tt:H264><tt:GovLength>100</tt:GovLength><tt:H264Profile>Main</tt:H264Profile></tt:H264>
                </tt:VideoEncoderConfiguration>
              </trt:Profiles>
            </trt:GetProfilesResponse>
          </SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    private static XElement Body(string envelope) => XDocument.Parse(envelope).Root!.Elements().First(x => x.Name.LocalName == "Body");

    [Fact]
    public void Password_digest_is_sha1_of_nonce_created_and_password()
    {
        var nonce = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();

        // Same value as: (nonce bytes; printf '2026-11-02T11:50:40Zhaslo') | openssl dgst -sha1 -binary | base64
        Assert.Equal("aqk/wWWQHVkP/FoYdLo0vGVpDXA=", OnvifClient.PasswordDigest(nonce, "2026-11-02T11:50:40Z", "haslo"));
    }

    [Fact]
    public void Security_header_carries_the_camera_time_and_the_nonce()
    {
        var nonce = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        var header = OnvifClient.BuildSecurityHeader("admin", "haslo", new DateTimeOffset(2026, 11, 2, 12, 50, 40, TimeSpan.FromHours(1)), nonce);
        var text = header.ToString();

        Assert.Contains(">2026-11-02T11:50:40Z<", text);
        Assert.Contains(">aqk/wWWQHVkP/FoYdLo0vGVpDXA=<", text);
        Assert.Contains(">admin<", text);
        Assert.DoesNotContain("haslo", text);
    }

    [Fact]
    public void Profiles_are_parsed_with_their_encoders()
    {
        var profiles = OnvifClient.ParseProfiles(Body(ProfilesResponse));

        Assert.Equal(new[] { "profile3", "profile1", "profile2" }, profiles.Select(x => x.Token));
        var main = profiles[1].VideoEncoder!;
        Assert.Equal("VideoEncoderToken1", main.Token);
        Assert.Equal("H264", main.Encoding);
        Assert.Equal((1920, 1080, 25, 3072, 100), (main.Width, main.Height, main.FrameRate, main.BitrateKbps, main.GovLength));
    }

    [Theory]
    [InlineData(RecordingStream.Main, null, "profile1")]
    [InlineData(RecordingStream.Sub, null, "profile2")]
    [InlineData(RecordingStream.Lowest, null, "profile3")]
    [InlineData(RecordingStream.Main, "PROFILE2", "profile2")]
    public void Profile_is_chosen_by_resolution_not_by_order(RecordingStream stream, string? token, string expected)
    {
        var profiles = OnvifClient.ParseProfiles(Body(ProfilesResponse));

        var chosen = StreamSourceResolver.SelectProfile(profiles, new RecordingQuality { Stream = stream, ProfileToken = token });

        Assert.Equal(expected, chosen!.Token);
    }

    [Fact]
    public void Encoder_values_replace_only_what_is_set()
    {
        var encoder = OnvifClient.ParseProfiles(Body(ProfilesResponse))[2].VideoEncoder!;

        var changed = OnvifClient.WithEncoderValues(encoder, new RecordingQuality { BitrateKbps = 512, KeyframeInterval = 50 })!;

        XNamespace tt = "http://www.onvif.org/ver10/schema";
        Assert.Equal("512", changed.Element(tt + "RateControl")!.Element(tt + "BitrateLimit")!.Value);
        Assert.Equal("50", changed.Element(tt + "H264")!.Element(tt + "GovLength")!.Value);
        Assert.Equal("704", changed.Element(tt + "Resolution")!.Element(tt + "Width")!.Value);
        Assert.Equal("Main", changed.Element(tt + "H264")!.Element(tt + "H264Profile")!.Value);
        Assert.Equal("VideoEncoderToken2", changed.Attribute("token")!.Value);
    }

    [Fact]
    public void Encoder_values_equal_to_the_camera_change_nothing()
    {
        var encoder = OnvifClient.ParseProfiles(Body(ProfilesResponse))[2].VideoEncoder!;

        Assert.Null(OnvifClient.WithEncoderValues(encoder, new RecordingQuality { BitrateKbps = 768, FrameRate = 25 }));
    }

    [Fact]
    public async Task Client_signs_with_the_camera_clock_and_uses_the_media_address_on_the_camera_host()
    {
        var handler = new StubHttpHandler((request, body) =>
        {
            if (body.Contains("GetSystemDateAndTime"))
            {
                return StubHttpHandler.Xml(Envelope("<tds:GetSystemDateAndTimeResponse xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:SystemDateAndTime><tt:UTCDateTime><tt:Time><tt:Hour>11</tt:Hour><tt:Minute>50</tt:Minute><tt:Second>40</tt:Second></tt:Time><tt:Date><tt:Year>2026</tt:Year><tt:Month>11</tt:Month><tt:Day>2</tt:Day></tt:Date></tt:UTCDateTime></tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>"));
            }

            if (body.Contains("GetCapabilities"))
            {
                // The camera reports its factory address - the client must keep talking to the one it was given.
                return StubHttpHandler.Xml(Envelope("<tds:GetCapabilitiesResponse xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:Capabilities><tt:Media><tt:XAddr>http://192.168.1.210:80/onvif/Media</tt:XAddr></tt:Media></tds:Capabilities></tds:GetCapabilitiesResponse>"));
            }

            if (body.Contains("GetStreamUri"))
            {
                return StubHttpHandler.Xml(Envelope("<trt:GetStreamUriResponse xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\"><trt:MediaUri><tt:Uri>rtsp://192.168.1.210:554/profile2</tt:Uri></trt:MediaUri></trt:GetStreamUriResponse>"));
            }

            return StubHttpHandler.Xml(ProfilesResponse);
        });
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 11, 50, 30, TimeSpan.Zero));
        using var client = new OnvifClient("192.168.10.3", "admin", "haslo", time, handler);

        var profiles = await client.GetProfilesAsync(CancellationToken.None);
        var uri = await client.GetStreamUriAsync("profile2", CancellationToken.None);

        Assert.Equal(3, profiles.Count);
        Assert.Equal("rtsp://192.168.10.3:554/profile2", uri);
        Assert.Equal("http://192.168.10.3/onvif/Media", handler.Requests[^1].Uri.ToString());
        Assert.DoesNotContain("Security", handler.Requests[0].Body);
        // The camera is 10 s ahead of the device: the token is signed with the camera's time.
        Assert.Contains(">2026-11-02T11:50:40Z<", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Fault_is_reported_with_its_subcode()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Xml("""
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><s:Fault>
            <s:Code><s:Value>s:Sender</s:Value><s:Subcode><s:Value>ter:NotAuthorized</s:Value></s:Subcode></s:Code>
            <s:Reason><s:Text xml:lang="en">Sender not Authorized</s:Text></s:Reason></s:Fault></s:Body></s:Envelope>
            """, HttpStatusCode.BadRequest));
        using var client = new OnvifClient("192.168.10.3", null, null, TimeProvider.System, handler);

        var error = await Assert.ThrowsAsync<OnvifException>(() => client.GetDeviceInformationAsync(CancellationToken.None));

        Assert.True(error.IsNotAuthorized);
        Assert.Equal("ter:NotAuthorized", error.FaultCode);
        Assert.Equal("Sender not Authorized", error.Reason);
    }

    [Fact]
    public async Task Explicit_rtsp_url_needs_no_onvif_and_gets_encoded_credentials()
    {
        var resolver = new StreamSourceResolver(new ThrowingOnvifFactory(), NullLogger<StreamSourceResolver>.Instance);
        var camera = new RecordingCameraDefinition { Id = "cam1", RtspUrl = "rtsp://192.168.10.3:554/profile2", Username = "admin", Password = "p@ss:w/rd" };

        var stream = await resolver.ResolveAsync(camera, CancellationToken.None);

        Assert.Equal("rtsp://admin:p%40ss%3Aw%2Frd@192.168.10.3:554/profile2", stream.SourceUrl);
    }

    [Fact]
    public void Url_with_credentials_is_kept_and_redacted_for_logs()
    {
        Assert.Equal("rtsp://a:b@host/p", RtspUrls.WithCredentials("rtsp://a:b@host/p", "x", "y"));
        Assert.Equal("rtsp://host:554/p", RtspUrls.Redact("rtsp://admin:secret@host:554/p"));
        Assert.Equal("rtsp://host/p@x", RtspUrls.Redact("rtsp://host/p@x"));
    }

    private static string Envelope(string body) =>
        $"<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:tt=\"http://www.onvif.org/ver10/schema\"><s:Body>{body}</s:Body></s:Envelope>";

    private sealed class ThrowingOnvifFactory : IOnvifClientFactory
    {
        public IOnvifClient Create(string host, string? username, string? password) => throw new InvalidOperationException("ONVIF must not be used");
    }
}

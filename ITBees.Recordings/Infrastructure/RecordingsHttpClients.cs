namespace ITBees.Recordings.Infrastructure;

/// <summary>Shared HTTP clients: one for the local MediaMTX (API and playback), one for downloading the release.</summary>
internal sealed class RecordingsHttpClients : IDisposable
{
    public RecordingsHttpClients()
        : this(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    internal RecordingsHttpClients(HttpMessageHandler handler)
    {
        // Clips are streamed, so the timeout only guards against a hung server.
        Local = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
        Download = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(15) };
        _handler = handler;
    }

    private readonly HttpMessageHandler _handler;

    public HttpClient Local { get; }
    public HttpClient Download { get; }

    public void Dispose()
    {
        Local.Dispose();
        Download.Dispose();
        _handler.Dispose();
    }
}

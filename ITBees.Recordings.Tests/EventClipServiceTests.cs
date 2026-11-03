using ITBees.Recordings.Models;
using ITBees.Recordings.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ITBees.Recordings.Tests;

public sealed class EventClipServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 11, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(T0);
    private readonly FakeMediaMtxClient _mediaMtx = new();
    private readonly FakeRecordingManager _manager = new();
    private readonly RecordingsOptions _options;
    private readonly List<ClipResult> _finished = new();

    public EventClipServiceTests()
    {
        _options = new RecordingsOptions { StateDirectory = Path.Combine(_temp.Path, "state"), ClipMaxWait = TimeSpan.FromMinutes(5) };
        _manager.ClipsDirectory = Path.Combine(_temp.Path, "clips");
        _manager.Configure(new[] { new RecordingCameraDefinition { Id = "wjazd", Host = "192.168.10.3" } });
        // Continuous recording from 10:45 to now (11:00), extended by the tests as time passes.
        Record(T0.AddMinutes(-15), T0);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Clip_spans_45_s_around_the_event_and_is_due_after_its_end_plus_the_settle_delay()
    {
        var service = CreateService();

        var job = service.Enqueue(Request("e1", T0));

        Assert.Equal(T0.AddSeconds(-45), job.Start);
        Assert.Equal(TimeSpan.FromSeconds(90), job.Duration);
        Assert.Equal(T0.AddSeconds(50), job.DueAt);
        Assert.Equal(Path.Combine("2026-11-02", "20261102T110000Z_wjazd_entry_e1.mp4"), job.FilePath);
    }

    [Fact]
    public async Task Clip_is_cut_only_when_due()
    {
        var service = CreateService();
        service.Enqueue(Request("e1", T0));

        _time.Advance(TimeSpan.FromSeconds(49));
        Record(T0, _time.Now);
        await service.ProcessDueJobsAsync();
        Assert.Empty(_mediaMtx.Downloads);

        _time.Advance(TimeSpan.FromSeconds(1));
        Record(T0, _time.Now);
        await service.ProcessDueJobsAsync();

        var result = Assert.Single(_finished);
        Assert.Equal(ClipOutcome.Completed, result.Outcome);
        Assert.Equal(("wjazd", T0.AddSeconds(-45), TimeSpan.FromSeconds(90)), _mediaMtx.Downloads.Single());
        Assert.Equal(Path.Combine(_manager.ClipsDirectory!, "2026-11-02", "20261102T110000Z_wjazd_entry_e1.mp4"), result.FilePath);
        Assert.True(File.Exists(result.FilePath));
        Assert.False(File.Exists(result.FilePath + ".part"));
        Assert.Empty(service.PendingJobs);
    }

    [Fact]
    public async Task Two_events_closer_than_the_clip_length_share_footage_in_separate_files()
    {
        var service = CreateService();
        service.Enqueue(Request("first", T0));
        service.Enqueue(Request("second", T0.AddSeconds(30)));

        _time.Advance(TimeSpan.FromSeconds(80));
        Record(T0, _time.Now);
        await service.ProcessDueJobsAsync();

        Assert.Equal(2, _finished.Count);
        Assert.Equal(T0.AddSeconds(-45), _mediaMtx.Downloads[0].Start);
        Assert.Equal(T0.AddSeconds(-15), _mediaMtx.Downloads[1].Start);
        Assert.NotEqual(_finished[0].FilePath, _finished[1].FilePath);
        Assert.All(_finished, x => Assert.Equal(ClipOutcome.Completed, x.Outcome));
    }

    [Fact]
    public async Task Clips_of_one_group_land_in_one_package_with_their_metadata()
    {
        var service = CreateService();
        var metadata = new Dictionary<string, string> { ["sessionId"] = "516382", ["plate"] = "WB784CX" };
        service.Enqueue(Request("entry-1", T0) with { GroupId = "session-516382", Metadata = metadata });
        _manager.Configure(new[] { new RecordingCameraDefinition { Id = "wjazd", Host = "192.168.10.3" }, new RecordingCameraDefinition { Id = "wyjazd", Host = "192.168.10.4" } });
        _mediaMtx.Spans["wyjazd"] = new List<RecordedSpan> { new(T0.AddMinutes(-15), TimeSpan.FromMinutes(30)) };
        service.Enqueue(Request("exit-1", T0.AddMinutes(10)) with { CameraId = "wyjazd", Label = "exit", GroupId = "session-516382" });

        _time.Advance(TimeSpan.FromMinutes(11));
        Record(T0, _time.Now);
        await service.ProcessDueJobsAsync();

        var package = Path.Combine(_manager.ClipsDirectory!, "session-516382");
        Assert.Equal(new[] { "20261102T110000Z_wjazd_entry_entry-1.json", "20261102T110000Z_wjazd_entry_entry-1.mp4", "20261102T111000Z_wyjazd_exit_exit-1.json", "20261102T111000Z_wyjazd_exit_exit-1.mp4" },
            Directory.GetFiles(package).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));

        var json = File.ReadAllText(_finished[0].MetadataPath!);
        Assert.Contains("\"eventTime\": \"2026-11-02T11:00:00+00:00\"", json);
        Assert.Contains("\"clipStart\": \"2026-11-02T10:59:15+00:00\"", json);
        Assert.Contains("\"outcome\": \"Completed\"", json);
        Assert.Contains("\"plate\": \"WB784CX\"", json);
        Assert.Contains("\"groupId\": \"session-516382\"", json);
    }

    [Fact]
    public void Same_event_is_queued_once()
    {
        var service = CreateService();

        var first = service.Enqueue(Request("e1", T0));
        var again = service.Enqueue(Request("e1", T0));

        Assert.Equal(first.Id, again.Id);
        Assert.Single(service.PendingJobs);
    }

    [Fact]
    public async Task Gap_in_the_recording_gives_a_partial_clip_from_where_the_recording_starts()
    {
        _mediaMtx.Spans.Clear();
        // Camera came back 20 s after the event.
        Record(T0.AddSeconds(20), T0.AddMinutes(5));
        var service = CreateService();
        service.Enqueue(Request("e1", T0));

        _time.Advance(TimeSpan.FromMinutes(1));
        await service.ProcessDueJobsAsync();

        var result = Assert.Single(_finished);
        Assert.Equal(ClipOutcome.Partial, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(25), result.Recorded);
        Assert.Equal((T0.AddSeconds(20), TimeSpan.FromSeconds(25)), (_mediaMtx.Downloads[0].Start, _mediaMtx.Downloads[0].Duration));
    }

    [Fact]
    public async Task Nothing_recorded_is_retried_until_the_max_wait_and_then_reported()
    {
        _mediaMtx.Spans.Clear();
        var service = CreateService();
        service.Enqueue(Request("e1", T0));

        _time.Advance(TimeSpan.FromMinutes(1));
        await service.ProcessDueJobsAsync();
        Assert.Empty(_finished);
        Assert.Equal(1, service.PendingJobs.Single().Attempts);

        _time.Advance(TimeSpan.FromMinutes(5));
        await service.ProcessDueJobsAsync();

        Assert.Equal(ClipOutcome.NoRecording, Assert.Single(_finished).Outcome);
        Assert.Null(_finished[0].FilePath);
        Assert.Empty(service.PendingJobs);
    }

    [Fact]
    public async Task Missing_tail_waits_while_the_camera_records()
    {
        var service = CreateService();
        service.Enqueue(Request("e1", T0));

        // Due, but MediaMTX has written only up to 30 s after the event.
        _time.Advance(TimeSpan.FromSeconds(50));
        Record(T0, T0.AddSeconds(30));
        await service.ProcessDueJobsAsync();
        Assert.Empty(_finished);

        Record(T0.AddSeconds(30), T0.AddSeconds(60));
        _time.Advance(TimeSpan.FromSeconds(5));
        await service.ProcessDueJobsAsync();

        Assert.Equal(ClipOutcome.Completed, Assert.Single(_finished).Outcome);
    }

    [Fact]
    public async Task Queued_clips_survive_a_restart_of_the_host()
    {
        CreateService().Enqueue(Request("e1", T0));

        var restarted = CreateService();
        Assert.Single(restarted.PendingJobs);

        _time.Advance(TimeSpan.FromMinutes(1));
        Record(T0, _time.Now);
        await restarted.ProcessDueJobsAsync();

        Assert.Equal(ClipOutcome.Completed, Assert.Single(_finished).Outcome);
        Assert.Empty(CreateService().PendingJobs);
    }

    [Fact]
    public void Unknown_camera_and_too_long_clips_are_refused()
    {
        var service = CreateService();

        Assert.Throws<ArgumentException>(() => service.Enqueue(Request("e1", T0) with { CameraId = "nope" }));
        Assert.Throws<ArgumentException>(() => service.Enqueue(Request("e1", T0) with { Before = TimeSpan.FromMinutes(20), After = TimeSpan.FromMinutes(20) }));
        Assert.Throws<ArgumentException>(() => service.Enqueue(Request("", T0)));
    }

    [Fact]
    public async Task Packages_and_their_clips_are_listed_with_metadata()
    {
        var service = CreateService();
        service.Enqueue(Request("e1", T0) with { GroupId = "session-1", Metadata = new Dictionary<string, string> { ["plate"] = "WB784CX" } });
        service.Enqueue(Request("e2", T0.AddSeconds(30)) with { GroupId = "session-1" });
        service.Enqueue(Request("e3", T0.AddSeconds(10)));
        _time.Advance(TimeSpan.FromMinutes(2));
        Record(T0, _time.Now);
        await service.ProcessDueJobsAsync();

        var packages = service.ListPackages();
        var session = Assert.Single(packages, x => x.Name == "session-1");
        Assert.Equal(2, session.ClipCount);
        Assert.Equal(T0, session.FirstEventTime);
        Assert.Equal(T0.AddSeconds(30), session.LastEventTime);
        Assert.Contains(packages, x => x.Name == "2026-11-02" && x.ClipCount == 1);

        var clips = service.ListClips("session-1");
        Assert.Equal(new[] { "session-1/20261102T110000Z_wjazd_entry_e1.mp4", "session-1/20261102T110030Z_wjazd_entry_e2.mp4" }, clips.Select(x => x.RelativePath));
        Assert.Equal("WB784CX", clips[0].Metadata!.Metadata["plate"]);
        Assert.Equal(ClipOutcome.Completed, clips[0].Metadata!.Outcome);
        Assert.NotNull(service.GetClipPath(clips[0].RelativePath));
    }

    [Theory]
    [InlineData("../state/clip-jobs.json")]
    [InlineData("session-1/../../secret.mp4")]
    [InlineData("session-1/missing.mp4")]
    [InlineData("")]
    public void Clip_paths_outside_the_clips_directory_are_refused(string relativePath)
    {
        Assert.Null(CreateService().GetClipPath(relativePath));
    }

    [Fact]
    public void Package_names_with_path_characters_are_refused()
    {
        Assert.Empty(CreateService().ListClips("../state"));
    }

    [Fact]
    public async Task Export_writes_what_is_recorded_now()
    {
        var service = CreateService();
        var file = Path.Combine(_temp.Path, "export.mp4");

        var result = await service.ExportAsync("wjazd", T0.AddMinutes(-2), TimeSpan.FromMinutes(1), file);

        Assert.Equal(ClipOutcome.Completed, result.Outcome);
        Assert.True(File.Exists(file));
    }

    private EventClipService CreateService()
    {
        var service = new EventClipService(Options.Create(_options), _manager, _mediaMtx, _time, NullLogger<EventClipService>.Instance);
        service.ClipFinished += (_, e) => _finished.Add(e.Result);
        return service;
    }

    private static ClipRequest Request(string eventId, DateTimeOffset time) =>
        new() { CameraId = "wjazd", EventId = eventId, EventTime = time, Label = "entry" };

    // Extends the recording like MediaMTX does: adjacent stretches merge into one span.
    private void Record(DateTimeOffset from, DateTimeOffset to)
    {
        if (_mediaMtx.Spans.TryGetValue("wjazd", out var spans) == false)
        {
            _mediaMtx.Spans["wjazd"] = spans = new List<RecordedSpan>();
        }

        var touching = spans.FirstOrDefault(x => x.Start <= from && x.End >= from);
        if (touching != null)
        {
            spans.Remove(touching);
            from = touching.Start;
            to = to > touching.End ? to : touching.End;
        }

        spans.Add(new RecordedSpan(from, to - from));
    }
}

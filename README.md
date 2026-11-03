# ITBees.Recordings

Continuous camera recording for Linux devices (e.g. an Octopark column or local server), built on
[MediaMTX](https://github.com/bluenviron/mediamtx). The library:

- installs MediaMTX **at runtime** (pinned release, SHA-256 checked, no build step, no apt) and runs it
  as a **systemd service** under the account of the host application,
- records every configured camera **continuously** into fixed-length fMP4 files (15 min by default),
  overwriting the oldest files when the disk reaches a usage limit (ring buffer),
- finds the stream over **ONVIF** (main / sub / lowest profile, or an explicit token) and can set the
  camera encoder (resolution, bitrate, fps, key-frame interval) to control file size,
- optionally records onto a **USB disk** (local server), falling back to a folder on the device while no disk
  is plugged in,
- cuts **event clips** (45 s before + 45 s after by default) out of the continuous recording.

It depends on neither FAS nor EF - only on `Microsoft.Extensions.*`.

## Two processes

| | What | Where |
|---|---|---|
| **Recorder** | MediaMTX pulls RTSP from the cameras and writes `recordings/<camera>/<start>.mp4`, one file per 15 min, 1 s parts (a power cut loses at most 1 s). | systemd unit `itbees-recordings` - keeps recording while the host application restarts or updates |
| **Clipper** | `IEventClipService` queues a clip per event (kept on disk), waits until the recording past its end is written, then fetches the range from the MediaMTX playback server as a standard MP4. | background service in the host application |

Every event gets its **own file**: two entries 30 s apart give two 90 s clips that share 60 s of
footage. Nothing is re-encoded, so CPU use stays at a few percent even on a Raspberry Pi.

## Quick start

```csharp
services.AddRecordings(configuration.GetSection(RecordingsOptions.SectionName));
```

`appsettings.json`:

```json
{
  "Recordings": {
    "SegmentDuration": "00:15:00",
    "MaxDiskUsagePercent": 90,
    "ClipBefore": "00:00:45",
    "ClipAfter": "00:00:45",
    "Cameras": [
      {
        "Id": "wjazd",
        "Host": "192.168.10.3",
        "Username": "admin",
        "Password": "onvif-password",
        "Quality": { "Stream": "Sub", "KeyframeInterval": 50 }
      }
    ]
  }
}
```

A parking event:

```csharp
eventClipService.Enqueue(new ClipRequest
{
    CameraId = "wjazd",
    EventId = journalEventGuid.ToString(),
    EventTime = DateTimeOffset.UtcNow,
    Label = "entry",
    GroupId = "session-516382",                       // package: one folder per session / passage
    Metadata = new Dictionary<string, string> { ["plate"] = "WB784CX" },
});

eventClipService.ClipFinished += (_, e) =>
{
    // e.Result.Outcome: Completed / Partial (gap in the recording) / NoRecording / Failed
    // e.Result.FilePath:     clips/session-516382/20261102T110000Z_wjazd_entry_<event>.mp4
    // e.Result.MetadataPath: the same name with .json (times, outcome, metadata)
};
```

The camera set can change at runtime with `IRecordingManager.Configure(...)`; MediaMTX reloads its
configuration without dropping the other cameras. `Enabled` / `IRecordingManager.SetEnabled(bool)` switch the
recorder on and off at runtime (e.g. only the device acting as the recorder records): while off nothing is
installed, and a running service is stopped only after `DisabledStopDelay` (2 min) - a host that decides after
its start whether to record does not cut the recording at every restart. Storage is checked before anything is
downloaded. `SetMaxDiskUsagePercent(int)` changes the disk limit at runtime, `SetUsbStorage(bool)` the storage
(USB disk or the device folder; recorded files stay where they are). `IRecordingManager.GetStatus()` and
the `StatusChanged` event report per camera `Recording` / `Connecting` / `Error` / `NoStorage` and `RecordedSince`
(the oldest segment still on disk).
On-demand footage: `IEventClipService.ListRecordedAsync` (recorded stretches, gaps = camera offline)
and `ExportAsync` (any range to a file). Browsing clips: `ListPackages(skip, take)` (groups and days, newest
first), `ListClips(package)` (with the `.json` metadata) and `GetClipPath(relativePath)` (refuses paths outside
the clips directory).

## Quality and size

No transcoding on the device - size is controlled at the camera. One hour takes about
**bitrate [kb/s] × 0.45 MB**. Measured on IPOX PX-TZIP2012IR7LPR-WG (fw 5.1):

| Profile | Picture | Bitrate | Per hour | Per day |
|---|---|---|---|---|
| `profile1` (Main) | 1920×1080, 25 fps | 3072 kb/s | ~1.4 GB | ~33 GB |
| `profile2` (Sub) | 704×576, 25 fps | 768 kb/s | ~0.35 GB | ~8 GB |
| `profile3` (Lowest) | 480×240, 25 fps | 512 kb/s | ~0.23 GB | ~5.5 GB |

`Quality` picks the profile (`Stream` = Main/Sub/Lowest by resolution, or `ProfileToken`) and,
when `Width`/`Height`/`FrameRate`/`BitrateKbps`/`KeyframeInterval` are set, writes them to that
profile's encoder over ONVIF (resolution must be one the camera offers). A clip can only start on a
key frame: with the factory GOP of 100 at 25 fps a clip may start up to 4 s early - `KeyframeInterval`
= 2 × fps (50) is a good setting.

## Cameras and ONVIF

- ONVIF is only discovery and settings (SOAP on port 80, `/onvif/device_service`); the picture is RTSP.
- IPOX/TVT cameras have a **separate ONVIF account list** (web panel: Network → Advanced → Onvif).
  Its password is not the web admin password; the account also opens RTSP.
- Requests are signed with WS-UsernameToken using the **camera's** clock (read first), HTTP Digest is
  answered too.
- The stream address is taken from `GetStreamUri`; the host part is replaced with the address the
  camera is reached by. An explicit `RtspUrl` skips ONVIF (credentials are added URL-encoded).

## Storage

```
<root>/recordings/<camera>/2026-11-02_12-00-00-123456.mp4             continuous recording
<root>/clips/<group>/20261102T110000Z_<camera>_<label>_<event>.mp4    event clip (GroupId set)
<root>/clips/<group>/20261102T110000Z_<camera>_<label>_<event>.json   its metadata
<root>/clips/2026-11-02/…                                             clips without a group
```

- `<root>` = `StorageDirectory` (`/var/lib/itbees-recordings`), or with `UsbStorage: true`
  `<mount>/itbees-recordings` on the first USB partition (`UsbLabel` picks one). An unmounted disk is
  mounted at `UsbMountPoint` (FAT/exFAT/NTFS with the app's uid/gid, ext4 as is). While no USB disk is
  usable the recordings go to `StorageDirectory` on the device (`UsbFallbackToStorageDirectory`, on by default;
  off = record nothing), and a disk plugged in later is picked up on the next pass (about 30 s).
  `RecordingStatus.UsbDiskInUse` tells which one is used.
- **Overwriting:** when the file system is fuller than `MaxDiskUsagePercent` (90 %, as `df` shows it)
  or our files exceed `MaxStorageMegabytes` (optional), the oldest files are deleted - continuous
  recordings and clips alike, by age - until the usage is 1 point under the limit. The file a camera is
  writing is never touched. `RecordingRetention` / `ClipRetention` add optional age limits (off by default).
- Clip metadata (`.json`): camera, event id, label, group, event time in UTC and local time, requested and
  recorded range, outcome and the caller's `Metadata`.

## Device requirements

- Linux, `systemd`, `lsblk`, `mountpoint` (util-linux).
- The host application's account needs passwordless `sudo -n` for `install`, `mkdir`, `mount` and
  `systemctl` - the same as the other device helpers of Octopark ColumnApp.
- Internet on the first start (GitHub download, ~15 MB), or `MediaMtxArchivePath` pointing to
  `mediamtx_v1.21.3_linux_<arch>.tar.gz` (arm64 / armv7 / armv6 / amd64).
- Ports on localhost: 8554 (RTSP live view), 9996 (playback), 9997 (API). Set `RtspAddress` /
  `PlaybackAddress` to `:8554` / `:9996` to open them to the LAN.

## Known limits

- File names and clip times use the device clock. A column whose RTC lost time after a power cut
  records under a wrong date until NTP corrects it.
- `ManageMediaMtx: false` (developer machine, Windows) only talks to a MediaMTX started by hand.
- A FAT/exFAT disk that the desktop already auto-mounted for another user (`/media/pi/...`) is not
  writable for a different service account - use ext4, or let the library mount the disk itself.
- Updating MediaMTX = new `MediaMtxRelease.Version` + checksums from the release's `checksums.sha256`.

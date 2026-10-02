# Sightline

**A REX Technologies product.** The app a Wi-Fi action camera should have come with: connect in
one tap, see what the camera sees, control everything, and keep your internet while you do.

> **Status: 0.1.0, under construction.** The protocol library and the `sightline` command line
> work end to end against a real camera. The Android and Windows apps are being built on top of
> them. What has been observed on real hardware is recorded only in
> [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md); a capability without a dated line there is not claimed.

## Which cameras

Generalplus-based Wi-Fi action cameras: the ones whose Wi-Fi name starts `ActionCam_`, that live
at `192.168.100.1`, stream RTSP on port 8080 at `/?action=stream` and take commands on port 8081.
The vendor apps that ship with them (XDV, Renkforce Action Cam, CAM 4S Plus) target a different
chip family entirely, which is why they half-work. See [docs/CAMERAS.md](docs/CAMERAS.md).

## Try the command line

```powershell
dotnet build Sightline.slnx
$s = "apps/windows/src/Sightline.Cli/bin/Debug/net10.0/sightline.exe"

& $s adapters                                  # which Wi-Fi adapter the camera would use
& $s connect --ssid ActionCam_xxxxxxxxxxxx     # join it, keeping your internet on the other one
& $s status                                    # mode, recording, power
& $s settings                                  # every setting the camera supports
& $s snapshot --out frame.jpg                  # one picture from the live view
& $s photo                                     # take a photo onto the camera's card
& $s record                                    # start or stop recording to the card
& $s disconnect
```

The camera's Wi-Fi switches itself off after about a minute of nobody talking to it; press its
Wi-Fi button to wake it.

## Repository

| Path | What |
| --- | --- |
| `protocol/` | The wire protocol in C# and Kotlin, both held to the same golden vectors from a real camera. |
| `apps/windows/` | The shared core, the `sightline` command, and the Windows app. |
| `apps/android/` | The Android app. |
| `docs/` | Architecture, the protocol as measured, acceptance, decisions. |

## Licence

MIT. Copyright (c) 2026 REX Technologies. See [LICENSE](LICENSE).

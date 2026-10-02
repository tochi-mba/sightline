# On-device acceptance

The only place this repository makes claims about a real camera or a real network. A line with a
date was observed on that date; a line without one has not been observed and is not claimed.

Reference camera: Wi-Fi name `ActionCam_f8160c220c72`, firmware `20240708 V1.3`, Generalplus.
Reference PC: Windows 11, camera joined on a TP-Link USB Wi-Fi adapter, internet over a USB-tethered
phone and a WireGuard VPN.

## Staying online

| # | Check | Date | Result |
| --- | --- | --- | --- |
| N1 | PC joins the camera on its spare Wi-Fi adapter; the default route stays on the tethered link | 2026-10-02 | Pass |
| N2 | PC internet answers an HTTP request while connected to the camera | 2026-10-02 | Pass |
| N3 | PC internet answers after leaving the camera | 2026-10-02 | Pass |
| N4 | The camera advertises itself as a default gateway | 2026-10-02 | Observed (yes) |
| N5 | Phone joins the camera while mobile data carries the internet | | Not run: the reference phone is the PC's internet, and joining it to the camera took the PC offline |

## Control (GPSOCKET, TCP 8081)

| # | Check | Date | Result |
| --- | --- | --- | --- |
| C1 | `GetDeviceStatus` is acknowledged | 2026-10-02 | Pass, 16-byte payload |
| C2 | `GetParameterFile` returns the whole menu | 2026-10-02 | Pass, 18,523 bytes in 38 chunks |
| C3 | The menu parses into 21 settings in 4 categories | 2026-10-02 | Pass |
| C4 | `PlaybackGetFileCount` in browse mode | 2026-10-02 | Pass, 2 files |
| C5 | `SetMode(record)` is acknowledged | 2026-10-02 | Pass |
| C6 | `RestartStreaming` is acknowledged | 2026-10-02 | Pass |
| C7 | `CapturePicture` takes a photo onto the card | | |
| C8 | `RecordToggle` starts and stops recording | | |
| C9 | `MenuSetParameter` changes a setting, read back | | |
| C10 | List, download and delete a file | | |

## Picture (RTSP, TCP 8080)

| # | Check | Date | Result |
| --- | --- | --- | --- |
| P1 | DESCRIBE, SETUP on `track0`, PLAY all answer 200 | 2026-10-02 | Pass |
| P2 | Frames reassemble into valid JPEGs | 2026-10-02 | Pass, 28 of 28 |
| P3 | 640×360 at about 12 frames a second | 2026-10-02 | Pass, 12.2 fps |
| P4 | `sightline snapshot` saves a real picture through the whole stack | 2026-10-02 | Pass |
| P5 | The Windows app shows the live picture | | **Fails**: the app reads the menu before starting the stream, and RTSP is then unanswered. See PROTOCOL.md, open questions |

## Command line

| # | Check | Date | Result |
| --- | --- | --- | --- |
| L1 | `sightline adapters` picks the idle adapter and says the internet is not affected | 2026-10-02 | Pass |
| L2 | `sightline connect` joins the camera | 2026-10-02 | Pass |
| L3 | `sightline status` | 2026-10-02 | Pass |
| L4 | `sightline settings` lists all 21 settings with their options | 2026-10-02 | Pass |
| L5 | `sightline snapshot` | 2026-10-02 | Pass |
| L6 | `sightline disconnect` leaves and removes the profile | 2026-10-02 | Pass |

## Windows app

| # | Check | Date | Result |
| --- | --- | --- | --- |
| W1 | Opens in the REX theme and finds the camera in range by itself | 2026-10-02 | Pass |
| W2 | Says before connecting that the internet is not affected | 2026-10-02 | Pass |
| W3 | Connects and shows the camera's real settings and firmware | 2026-10-02 | Pass |
| W4 | Shows the live picture | | Fails, see P5 |
| W5 | A failed picture says why instead of staying black | 2026-10-02 | Pass |

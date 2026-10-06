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
| C7 | `CapturePicture` takes a photo onto the card | 2026-10-06 | Pass, the card went from 4 files to 5 |
| C8 | `RecordToggle` starts and stops recording | 2026-10-06 | Pass, status read recording, then not; the card went to 6 files |
| C9 | `MenuSetParameter` changes a setting, read back | | |
| C10 | List, download and delete a file | | |

## Picture (RTSP, TCP 8080)

| # | Check | Date | Result |
| --- | --- | --- | --- |
| P1 | DESCRIBE, SETUP on `track0`, PLAY all answer 200 | 2026-10-02 | Pass |
| P2 | Frames reassemble into valid JPEGs | 2026-10-02 | Pass, 28 of 28 |
| P3 | 640×360 at about 12 frames a second | 2026-10-02 | Pass, 12.2 fps |
| P4 | `sightline snapshot` saves a real picture through the whole stack | 2026-10-02 | Pass |
| P5 | The Windows app shows the live picture | | **Fails**: the app closes the stream and opens another, and the camera answers only one stream per power-on. See PROTOCOL.md, "One stream per power-on" |
| P6 | A second RTSP connection is answered after the first is closed | 2026-10-06 | **No**, with or without `TEARDOWN`, and after the camera hung up the first itself |
| P7 | `TEARDOWN` and `PAUSE` stop the stream | 2026-10-06 | **No**: 501 Not Implemented, and 200 OK with the stream still coming |
| P8 | `DESCRIBE`, `SETUP`, `PLAY` again on the first connection | 2026-10-06 | Pass |
| P9 | `SetMode(browse)` while streaming | 2026-10-06 | The camera hangs up the stream connection at once |
| P10 | The stream starts after the whole menu and every setting are read | 2026-10-06 | Pass, on a camera that had not streamed since it was switched on |

## Command line

| # | Check | Date | Result |
| --- | --- | --- | --- |
| L1 | `sightline adapters` picks the idle adapter and says the internet is not affected | 2026-10-02 | Pass |
| L2 | `sightline connect` joins the camera | 2026-10-02 | Pass |
| L3 | `sightline status` | 2026-10-02 | Pass |
| L4 | `sightline settings` lists all 21 settings with their options | 2026-10-02 | Pass |
| L5 | `sightline snapshot` | 2026-10-02 | Pass |
| L6 | `sightline disconnect` leaves and removes the profile | 2026-10-02 | Pass |
| L7 | The whole command set in one sitting: connect, status, settings, files, snapshot, photo, record start and stop, disconnect, on the TP-Link adapter, with the PC's internet answering afterwards and the built-in adapter untouched | 2026-10-06 | Pass |
| L8 | `sightline disconnect` removes the profile, five sittings in a row | 2026-10-06 | Pass. Once before that, the profile was found still on the adapter afterwards; the cause was not reproduced, and a profile Windows will not delete is now reported |

## Windows app

| # | Check | Date | Result |
| --- | --- | --- | --- |
| W1 | Opens in the REX theme and finds the camera in range by itself | 2026-10-02 | Pass |
| W2 | Says before connecting that the internet is not affected | 2026-10-02 | Pass |
| W3 | Connects and shows the camera's real settings and firmware | 2026-10-02 | Pass |
| W4 | Shows the live picture | | Fails, see P5 |
| W5 | A failed picture says why instead of staying black | 2026-10-02 | Pass |

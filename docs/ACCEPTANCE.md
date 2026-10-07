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
| N5 | Phone joins the camera through Sightline while mobile data carries the internet | | Not run yet: needs N6's saved network kept from joining by itself first |
| N6 | Phone's Wi-Fi turned on while the camera is saved on it as an ordinary network | 2026-10-06 | **The PC lost the internet for about 20 seconds.** Android joined the saved camera network by itself, before Sightline asked for anything, and USB tethering moved its upstream from mobile data to Wi-Fi. It came back as soon as the phone's Wi-Fi was turned off. Turn off Auto reconnect for a saved camera network on a phone that tethers |

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
| C9 | `MenuSetParameter` changes a setting, read back | 2026-10-06 | Pass: Record Resolution set to 2.7K, 1080FHD, 720P and back to 4K, each read back as set, and status byte 4 followed it |
| C10 | List, download and delete a file | 2026-10-06 | Pass: 8 files listed; a 215 KB photo (a whole JPEG, padded with 4 zero bytes) and a 1.3 MB clip (RIFF/AVI) downloaded at about 750 to 870 KB/s; the clip, made by an earlier test, deleted; 7 files left |
| C11 | Thumbnails of every file, photos and videos, in browse mode | 2026-10-06 | Pass: 2 to 6 KB each, 70 to 320 ms each |
| C12 | The camera's buttons work after a session with no live picture | 2026-10-06 | Pass, after listing, thumbnails, and downloading and deleting |
| C13 | The camera's buttons work after its live picture has run | 2026-10-06 | Over UDP, **yes**, after every stream (P13 to P15). Over TCP, **no**: frozen on the Wi-Fi screen until the battery is taken out, however the stream ended. See PROTOCOL.md |
| C14 | `PowerOff` switches off a frozen camera | 2026-10-06 | **No**: acknowledged, and ignored |
| C15 | What each record resolution really records, read from each frame's own JPEG header | 2026-10-06 | "4K" and "2.7K" record 1920×1080 MJPEG at 30 fps, the same as "1080FHD"; "720P" records 1280×720. Every clip already on the card was 1920×1080 too |
| C16 | A clip recorded at the "4K" setting downloads whole | 2026-10-06 | Pass: 5 seconds, 6.4 MB, an AVI of 1920×1080 MJPEG at 30 fps with 16 kHz mono PCM, in 7.2 seconds |
| C17 | `Playback_Start` plays a clip from the card | 2026-10-06 | **No**: sent after browse mode, `RestartStreaming` and a UDP RTSP session, it made the camera drop off Wi-Fi |

## Picture (RTSP on TCP 8080, pictures over UDP)

| # | Check | Date | Result |
| --- | --- | --- | --- |
| P1 | DESCRIBE, SETUP on `track0`, PLAY all answer 200 | 2026-10-02 | Pass |
| P2 | Frames reassemble into valid JPEGs | 2026-10-02 | Pass, 28 of 28 |
| P3 | 640×360 at about 12 frames a second | 2026-10-02 | Pass, 12.2 fps |
| P4 | `sightline snapshot` saves a real picture through the whole stack | 2026-10-02 | Pass |
| P5 | The Windows app's camera code shows the live picture | 2026-10-06 | Pass: the app's controller, joining on the TP-Link adapter, played 12.4 frames a second. Before that day it failed: it closed the stream and opened another, and the camera answers one stream per power-on |
| P11 | Letting go of the picture and coming back to it keeps the one stream, over TCP | 2026-10-06 | Pass: 3 seconds let go, then 13.0 frames a second on the same connection. The apps no longer keep it: over UDP the stream stops when nobody watches and another starts |
| P12 | Reading the card while holding the picture says it needs the camera switched off and on, over TCP | 2026-10-06 | Pass: 8 files listed, then the live view said so at once, with nothing retried. Over UDP the picture comes back after the card is read instead (P14) |
| P6 | A second RTSP connection is answered after the first is closed, over TCP | 2026-10-06 | **No**, with or without `TEARDOWN`, and after the camera hung up the first itself |
| P7 | `TEARDOWN` and `PAUSE` stop the stream | 2026-10-06 | **No**: 501 Not Implemented, and 200 OK with the stream still coming |
| P8 | `DESCRIBE`, `SETUP`, `PLAY` again on the first connection | 2026-10-06 | Pass |
| P9 | `SetMode(browse)` while streaming | 2026-10-06 | The camera hangs up the stream connection at once, over TCP and over UDP |
| P10 | The stream starts after the whole menu and every setting are read | 2026-10-06 | Pass, on a camera that had not streamed since it was switched on |
| P13 | Three UDP streams in a row, each on a new RTSP connection | 2026-10-06 | Pass: each answered 200 to DESCRIBE, SETUP and PLAY and sent about 240 packets in two seconds; the camera's buttons worked afterwards |
| P14 | Browse mode in the middle of a UDP stream, then a new stream | 2026-10-06 | Pass: the camera closed the stream, the file list and a thumbnail worked, and the next stream sent about 240 packets in two seconds; buttons working |
| P15 | Closing the RTSP connection stops a UDP stream | 2026-10-06 | Pass: eight stragglers within three seconds, then nothing |
| P16 | The camera takes an odd client port | 2026-10-06 | Pass: port 63721, chosen by Windows, was answered with `server_port=59728-59729` |
| P17 | The Windows app plays the picture over UDP, and again after reading the card | 2026-10-07 | Pass: the app's controller, joining on the TP-Link adapter, played 12.4 frames a second; let go and held again, 12.3; the card read while watching (5 files, 5 thumbnails), then back at 12.4. The PC's internet answered afterwards |
| P18 | The Android app plays the picture over UDP, and again after reading the card | | |
| P19 | A UDP stream keeps going for three minutes with no keepalive | 2026-10-07 | Pass: 854 to 870 packets in every ten seconds for 180 seconds, all from the camera's `server_port`; the SETUP reply names no session timeout |
| P20 | Every picture of a UDP stream arrives whole | 2026-10-07 | Pass: 61 pictures in five seconds through the app's own socket and reassembler, 0 packets lost, 0 pictures dropped |

## Playing clips from the card

| # | Check | Date | Result |
| --- | --- | --- | --- |
| V1 | The Windows app's controller plays a clip off the card as it arrives, and keeps it | 2026-10-07 | Pass: a 9.3 MB clip of 3.9 seconds (1920×1080, 30 fps by its header, 16 kHz mono sound) came off the card at 812 KB/s, a third of the speed it plays. Its header was read 0.14 seconds in and its first picture 1.3 seconds in; it was kept whole, the size the card said, and the live picture came back afterwards. A player stepped alongside started at 6.8 seconds and still had to stop and wait once near the end |
| V2 | A clip played before plays again from this PC alone | 2026-10-07 | Pass: whole in 0.08 seconds, with nothing asked of the camera |
| V3 | Closing a clip part-way stops its fetch, and the camera carries on | 2026-10-07 | Pass: let go at 3.4 MB, it stopped in 0.07 seconds and left nothing in the cache; the card was read straight after and the live picture came back. The PC's internet answered afterwards |

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
| W4 | Shows the live picture in its window | | Not yet seen on screen; its camera code passes, see P5 |
| W5 | A failed picture says why instead of staying black | 2026-10-02 | Pass |

## Distribution

| # | Check | Date | Result |
| --- | --- | --- | --- |
| D1 | A merge to `main` publishes the rolling Windows release | 2026-10-06 | Pass: installer, portable exe, command line, update package, release JSON and checksums, each also under a versioned name |
| D2 | A merge to `main` publishes the rolling Android release, signed with the release key | 2026-10-06 | Pass: 2.1 MB APK, certificate `CN=Sightline, O=REX Technologies`, SHA-256 `3e2dae5e…688e0748` |
| D3 | The installer installs for one person with no administrator prompt | 2026-10-06 | Pass: silent install in 7 seconds; Start menu and desktop shortcuts; an Installed apps entry |
| D4 | `sightline` works in a fresh terminal after installing | 2026-10-06 | Pass: found on the PATH, `--version` reports the rolling build |
| D5 | Uninstalling removes everything it added and nothing else | 2026-10-06 | Pass for the folder, shortcuts and Installed apps entry. The PATH came back one character short, a trailing separator the install had trimmed; fixed the same day and held to the exact text by a test |
| D6 | The release APK installs on the reference phone | 2026-10-06 | Pass: Samsung Galaxy S21 Ultra, Android 15, over USB |
| D7 | The site is published and says what the live picture costs | 2026-10-06 | Pass: https://tochi-mba.github.io/sightline/ answers, with the day's wording |
| D8 | Uninstalling keeps the person's settings, or asks | | Found on 2026-10-06 that it did not: the install folder was also the settings folder. Settings now live in `%LOCALAPPDATA%\REX Technologies\Sightline`, held there by a test; not yet confirmed by installing and uninstalling a build with the change |

# The camera protocol, as measured

Everything here was observed on a real camera, the reference unit: Wi-Fi name
`ActionCam_f8160c220c72`, firmware `20240708 V1.3`. Where a vendor document or an earlier guess
disagreed with the device, the device wins and the disagreement is noted. Anything not yet
observed is marked **unverified**.

## Which camera this is

A **Generalplus** SoC design. Not the Allwinner V3 design that the vendor apps "XDV",
"Renkforce Action Cam" and "CAM 4S Plus" target: those apps expect a binary control channel on
TCP 6666 and a file server on TCP 80, and on this camera **both ports are closed**. That is why
those apps half-work with it.

| Port | Service |
| --- | --- |
| TCP 8080 | RTSP — the live picture and sound |
| TCP 8081 | GPSOCKET — every command |
| TCP 8082 | Open; answers nothing that has been tried. Purpose unknown. |

A full connect-scan of ports 1–10000 found only these three. No UDP service answered a probe.

## The network

- The camera is an access point at `192.168.100.1` and runs DHCP; a client gets `192.168.100.2`.
- **It advertises itself as the default gateway.** On a machine with another route to the
  internet, the camera's route can win, depending on interface metrics. On the reference PC the
  tethered link kept the default route; the route to the camera nevertheless persisted after
  disconnecting. Never assume the camera's adapter is not carrying a default route.
- **The access point switches itself off** within about a minute of nobody using it, even with
  the camera's `Auto Power Off` and `Screen Saver` settings both Off. Pressing the camera's Wi-Fi
  button brings it back.

## GPSOCKET, the control channel (TCP 8081)

```text
request : "GPSOCKET" | uint16 LE type (1 = command) | mode_id | cmd_id | payload
ack     : "GPSOCKET" | uint16 LE type (2 = ack) | mode_id | cmd_id | uint16 LE size | payload
refusal : "GPSOCKET" | uint16 LE type (3 = nak) | mode_id | cmd_id | int16 LE reason
```

- `mode_id` and `cmd_id` are the high and low bytes of a 16-bit command number.
- **A request carries no length.** The camera knows each command's payload size.
- **The server silently drops any request that does not begin with `GPSOCKET`.** No banner, no
  error. This is why the port looks dead to every scanner and to HTTP, RTSP, telnet and ONVIF.
- **Long answers are chunked**: a run of acks of at most 242 bytes each, ended by an empty ack.
- A refusal (`nak`) has no payload: its 16-bit signed reason code sits where an ack's size does.
  That is how the firmware source builds every refusal, `gp_resp_set(NAK | cmd, reason, NULL, 0)`;
  read as a size, "busy" (−1) would be 65,535 bytes that never arrive.
- **The server takes one client at a time.** A vendor app connected to it locks this app out.

### Commands verified on the device

| Command | Number | Observed |
| --- | --- | --- |
| `GetDeviceStatus` | 0x0001 | Ack, **16-byte** payload (see below) |
| `GetParameterFile` | 0x0002 | Ack, the menu XML: 18,523 bytes in 38 chunks plus the empty end |
| `RestartStreaming` | 0x0004 | Ack, empty payload |
| `SetMode` | 0x0000 | Ack; payload one byte, 0 record, 1 capture, 2 browse |
| `PlaybackGetFileCount` | 0x0302 | Ack, `02 00` = 2 files; refused with "busy" outside browse mode |

The rest of the command set is in Appendix A of the plan and in
[`GpSockCommand.cs`](../protocol/dotnet/Sightline.Protocol/GpSock/GpSockCommand.cs); each is
**unverified** until it appears in [ACCEPTANCE.md](ACCEPTANCE.md).

### The device status

The reference camera answers with **16 bytes**: `000280010025b3000000fe7c0000a501`. Vendor
documentation for a later firmware describes 20 bytes with a different layout, and the two do not
agree past the first few fields. What is pinned down:

| Byte | Meaning | Evidence |
| --- | --- | --- |
| 0 | Mode (0 record, 1 capture, 2 browse, 3 menu) | Reported 0 while in record mode |
| 1 | bit 0 busy (recording or playing), bit 1 audio on | Audio was on |
| 3 | External power | Was 1 on USB power |

Byte 2 reads `0x80` on external power, which is not a percentage, so **battery is not decoded**.
Free space lies beyond the 16 bytes in the documented layout, so **free space is not decoded**.
Both are reported as unknown rather than guessed.

### The menu

`GetParameterFile` returns the camera's own settings catalogue as XML: 21 settings in four
categories (Record, Capture, System, Wi-Fi), each with its id, type, default and allowed values.
The app reads its settings from here rather than from a table, so a camera with a different menu
still shows the right options. The reference document is
[`protocol/golden/menu/reference-camera.xml`](../protocol/golden/menu/reference-camera.xml).

## RTSP, the picture (TCP 8080)

- `OPTIONS` answers `Public: DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE`. There is no
  `GET_PARAMETER` or `SET_PARAMETER`: RTSP is not a control plane here.
- The stream is at `rtsp://192.168.100.1:8080/?action=stream`.
- The SDP declares two tracks: `m=video 0 RTP/AVP 26` (**JPEG**) at `a=control:track0` and
  `m=audio 0 RTP/AVP 97` with `a=rtpmap:97 L16/16000/1` (**16-bit PCM, 16 kHz, mono**) at
  `track1`. The camera streams its own sound.
- **The video track URL is the base URL with `/track0` appended after the query string**:
  `rtsp://192.168.100.1:8080/?action=stream/track0`. Taking the last `a=control` line selects the
  audio track, and no picture ever arrives.
- **A DESCRIBE reply has a body** (`Content-Length`). It must be read in full, or every later read
  on the connection is misaligned.
- Session ids are one byte repeated fifteen times and change per session (`040404…`, `DEDEDE…`,
  `222222…`).

### What arrives after PLAY

- **Bare RTP, with no RTSP interleaved framing**, even though the camera answers a TCP transport
  request with `interleaved=0-1`. The first byte after the PLAY reply is `0x80`, never `0x24`.
  A client expecting `$`-framing finds `$` bytes inside the JPEG data and produces nonsense.
- Packets are split on the RTP header anchored to the stream's fixed SSRC (`0x22222222`):
  version 2, payload type 26, then sequence, timestamp and that SSRC.
- **Each frame carries a complete JFIF header in its payload**, so RFC 2435's reconstruction of
  quantisation and Huffman tables is unnecessary: fragments are concatenated from fragment
  offset 0, and the RTP marker bit ends a frame.
- Measured: **640×360, about 12.2 frames a second, about 11 KB a frame, about 1.1 Mbit/s**,
  with no sequence gaps over TCP across 218 packets.
- The camera burns a date and time into the picture, and its clock was about two years wrong.

## Open questions

- **Reading the menu before starting the stream leaves RTSP unanswered.** After
  `GetParameterFile`, a `DESCRIBE` on 8080 times out — with or without `RestartStreaming`, and even
  after `SetMode(record)` restores mode 0. Starting the stream with no menu read beforehand works.
  Under investigation; until it is understood, the app starts the picture before reading the menu.
- Whether `RestartStreaming` is ever needed on this firmware: the stream was received without it.
- What TCP 8082 is.
- The meaning of status bytes 2 and 4–15.

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
| 4 | Record resolution: the Record Resolution menu value | Read back 0, 1, 2 and 4 as each was set |

Byte 2 reads `0x80` on external power, which is not a percentage, so **battery is not decoded**.
Free space lies beyond the 16 bytes in the documented layout, so **free space is not decoded**.
Both are reported as unknown rather than guessed.

### The menu

`GetParameterFile` returns the camera's own settings catalogue as XML: 21 settings in four
categories (Record, Capture, System, Wi-Fi), each with its id, type, default and allowed values.
The app reads its settings from here rather than from a table, so a camera with a different menu
still shows the right options. The reference document is
[`protocol/golden/menu/reference-camera.xml`](../protocol/golden/menu/reference-camera.xml).

## RTSP, the picture (TCP 8080, pictures over UDP)

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
  `222222…`, `F0F0F0…`).
- TCP 8082 answers nothing: not HTTP, not RTSP, not a `GPSOCKET` frame.

### What arrives after PLAY

- **Each frame carries a complete JFIF header in its payload**, so RFC 2435's reconstruction of
  quantisation and Huffman tables is unnecessary: fragments are put together from fragment offset 0,
  and the RTP marker bit ends a frame. The stream's SSRC is fixed (`0x22222222`), payload type 26.
- A frame is about 8.8 KB in seven packets: six of 1,420 bytes of picture and a last one of 300 or so,
  each with the JPEG header's type 1, Q 1 and 640×360. Offsets, timestamps and markers were exactly as
  RFC 2435 says across 168 packets (2026-10-07).
- **Each picture is padded after its end-of-image marker** with up to seven zero bytes, to a multiple of
  eight. Of 24 pictures, 21 ended in zeros; a check for `FF D9` at the very end refuses them.
- Measured: **640×360, about 12 frames a second, about 11 KB a frame, about 115 RTP packets a
  second**.
- The camera burns a date and time into the picture, and its clock was about two years wrong.

### The stream goes over UDP (measured 2026-10-06)

The transport the stream is asked for decides whether the camera survives it.

**Over the RTSP connection** (`RTP/AVP/TCP;unicast;interleaved=0-1`) the camera:

- sends **bare RTP with no interleaved framing**, although it answers `interleaved=0-1`: the first
  byte after the PLAY reply is `0x80`, never `0x24`, and a client expecting `$`-framing finds `$`
  bytes inside the JPEG data;
- answers **only the first such connection after it is switched on**: a second one connects and its
  `DESCRIBE` is never answered, however the first ended;
- answers `TEARDOWN` with `501 Not Implemented`, though `OPTIONS` lists it, and ignores `PAUSE`;
- and once that stream ends, **its own buttons and screen freeze** on the Wi-Fi screen until its
  battery is taken out and put back. `PowerOff` is acknowledged and ignored while frozen.

| Session over TCP | Live picture | Buttons after |
| --- | --- | --- |
| List the card, thumbnails, download, delete | No | Working |
| Stream, then browse the card while it flows | Yes | **Frozen** |
| Stream, close our connection, browse, ask for a second stream | Yes | **Frozen** |
| Stream, close our connection, read the status, disconnect | Yes | **Frozen** |

**Over UDP** (`RTP/AVP;unicast;client_port=P-P+1`) none of that happens. The camera answers
`server_port=59728-59729` (for example) and sends each RTP packet as one datagram to `P`:

| Session over UDP | What happened | Buttons after |
| --- | --- | --- |
| Three streams in a row, each on a new RTSP connection | Each answered `200` to DESCRIBE, SETUP and PLAY, and sent about 240 packets in two seconds | Working |
| Browse mode in the middle of a stream | The camera closed the RTSP connection and stopped sending; the file list and a thumbnail worked | Working |
| A new stream after browsing | Answered, about 240 packets in two seconds | Working |
| Close our RTSP connection mid-stream | Eight stragglers within three seconds, then nothing | Working |

What a client does, then:

- Ask for UDP, from a socket bound to its own address on the camera's network.
- Let the system choose the port. The camera takes any port it is told, odd ones too (63721 was
  answered), and a fixed one can fall in a range Windows reserves for itself (50000–50059 and
  50166–50365 on the PC this was measured on), which refuses the bind outright.
- Send one datagram from that port to `server_port` once PLAY is answered. Windows Firewall drops
  inbound UDP it did not ask for on a public network, and lets the stream in as the reply to this.
- Close the RTSP connection to stop the stream; there is no other way, and none is needed.
- Nothing needs keeping alive: a stream ran three minutes with no request and no RTCP, at a steady
  twelve pictures a second (2026-10-07). The datagrams come from `server_port` itself.
- Start the picture whenever it is wanted, as often as it is wanted. Browsing the card ends it, and
  it can be started again once the camera is back in record or capture mode.

`RestartStreaming` followed by an RTSP session in **browse** mode also sends a picture, at 640×360
and about two frames a second.

### Playing a clip from the card (2026-10-06, 2026-10-07)

`Playback_Start` (0x0300) with the file's index as two little-endian bytes, sent after
`SetMode(browse)`, `RestartStreaming` and a UDP RTSP session, as the vendor app does it, **made the
camera drop off Wi-Fi**: the control connection was aborted and its access point disappeared. It is
not used, and is not sent to a camera again until it is understood.

So the apps play a clip by downloading it with `Playback_GetRawData` and reading it as it arrives.
What a clip from the card holds, read from the reference camera's own (2026-10-07):

- An AVI: `RIFF`/`AVI `, a `JUNK` chunk, `LIST hdrl` (`avih`, a `strl` for the pictures and one for
  the sound, `odml`), `LIST movi`, then the indexes (`ix00`, `ix01`) and another `JUNK`.
- Every picture is a whole JPEG in a `00dc` chunk, padded after its end-of-image marker as the
  stream's pictures are. The sound is 16 kHz mono 16-bit PCM in `01wb` chunks of 16,376 bytes, half a
  second each, every one written after the 10 to 13 pictures taken during it.
- The header says 30 pictures a second, and the index fills that many places: one clip held 122
  pictures in 149 places, about every fifth repeated, so the camera takes about 25 a second. A clip
  still arriving has no index yet, so the apps time its pictures by the runs of sound around them.
- It comes off the card at 700 to 875 KB a second, a third to a half of the speed a 1080p clip plays
  at, so a player waits until the rest will arrive before it is needed (ACCEPTANCE V1 to V4). Letting a
  download go part-way is safe: the camera abandons it at the next request, as for any download.

### What the record resolutions really record (2026-10-06)

Each frame of a clip was read for the size its own JPEG header declares:

| Menu choice (value) | Frames recorded | Rate |
| --- | --- | --- |
| 4K (0) | 1920×1080 MJPEG, about 60 KB a frame | 30 fps |
| 2.7K (1) | 1920×1080 MJPEG, about 60 KB a frame | 30 fps |
| 1080FHD 1920X1080 (2) | 1920×1080 MJPEG, about 60 KB a frame | 30 fps |
| 720P 1280X720 (4) | 1280×720 MJPEG, about 32 KB a frame | 30 fps |

So "4K" and "2.7K" are labels: on this camera they record what 1080FHD records. Clips are AVI with
16 kHz mono PCM sound, 10 to 19 Mbit/s at 1080p by what the picture holds, and download at 700 to
875 KB a second. Status byte 4 follows the setting (0, 1, 2 and 4 read back as set).

## Open questions

- How the camera's clock is set. The menu's `Date/Time` item (0x0205) only chooses how the date
  is written; NAK code −13 suggests a time command exists, but nothing seen so far names it, and
  commands are not guessed at on a real camera.
- Whether `RestartStreaming` is ever needed on this firmware: the stream was received without it.
- What TCP 8082 is.
- The meaning of status bytes 2 and 5–15.
- Whether a UDP stream needs keeping alive past three minutes, the longest measured so far.
- How the camera plays a clip from its card without dropping off Wi-Fi.

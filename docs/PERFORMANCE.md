# Performance

How fast Sightline's own code is, measured so that an optimisation can be proved and a regression
caught. Every figure here comes from the benchmark suite in `benchmarks/dotnet`, and every claim
about speed in this repository should come with a before and an after from it.

What this does **not** measure is the camera. How long the camera takes to answer, how many frames
a second it sends (12.2 at 640×360) and how fast its card copies (0.89 MB/s) are properties of the
hardware, measured on the device and dated in `docs/ACCEPTANCE.md`. These benchmarks measure
Sightline's share: the work between bytes arriving on a socket and a picture, a file or an answer
being ready.

## Running it

| Command | What it does |
| --- | --- |
| `./tools/scripts/bench.ps1` | Runs the whole suite in Release and compares it with the baseline. Exit 0: no regression; 1: a regression; 2: nothing to compare. |
| `./tools/scripts/bench.ps1 -Filter '*LiveView*'` | Runs and compares only the benchmarks whose names match. |
| `./tools/scripts/bench.ps1 -Record` | Replaces the baseline with this run, and writes `benchmarks/baseline/manifest.json` describing the machine. |
| `python tools/scripts/bench_compare.py artifacts/bench/<time>` | Compares a run that already exists. `--slower 5` tightens the time threshold. |

Every run writes BenchmarkDotNet's full reports under `artifacts/bench/<time>/`, which git ignores.
The whole suite takes about 13 minutes.

A regression is a benchmark more than 10% slower than the baseline, or allocating any more memory.
Allocation is held to zero tolerance because on a phone it is garbage collection, and garbage
collection is dropped frames and battery.

## What is measured

| Benchmark class | What one operation is | Why it matters |
| --- | --- | --- |
| `LiveViewReassembly` | Turning one frame's RTP packets into a JPEG, at three read sizes | Runs 12 times a second for as long as the live view is open |
| `LiveViewSession` | One frame through a whole session (`StreamFrame`), and the first picture of a stream (`FirstPicture`) | The live view end to end, short of decoding: the ceiling Sightline's side could sustain |
| `LiveViewDecode` | Decoding a 640×360 frame to pixels, full and half size | The most expensive thing done per frame |
| `ControlFraming` | Encoding and decoding single control frames | Every command and answer passes through it |
| `ControlChannel` | A status round trip, the whole menu, 200 files listed, a megabyte downloaded | What connecting, browsing and copying cost on Sightline's side |
| `CatalogueParsing` | The camera's menu XML, one page of the file list | Done at every connect and every library refresh |

Each benchmark checks its own result before it is timed, so a fast wrong answer fails the run
instead of winning it. The live-view session check is what found the reassembler bug fixed in
`f9bc661`: the first picture of a stream came back 893 bytes short.

## The baseline

`benchmarks/baseline/` holds the reports a comparison is made against, and `manifest.json` records
the commit, the machine, the power plan and the .NET runtime they came from. A figure means nothing
without the hardware behind it, so a baseline is only ever compared with runs from the same machine.

**The current baseline is provisional** (`"provisional": true` in the manifest). It was recorded on
2026-10-05 on the development laptop (Intel Core i7-10510U, 4 cores, 31.8 GB, Windows 11, .NET
10.0.12, Balanced power plan) while other work was running. It is good for catching large
regressions, and it should be re-recorded with `-Record` on an idle, plugged-in machine before any
optimisation smaller than about 10% is claimed.

| Benchmark | Mean | Allocated |
| --- | ---: | ---: |
| `LiveViewReassembly.FrameFromPackets` (1,460-byte reads) | 36.7 µs | 21.4 KB |
| `LiveViewReassembly.FrameFromPackets` (8 KB reads) | 37.3 µs | 21.2 KB |
| `LiveViewReassembly.FrameFromPackets` (64 KB reads) | 46.3 µs | 21.1 KB |
| `LiveViewSession.StreamFrame` (1,460-byte reads) | 53.7 µs | 24.2 KB |
| `LiveViewSession.StreamFrame` (16 KB reads) | 34.9 µs | 22.3 KB |
| `LiveViewSession.FirstPicture` (1,460-byte reads) | 70.3 µs | 78.9 KB |
| `LiveViewSession.FirstPicture` (16 KB reads) | 81.4 µs | 92.4 KB |
| `LiveViewDecode.DecodeFrame` | 4.44 ms | 800 B |
| `LiveViewDecode.DecodeFrameAtHalfSize` | 2.05 ms | 736 B |
| `ControlFraming.EncodeStatusRequest` | 22.5 ns | 40 B |
| `ControlFraming.DecodeStatusAnswer` | 40.1 ns | 40 B |
| `ControlFraming.DecodeRefusal` | 34.2 ns | 32 B |
| `ControlFraming.DecodeMenuChunk` | 95.6 ns | 272 B |
| `ControlFraming.DecodeDownloadFrame` | 12.6 µs | 60.0 KB |
| `ControlChannel.StatusRoundTrip` | 564.5 ns | 496 B |
| `ControlChannel.ReadWholeMenu` | 441.5 µs | 242.3 KB |
| `ControlChannel.ListTwoHundredFiles` | 238.2 µs | 46.8 KB |
| `ControlChannel.DownloadMegabyte` | 526.0 µs | 1.0 MB |
| `CatalogueParsing.ParseMenu` | 365.1 µs | 127.3 KB |
| `CatalogueParsing.ParseFileListPage` | 19.3 µs | 2.2 KB |

## What the baseline says

- **The live view is bounded by decoding, not by the protocol.** At 12.2 frames a second each frame
  has 82 ms. Reassembly and the session take under 0.1% of that; decoding takes about 5%, or half
  that at half size. Decoding is where an optimisation of the live view would pay.
- **Reads of 1,460 bytes, one Ethernet-sized TCP segment, make the session about 50% slower per
  frame** than 16 KB reads. The client already reads 16 KB at a time; the figure is there so a
  change that shrinks the buffer is noticed.
- **The first picture allocates three to four times what a later frame does**, from starting the
  stream and negotiating RTSP. It happens once per stream.
- **Copying is never Sightline's bottleneck.** A megabyte takes half a millisecond on Sightline's
  side and over a second on the camera's.
- **Every byte of a download is allocated once** (1.0 MB for 1 MB): the frames are decoded into new
  arrays. A download is limited by the camera, so this is not worth pooling yet, but it is the first
  place to look if memory on a phone becomes the concern.

## Not measured yet

- The Kotlin port, which the Android app runs. Its hot paths mirror the .NET ones line for line, and
  the same golden vectors hold both to the same output, but its speed on a phone has not been
  measured. Android benchmarks need a device and the Jetpack Microbenchmark library; until they
  exist, nothing here should be read as a claim about the phone.
- The Windows app's rendering: how long a decoded frame takes to reach the screen.

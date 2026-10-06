# AGENTS.md

Rules for everyone who changes this repository, people and coding agents alike.

## What Sightline is

An Android and Windows app, plus a command line, for Generalplus Wi-Fi action cameras: live
view, every camera setting, the card's files, a ride HUD and a background security-camera mode —
without ever taking the device offline. [docs/PROTOCOL.md](docs/PROTOCOL.md) is the camera's
protocol as measured on real hardware.

## Invariants

1. **One control connection, held open.** The camera stops recording and tears down its stream
   when the control socket on 8081 closes. Never connect per command.
2. **Start the stream on the control channel.** RTSP SETUP and PLAY can both succeed and deliver
   nothing until `RestartStreaming` (0x0004) has been sent.
3. **The stream has no interleaved framing.** After PLAY the camera sends bare RTP down the TCP
   connection. Never hand it to a stock RTSP stack and expect a picture.
4. **Never take a device offline.** Camera traffic is bound to the camera's network; nothing
   changes the default route, disables mobile data, or binds a whole process.
5. **Honesty about hardware.** No capability, figure or model is claimed until it is a dated line
   in `docs/ACCEPTANCE.md`. A field this firmware's status payload does not reliably carry is
   reported as unknown, never guessed.
6. **Everything that touches the outside world is an interface with a hand-written fake.** No
   mocking libraries.
7. **Real captures stay out of git.** They show somebody's room and carry a MAC address; they go
   in the ignored `artifacts/` folder. `protocol/golden/` holds sanitised vectors only.
8. **One defect per commit**, with a test that fails for the reason the bug existed.

## Commands

| Command | What it does |
| --- | --- |
| `dotnet build Sightline.slnx` | Builds the protocol, the core, the Windows app and the command line. |
| `dotnet test Sightline.slnx` | Runs every .NET test, the Windows window's headless tests included. See `docs/WINDOWS.md`. |
| `./gradlew :protocol:check` | Lints and tests the Kotlin protocol, and holds it to 100% line and branch coverage. |
| `./gradlew :android:core:check` | Lints and tests the Android app's logic, at 100% of lines and branches. |
| `./gradlew :android:app:check` | Lints and tests the Android app under Robolectric: logic at 100%, screens at 99% of lines. See `docs/ANDROID.md`. |
| `./gradlew :android:app:assembleDebug` | Builds the debug APK. |
| `./tools/scripts/package.ps1` | Builds the Windows installer, portable app, command line and update feed into `dist/windows`. |
| `python tools/scripts/release_version.py` | The version a release build is stamped with. See `docs/RELEASING.md`. |
| `./tools/scripts/bench.ps1` | Runs the benchmark suite and compares it with the baseline; `-Record` replaces the baseline. See `docs/PERFORMANCE.md`. |
| `python -m unittest discover tools/scripts/tests` | Tests the repository's own scripts: the site checker, the benchmark comparison and the icon generator, and holds the committed icon to the generator. |
| `python tools/scripts/generate_icon.py` | Redraws the Windows icon from the site's favicon geometry. Never edit the `.ico` by hand. |

# The Android app

Sightline for Android 10 and newer: the camera's live picture, every setting it has, its card, a ride
HUD and Sentry, without taking the phone off its internet connection.

## Modules

| Module | Folder | Holds | Held to |
| --- | --- | --- | --- |
| `:protocol` | `protocol/kotlin` | The camera's wire protocol, shared with nothing Android: GPSOCKET control, RTSP, RTP/JPEG. Its test fixtures are the fake camera every module tests against. | 100% of lines and branches |
| `:android:core` | `apps/android/core` | Everything that decides: the session, the controller, Sentry, the link advisor, the app's settings, the HUD's arithmetic, what's new. Pure JVM. | 100% of lines and branches |
| `:android:design` | `apps/android/design` | The REX tokens and components, on Compose foundation with no Material dependency. Ported from Flint. | A token test |
| `:android:app` | `apps/android/app` | The Android adapters and the screens. | 100% of lines and branches outside `ui/`; 99% of lines in `ui/` |

The rule is the plan's: a decision that can be tested without a phone lives in `:android:core`, and
`:android:app` only adapts it to Android.

## Staying online

The camera is joined with Android's local-only network request (`WifiNetworkSpecifier`, no internet
capability). Android adds the camera's Wi-Fi for Sightline's sockets only; the default route stays on
mobile data, or whatever else had it, and nothing else on the phone changes. Sightline never binds the
whole process to the camera, never switches Wi-Fi or mobile data, and never forgets a network.

Before connecting, the Live screen says in a sentence what will happen (`LinkAdvisor`): on mobile data,
nothing; on home Wi-Fi with a phone that can hold only one Wi-Fi network, that Wi-Fi pauses and mobile
data carries on; with mobile data off as well, that the phone will be offline until it disconnects.

The first connection asks for any camera whose Wi-Fi name starts `ActionCam_`, and Android shows its own
list. The camera then says its own name over the control channel, Sightline remembers it, and later
connections ask for exactly that camera, which Android grants without asking again.

## The controller

`CameraController` (in `:android:core`) is the camera as the app drives it. Its rules, each with tests:

- One camera operation at a time, because they share one control channel and some switch its mode.
- The live picture runs while a screen or an armed Sentry wants it, over UDP, and stops when nothing
  does. Reading the card pauses it, because browse mode ends the camera's stream, and it comes back
  afterwards. See PROTOCOL.md, "The stream goes over UDP".
- Every request has a deadline and every download a stall detector: a half-asleep camera accepts a
  connection and then answers nothing.
- A setting is read back after it is written, because the camera acknowledges values it ignores.
- Recording is confirmed from the camera's status, never assumed from the button press.
- A camera lost is reconnected up to five times, waiting longer each time, unless the person turned
  that off.
- A download is recognised from its first bytes, written pending, and published only once whole; a full
  phone fails the file without being taken for a lost camera.

The controller lives in the app's graph, not in a screen, so a recording or Sentry's watch survives the
screen turning off. A connected-device foreground service keeps the process alive while a camera is
connected or Sentry is armed, and its notification carries Record, Photo, Disarm and Disconnect.

## Screen sizes

- **A phone held upright:** the bar along the bottom, the controls under the picture.
- **A wide screen** (600dp and wider): a rail down the side, the controls beside the picture.
- **A phone on its side** (wide, but under 480dp tall): a narrow rail, and the controls stacked in a
  strip like a camera app's, so the picture gets the whole height.
- **The HUD** sits over the picture on translucent panels at its edges, the speed sized to the
  picture's height; the middle of the picture stays clear, and a picture that has stopped still says
  why.

## Permissions

| Permission | When it is asked | Why |
| --- | --- | --- |
| Nearby devices (Android 13+) | Connecting | To find the camera's Wi-Fi. Declared `neverForLocation`. |
| Location | Opening the HUD | The GPS speed. Nothing else uses location. |
| Notifications (Android 13+) | Arming Sentry | Its alarms. Sentry still arms if refused. |

Internet, network state, Wi-Fi state and the foreground service are install-time permissions.

## Settings

The app's own settings are typed keys in `AppSettings`, each with a default and what it accepts; the
Settings screen is drawn from that list, so none can exist without being shown. The camera's settings
come from the camera's own menu, with each value read back from the camera.

## Checking it

| Command | What it does |
| --- | --- |
| `./gradlew :android:core:check` | Lints and tests the logic, at 100% of lines and branches. |
| `./gradlew :android:app:check` | Lints and tests the app under Robolectric, at its floors. |
| `./gradlew :android:app:assembleDebug` | Builds the debug APK. |

Three things about Robolectric cost hours to find, and shape how the screens are written and tested:

- **A text field inside a `Dialog` never lets Compose go idle**, focused or not, so any test that opens
  one times out. Text is edited in place, under its row, instead; that is also kinder to a phone's
  keyboard.
- **`captureToImage` waits on the frame clock for a redraw Robolectric never makes.** To run code that is
  only drawn, never laid out, a test draws the activity's decor view into a bitmap itself.
- **The protocol fixtures' frames and thumbnails only look like JPEGs to the protocol.** The phone's own
  decoder refuses them, so a test that needs a picture drawn uses `TestGraph.picture()`, a real JPEG.

## Not yet proven on a phone

Everything above is tested against the fake camera, which behaves as the reference camera was measured
to. None of it has yet been run on a real phone against the real camera; until it has, and the result is
dated in `docs/ACCEPTANCE.md`, no claim about the phone is made. In particular:

- whether joining by exact name connects with no dialog on the reference phone;
- whether the camera gives thumbnails after a stream has been started and stopped;
- the frame rate and latency of the live picture on a phone;
- Sentry's sensitivity against a real scene.

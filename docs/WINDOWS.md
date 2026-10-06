# Sightline for Windows

The Windows app, its installer and its command line. The phone's counterpart is `docs/ANDROID.md`.

## How it is built

| Project | What it holds |
| --- | --- |
| `Sightline.Core` | Everything that decides: the camera controller, Sentry, the adapter advisor and camera finder, preferences, and every sentence the window says about the camera and its card. No window and no Windows API, so it is tested in full. |
| `Sightline.Platform.Windows` | Windows itself: its Wi-Fi through the Native Wi-Fi API, its addresses, the user's PATH, the single-copy guard, and updating through Velopack. |
| `Sightline.App` | The Avalonia window: a shell, one view model per page, and views that only bind. |
| `Sightline.Cli` | The `sightline` command, over the same core. |

The window's view models are built from `AppParts`, so their tests drive the real controller over the shared fakes in `Sightline.Core.Testing` (a fake camera on a fake network, fake Wi-Fi). The views are tested headless with the REX theme loaded, and pressed as a mouse presses, so a button covered by something on top cannot be pressed in a test either.

## Staying online

The camera is joined on the adapter the person picks, which the connect panel orders so the choice that changes least comes first: an adapter already on the camera, then a free one (the one used last, then a plugged-in one), and only then one that would have to leave a network. An adapter is never taken off a network without the person agreeing to exactly what it will cost, and it is put back on that network when the camera is left, a lost camera and quitting included. Every socket to the camera is sent from that adapter's own address, so nothing else on the PC changes route.

## Running in the background

Closing the window while a camera is connected or Sentry is armed leaves Sightline in the tray, when the person allows it (Settings, on by default); with nothing to keep, closing quits. The tray brings the window back, arms or disarms Sentry, says when an alarm last went up, and is the one place to quit. A second start shows the copy already running rather than fighting it for the camera and the adapter.

## Installing and updating

`tools/scripts/package.ps1` builds every download; `docs/RELEASING.md` says how they are published.

- `Sightline-Setup.exe` installs for one person, with no administrator: the Start menu, an entry in Installed apps with an uninstaller, and the install folder appended to that person's PATH so `sightline` works in any terminal. Uninstalling removes exactly that PATH entry and nothing else.
- An installed copy looks for a newer version when it opens, if the person allows it, downloads it quietly, and installs it with a restart the person chooses, never while the camera records or Sentry is armed. A rolling install follows rolling builds; a tagged install follows tags.
- Installing, updating and uninstalling first ask a running copy to quit, through the same channel a second start uses to show it, so it leaves its camera and puts its adapter back.
- `Sightline-Portable.exe` runs from anywhere and installs nothing; it cannot update itself, so it points to the download page instead.

None of it is code-signed, so Windows warns that the publisher is unknown. That is accurate, and the site and the releases say so.

## Checking it

| Command | What it does |
| --- | --- |
| `dotnet test Sightline.slnx` | Every .NET test: protocol, core, platform, app and views. |
| `./tools/scripts/package.ps1` | Builds the installer, the portable app, the command line and the update feed into `dist/windows`. |

## Not yet proven on this PC

Everything above is tested against fakes. Until each is dated in `docs/ACCEPTANCE.md`, no claim is made about:

- joining the reference camera on the spare adapter from the window, and leaving it;
- an install, an update and an uninstall of the packaged builds on a real Windows account;
- Sentry's sensitivity against a real scene.

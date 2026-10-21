# open-tapo

A small Windows tray app to control your **TP-Link Tapo** lights and cameras **locally**, without the Tapo app and without going through the cloud.

![open-tapo panel](docs/panel.png)

Click the icon next to the clock: a compact dark panel opens above the taskbar, one line per device. Click a line to unfold its settings; click outside to close it.

## Features

**Lights / plugs** (KLAP protocol)
- On / off, brightness
- White temperature presets and slider, colour presets, hue and saturation
- Fade on / off
- Time switched on today, over 7 days and over 30 days, plus Wi-Fi signal

**Cameras** (Tapo secure camera API)
- Privacy mode (lens cover), motion and person detection, auto tracking
- Alarm on detection, siren, status LED, image flip, night vision mode
- Last alert, SD card usage, Wi-Fi signal

**App**
- Automatic discovery on the local network, or add a device by IP
- Account stored in the Windows Credential Locker
- Refreshes every 30 s while the panel is open
- Single self-contained `.exe`, no installer, Windows 11 Fluent dark theme

## Requirements

- Windows 11 (x64)
- Your Tapo account email and password: the devices check them locally before they accept any command
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build and run

```powershell
dotnet publish src/OpenTapo -c Release -o dist
.\dist\OpenTapo.exe
```

`dist\OpenTapo.exe` is self-contained: copy it anywhere and run it. Running it again while it is already open brings the panel up.

Diagnostics: `OpenTapo.exe --probe` lists the devices found on the network and reads each one with the stored account. Errors the app cannot show are written to `%LOCALAPPDATA%\OpenTapo\error.log`.

## Usage notes

- **Sign in** with the email and password of your Tapo account. The app checks them against a device before saving them, so a typo is reported right away.
- **Lights on recent firmware (1.4+)** only accept TP-Link's newer TPAP protocol, which open-tapo does not support. Enable **Third-Party Compatibility** in the Tapo app's settings to turn the local KLAP protocol back on.
- **Cameras lock sign-in for about 30 minutes after 10 failed attempts.** open-tapo checks the password locally before completing a login, and it stops retrying after a rejection or a lockout. Repeated sign-ins with a wrong password still count.
- A device that discovery misses (another subnet, a sleeping Wi-Fi client) can be added by IP with the **+** button.

## Tested devices

| Device | Firmware |
|---|---|
| Tapo L530E (colour bulb) | 1.4.4 |
| Tapo C225 (camera) | 1.3.2 |

Other Tapo bulbs, plugs and cameras that use the same protocols should work, but they are untested. Settings a device does not report are hidden.

## How it works

| Part | Detail |
|---|---|
| Discovery | UDP broadcast and unicast to port 20002 (the Tapo app's discovery packet), plus a TLS certificate check on port 443 (`TPRI-DEVICE`) for cameras that do not answer UDP |
| Lights | KLAP v2 over HTTP: a two-step seed handshake proving both sides know `SHA256(SHA1(email) + SHA1(password))`, then AES-128-CBC requests signed with SHA-256 and a sequence number |
| Cameras | HTTPS login as `admin` with `encrypt_type 3` (client and device nonces, `device_confirm`), then `securePassthrough` AES-128-CBC requests authenticated by a `Tapo_tag` header. The camera checks the tag against the body as Python's `json.dumps` would print it, so the envelope is written in exactly that form |

## Security

- Credentials stay in the per-user Windows Credential Locker and are only sent to devices on your LAN. Nothing goes through the cloud.
- No password-derived value is sent to a device before it proves it knows the password hash.
- Device connections refuse HTTP redirects and cap response sizes, so a rogue host on the LAN cannot aim the app at other services.
- Known limits: the camera's self-signed certificate is accepted without pinning. KLAP exposes material that allows offline password guessing to anyone on the LAN, which is why TP-Link moved to TPAP. Camera responses are not integrity-protected by the protocol.

## Not supported

Live video, pan / tilt control, the TPAP protocol. Hubs and battery cameras are untested.

## Project layout

```
src/OpenTapo/
  Tapo/         protocol clients (KLAP, camera), discovery, camera API definitions
  Services/     credential store, device sessions, account check, manual device list
  ViewModels/   MVVM (CommunityToolkit.Mvvm)
  Views/        tray icon, panel, Fluent styles and device templates
  Diagnostics/  --probe and error log
```

Code style: explicit types everywhere (`var` fails the build, enforced by `.editorconfig`), file-scoped namespaces, a single NuGet dependency pinned by a lock file.

## Disclaimer

open-tapo is an independent project. It is not affiliated with, endorsed by or supported by TP-Link. "Tapo" and "TP-Link" are trademarks of their respective owners. Use at your own risk.

## License

[GPL-3.0](LICENSE)

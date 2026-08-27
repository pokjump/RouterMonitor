# Router Monitor

An application for monitoring an ADB VV5822 router (the "epicentro" admin panel), used among others by Netia. It polls the router's panel over HTTP, parses the returned HTML pages (the panel has no JSON/AJAX API), and shows connection status, Wi-Fi info, and connected devices in one place.

## Why this exists

The ADB VV5822 and its firmware (`VV5822_NETIA_7.6.0.0010`) are simply bad. The admin panel is slow, offers no real API, and the app sometimes fails to connect to the router altogether - not just a slow response, outright connection failures and timeouts. That's why the HTTP layer retries failed requests with backoff instead of giving up after one failed attempt, and why the app is built to keep polling in the background rather than assume a single check is enough.

## How it works

1. To log in, the app fetches the router's login page and pulls out the nonce/codes the panel embeds in it (`AdbLoginPageParser`). The panel's own JavaScript (`login_onsubmit()`) hashes the password together with that nonce before submitting the form; `LoginHash`/`Md5Crypt` reimplement that same hashing in C#, so the app authenticates the way a browser would without ever sending the plaintext password.
2. Once logged in, `RouterHttpClient` fetches the panel's HTML pages (device summary, home network, etc.), retrying failed requests with exponential backoff to work around the router's flaky connection.
3. Each page is parsed with `InfoFieldParser`/`HomenetPageParser` (using HtmlAgilityPack). Since the panel has no structured API, everything - WAN status, Wi-Fi info, connected devices - is read straight out of the rendered HTML labels and values.
4. In the WPF app, `PollingService` runs this login-fetch-parse cycle on a timer in the background, pushes the latest snapshot to the UI (`MainViewModel`), and writes it to a local SQLite database (`HistoryDatabase`) for the transfer chart and for spotting devices that weren't seen before.
5. `PollingService` compares each poll's device list against the previous one; any device that joins or drops off the network triggers a Windows tray notification (`TrayIconService`) - not just devices never seen before. The app starts hidden in the tray (no window is shown at startup) and registers itself to launch at Windows sign-in.
6. The console tool (`RouterMonitor.Console`) skips the UI and history entirely and just runs one login-fetch-print cycle - useful for scripting or a quick manual check.

## What the app does

- Shows WAN/DSL connection status, IP addresses, Wi-Fi (SSID, security) and basic device info.
- Lists devices connected to the home network (name, MAC address, IP address, interface).
- Periodically polls the router and stores download/upload transfer history in a local SQLite database, shown as a chart.
- Notifies via a system tray balloon whenever any device joins or leaves the network (not just the first time an unknown device shows up).
- Starts hidden in the system tray and launches automatically at Windows sign-in.

## Repository layout

- `src/RouterMonitor.Core` - UI-independent logic: HTTP client with retry, login to the panel (reimplements the hashing from `login_onsubmit()`), HTML page parsers, data models.
- `src/RouterMonitor.Wpf` - the desktop app (WPF) with a tray icon, SQLite-backed history, and a transfer chart.
- `src/RouterMonitor.Console` - a simple console tool for a one-off poll of the router that prints the result.

## Requirements

- .NET 10 SDK
- Windows (the WPF app uses Windows Forms for the tray icon)

## Running it

### WPF app

```
dotnet run --project src/RouterMonitor.Wpf
```

On first run the app creates `%AppData%\RouterMonitor\settings.json` with default values (router address, username, password, poll interval - 30 seconds by default, floored at 5 to avoid hammering the router). You need to fill in the login details for **your own** router there - the default values in the code are just the vendor's/ISP's factory defaults, not a universal password.

The app starts hidden in the system tray (no window pops up) and adds itself to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` so it launches automatically at sign-in; use the tray icon's "Pokaż" entry to open the window, and "Zakończ" to exit (which also removes the app from the tray, but not from the Run key - delete the `RouterMonitor` value there if you want to stop it from launching at startup).

### Console tool

```
dotnet run --project src/RouterMonitor.Console
```

Login details can be provided via environment variables: `ROUTER_BASEURL`, `ROUTER_USERNAME`, `ROUTER_PASSWORD`. The `--dump` flag saves the router's raw HTML responses to a `dumps` directory (useful when adding support for a different firmware).

## Compatibility

The parsers are tailored to a specific ADB VV5822 firmware version (`VV5822_NETIA_7.6.0.0010`). Other models/firmware versions may have a different page layout and require adjusting the parsers in `RouterMonitor.Core.Providers`.

## License

This project is released under the MIT License - see [LICENSE](LICENSE).

# HealthChecker

HealthChecker is a desktop app for Windows 10/11 built on **.NET 10 + WPF**.
It monitors domains and IP addresses every second, keeps short-term availability/ping charts,
stores state/history in `%AppData%`, supports tray mode/autostart, and includes an integrated
WinMTR-style traceroute view.

## Key capabilities

- Add monitoring targets as:
  - domain (`example.com`)
  - URL host (`https://example.com/path`)
  - IPv4/IPv6 address
- 1-second background probing for each target.
- Automatic domain-to-IP resolution.
- Per-target status in compact/expanded card format.
- Last-60-probes infographics:
  - availability bars
  - ping trend bars
  - value tooltips on hover
- WinMTR-inspired traceroute screen with per-hop metrics:
  - `Hostname`, `Nr`, `Loss %`, `Sent`, `Recv`, `Best`, `Avg`, `Worst`, `Last`
- System tray integration:
  - minimize to tray
  - tray context menu (`Open`, `Pause/Resume`, `Probe Now`, `Exit`)
- Autostart support (`--tray`) via Windows Run registry key.
- Persisted app state at `%AppData%\\HealthChecker\\state.json`.

## Technology stack

- .NET SDK: `10.0.x`
- UI: WPF
- Pattern: MVVM (lightweight, no external MVVM framework)
- Networking:
  - `System.Net.NetworkInformation.Ping`
  - DNS resolution via `System.Net.Dns`
- Persistence: JSON (`System.Text.Json`)
- Windows integration:
  - `NotifyIcon` (Windows Forms interop)
  - `Microsoft.Win32.Registry` for startup registration

## Repository layout

```text
.
+- HealthChecker.sln
+- README.md
+- LICENSE
+- THIRD_PARTY_NOTICES.md
+- .gitignore
L- HealthChecker/
   +- HealthChecker.csproj
   +- MainWindow.xaml
   +- MainWindow.xaml.cs
   +- App.xaml
   +- App.xaml.cs
   +- Models/
   +- ViewModels/
   +- Services/
   L- Converters/
```

## Requirements

- Windows 10 or Windows 11
- .NET 10 SDK installed
- ICMP permissions available in local environment/network policy (required for ping/traceroute)

## Quick start

### 1) Clone

```powershell
git clone <your-repo-url>
cd HealthChecker
```

### 2) Build

```powershell
dotnet build .\HealthChecker.sln
```

### 3) Run (normal mode)

```powershell
dotnet run --project .\HealthChecker\HealthChecker.csproj
```

### 4) Run directly in tray mode

```powershell
dotnet run --project .\HealthChecker\HealthChecker.csproj -- --tray
```

## Publish (Release)

### Recommended: self-contained single-file assets (for GitHub Releases)

Use the repository script to generate runnable standalone `.exe` files:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release-assets.ps1 -Version v1.0.0
```

Output:

```text
artifacts\v1.0.0\
  HealthChecker-v1.0.0-debug.exe
  HealthChecker-v1.0.0-release.exe
  HealthChecker-v1.0.0-sources.zip
```

This avoids the common issue where only `HealthChecker.exe` is uploaded without its runtime dependencies.

### Framework-dependent publish (dev/internal usage)

```powershell
dotnet publish .\HealthChecker\HealthChecker.csproj -c Release -r win-x64 --self-contained false
```

Artifacts are generated under:

```text
HealthChecker\bin\Release\net10.0-windows\win-x64\publish\
```

### Optional self-contained publish

```powershell
dotnet publish .\HealthChecker\HealthChecker.csproj -c Release -r win-x64 --self-contained true
```

## How monitoring works

- A 1-second dispatcher timer triggers a monitoring cycle.
- Each cycle probes all targets asynchronously.
- Every target stores rolling samples (used for uptime and charts).
- State is saved periodically and on shutdown to `%AppData%\\HealthChecker\\state.json`.

## Traceroute mode

- In an expanded target card click `Tracert`.
- App switches to internal traceroute view (with `Back` and `Stop`).
- A probe loop is run per TTL (up to 30 by default) with 1-second interval.
- Per-hop stats are aggregated continuously in table rows.
- When destination is reached, max active hop range is reduced dynamically.

## Startup and tray behavior

- `Start with Windows (launch in tray)` toggles registry value:
  - `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run`
- App registers executable path with `--tray` argument.
- Closing the main window sends app to tray; use tray menu `Exit` for full shutdown.

## Data and privacy

- HealthChecker does not send telemetry to external servers.
- Stored local data includes target list and probe history required for UI stats.
- Storage location: `%AppData%\\HealthChecker\\state.json`.

## Known limitations

- Current traceroute table is optimized for live stats, not for long-session export yet.
- No built-in CSV/HTML export at this stage.
- Geographic mapping/ASN enrichment is not included yet.

## Troubleshooting

### Build fails because `HealthChecker.exe` is in use

Stop the running process and build again:

```powershell
Get-Process HealthChecker -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\HealthChecker.sln
```

### No ping responses

- Verify firewall policy and ICMP allowance.
- Check local network restrictions (corporate VPN/policies can block ICMP).

## Development notes

- Keep UI changes in `MainWindow.xaml` and interaction logic in `MainViewModel`.
- Network logic belongs in `Services/`.
- Avoid committing `bin/` and `obj/` artifacts (already covered by `.gitignore`).

## Public repository checklist

Before your first push, update placeholders:

1. Replace `<your-repo-url>` in this README.
2. Update license copyright owner if needed.
3. Add screenshots under `docs/screenshots` and link them here.
4. Optionally configure GitHub Actions for CI (`dotnet build` on PR).

## Credits

Traceroute UX and metric model were inspired by WinMTR behavior.
See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for details.

## License

This project is licensed under the MIT License.
See [LICENSE](LICENSE).

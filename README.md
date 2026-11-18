# PerfWidget

An always-on-top widget for Windows, macOS and Linux that answers "what is hammering my machine?" at the level of **apps**, not processes.

![widget](screenshot.png) ![alert](screenshot-alert.png)

## Platforms

| | Windows | macOS | Linux |
|---|---|---|---|
| App | WPF build (`src/PerfWidget.Wpf`) | `PerfWidget.app`, menu-bar icon, no Dock icon (`src/PerfWidget.App`) | `PerfWidget` binary, tray icon where the desktop has one (`src/PerfWidget.App`) |
| Per-app RAM | private working set | memory footprint (Activity Monitor's "Memory") | private RSS (+ swap) |
| Per-app I/O | all I/O incl. network/pipes | real disk I/O | real disk I/O (own processes) |
| Per-app GPU | yes | no (macOS doesn't expose it) | no |
| Memory alerts | RAM %, commit, page-file swapping | kernel memory-pressure level, swapping | RAM %, PSI memory stalls, swapping |
| CPU heat | temp via LibreHardwareMonitor; firmware throttling | thermal state (throttling / critical) | hwmon temperature (k10temp, coretemp…) |
| GPU details | NVIDIA via NVML | utilization + GPU memory (IOKit) | NVIDIA via NVML, AMD via sysfs |
| Data folder | `%APPDATA%\PerfWidget` | `~/Library/Application Support/PerfWidget` | `~/.local/share/PerfWidget` |

On macOS, apps running as root or as another user (kernel_task, WindowServer, Spotlight) are listed with zero CPU and RAM unless PerfWidget runs as root. Machine totals are always right.

Layout: `src/PerfWidget.Core` is shared and holds the collector, grouping, alerts, history and live feed. Under it, `Windows/`, `Mac/` and `Linux/` hold the OS samplers behind `IPlatform`.

## Reading it

- **Verdict** (top): one short title plus one line of detail.
  - Green = all clear, amber = warning, red = critical.
  - Up to two more alerts are listed underneath.
  - Alerts use a 5-second average, so brief spikes don't fire them.
- **Five resource lines**: bar with a 60-second sparkline, then %, then the biggest user. Hover any line for the full detail (clocks, temperatures, top 4 apps, page file…).

  | Line | Bar shows | Next to it |
  |------|-----------|------------|
  | CPU  | share of all 32 threads | top app, CPU temp if known; amber "X maxing 1 core" if an app is single-thread bound |
  | RAM  | physical memory in use | biggest app |
  | SWAP | commit (RAM + page file reserved) | page file used, or red "swapping N MB/s" when RAM is full and Windows is paging |
  | GPU  | busiest engine | GPU temperature, top app |
  | DISK | busiest physical disk's active time | that disk and its throughput |

- **Top apps**: one number per app, ranked by whatever is under pressure (CPU when CPU is the problem, RAM otherwise…).
  - Click the ranking label to cycle through CPU / RAM / GPU / I/O / auto.
  - Click an app to see its processes, each with a short description from its command line (`renderer`, `server.js`, `--type=gpu-process`…).
  - Hover a process for its full command line.
- **Free space** per drive at the bottom.

## What it detects

- CPU maxed, RAM full, swapping, virtual memory exhaustion, GPU maxed, VRAM full, disk saturated, low disk space (critical when it's the page-file drive).
- **One app stuck on a single core**: one thread pinned at ≥90% of a core for 5 s. Total CPU looks low in this case, so Task Manager hides it.
- **CPU throttling**: firmware capping clocks (thermal or power), or running below base clock under load.
- **CPU temperature**: ≥ 89°C. Needs LibreHardwareMonitor running (see below).
- **GPU throttling**: NVIDIA thermal / hardware slowdown / power-brake reasons. The routine "power limit" and "reliability" limits are ignored.
- **GPU hot**: within 3°C of its slowdown temperature.
- **Memory creep**: an app that grew ≥ 1.5 GB over 30–60 min and is still at its peak (leak or runaway cache).

## History

- Every 10 s a JSON line goes to `%APPDATA%\PerfWidget\history\yyyy-MM-dd.jsonl` (kept 7 days, ~11 MB/day).
- Each line holds averages and peaks, the apps that mattered in that window, and any alerts.
- Right-click → **What happened in the last hour / 24 hours** opens a digest:
  - peaks with culprits
  - alerts with time ranges
  - memory growth per app
  - average CPU per app
  - lowest CPU clock under load
- Same digest from the command line (this is what Claude can run when you ask what's been slowing the machine):

  ```bash
  PerfWidget.exe --history 60 report.txt
  ```

  `PerfWidget.exe --dump now.txt` writes the live snapshot (every app and its processes) instead.

## Live feed for other tools

- While the widget runs, it rewrites `%APPDATA%\PerfWidget\live.json` every 2 s, replacing the whole file at once so readers never see a half-written file. Orbit reads this file.
- `PerfWidget.exe --json now.json` writes one snapshot in the same format without the widget (takes ~6 s).
- Schema `v: 1`:
  - `t` (UTC), `intervalSec` and `exe` (the widget's path, for running `--history`). Treat the feed as stale once `t` is more than ~10 s old, which means the widget is closed.
  - `verdict` {severity 0–3, title, detail, bottleneck} and `issues[]` {severity, resource, title, detail}.
  - `cpu` {pct, mhz, baseMhz, perfLimitPct, tempC, processes, threads}.
  - `memory` {totalBytes, usedBytes, pct, commitBytes, commitLimitBytes, pagefile…, pagingInBps}.
  - `gpu` {pct, vram…, tempC, powerW, powerLimitW, clockMhz, throttle}.
  - `disks[]` {name, busyPct, bps} and `drives[]` {name, freeBytes, totalBytes, hasPagefile}.
  - `apps[]`: up to 20 apps that matter (top by RAM, CPU, GPU and I/O). Each has {name, processes, cpuPct, maxThreadPct, ramBytes, commitBytes, gpuPct, vramBytes, ioBps}.
  - Within a version, fields are only ever added, never renamed or removed.

## Hidden sampler (what Orbit runs)

- `perfwidget-sampler` is the same collector with no window: one self-contained file per OS (~36 MB), nothing to install.
- `perfwidget-sampler --feed --parent-pid <pid>` keeps `live.json` and history current exactly like the widget, and exits when that process exits. Orbit starts it with its own pid, so every machine Orbit runs on gets the feed with no widget open.
- If the widget is open, the sampler sees a fresh feed from another live process and goes idle (~0% CPU) until the widget closes. The feed's `writer` field says which one wrote it.
- It also takes `--json`, `--dump`, `--history` and `--version`. Measured on Windows: ~0.15% CPU and ~130 MB while feeding.
- Releases: pushing a `sampler-vX.Y.Z` tag builds and tests it on all three OSes, then publishes `perfwidget-sampler-<rid>` archives with a `SHA256SUMS` file. Orbit pins the version and the sums.
- How Orbit gets it: installed Orbit builds carry the sampler inside the installer (the build step fetches the pinned asset and checks its hash), so testers need no download at all. Orbit run from a source checkout downloads the same pinned asset from this repo's public release page and checks its hash.

## Temperatures

- **GPU** (temperature, power, clocks, fan, throttle reasons): read from NVIDIA's NVML, which ships with the driver. No setup needed.
- **CPU**: Windows has no driver-free way to read Ryzen temperatures. Install and run [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (it needs admin for its sensor driver). The widget picks up its sensors automatically within a minute.
- **CPU clock speed and firmware throttling** work without it.

## Grouping rules

- A process belongs to the app that launched it, walking up the parent chain.
- The walk stops at OS launchers (Explorer, services, svchost, terminal windows, interactive shells).
- A known app (Chrome, Blender, Unreal…) launched from another app counts as its own app.
- Generic runtimes are named after what they run:
  - `...\Orbit\node_modules\electron\electron.exe` → **Orbit**
  - `node ...\server.js` → **server.js (node)**
  - `python train.py` → **train.py (python)**
- On macOS, everything inside one `.app` bundle is one app. XPC helpers launched by launchd join the app responsible for them.
- Aliases live in each sampler's rules: `Windows/WindowsPlatform.cs → BuildRules()`, `Mac/MacPlatform.cs` and `Linux/LinuxRules.cs`. Add a line to rename or merge an app.

## Notes

- RAM per app = private working set (matches Task Manager's Memory column). Commit is in the tooltip.
- Per-app I/O includes file, network and pipe traffic, so it can exceed a disk's own rate. True per-app disk I/O needs admin ETW tracing.
- Overhead: ~0.12% CPU, ~140 MB RAM (mostly the WPF runtime). No admin rights needed.

## Use

- Drag it by the top half.
- Right-click for:
  - history digests
  - compact mode, opacity, always-on-top
  - start with Windows
  - number of apps shown
  - Task Manager / Resource Monitor, exit
- `–` hides it to the tray. The tray icon is coloured by status; click it to bring the widget back.

## Build

Windows (WPF):

```bash
dotnet publish src/PerfWidget.Wpf/PerfWidget.Wpf.csproj -c Release -o dist -p:PublishSingleFile=true -p:DebugType=none
```

macOS / Linux, self-contained (no .NET install needed). Use `osx-arm64`, `osx-x64` or `linux-x64`:

```bash
dotnet publish src/PerfWidget.App/PerfWidget.App.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out
```

Then `scripts/make-mac-app.sh out PerfWidget.zip` wraps it as `PerfWidget.app`. It's ad-hoc signed, not notarized.

CI (`.github/workflows/ci.yml`) builds on Windows, macOS and Ubuntu. On each it starts a single-core CPU hog and takes a live `--json` snapshot, then `scripts/check-snapshot.ps1` checks the numbers are real and the hog is found. It also packages all four builds.

Settings and errors live in the data folder above, as `settings.json` and `error.log`.

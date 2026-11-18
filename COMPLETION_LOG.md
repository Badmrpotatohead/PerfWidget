# Completion Log — PerfWidget
Append entries here after every prompt ships. See CLAUDE.md for format.
---

## PROMPT #1 — App-level performance monitor widget
**Status:** ✅ Shipped
**Date:** 2026-11-16
**Commit:** 5e4460c
**Files changed:** PerfWidget.csproj, app.manifest, Native.cs, Collector.cs, App.xaml, App.xaml.cs, MainWindow.xaml, MainWindow.xaml.cs, README.md, .gitignore, screenshot*.png
**Notes:**
- WPF (.NET 10) always-on-top widget with a tray icon. Processes are rolled up into apps by process tree. A headline names the bottleneck plus the apps responsible.
- Verified against Windows' own counters: RAM free, commit, page file and CPU all match within sampling noise.
- Verified by test: row expansion, plus a 12 s synthetic 32-thread CPU load that raised a red "CPU maxed" headline with correct blame and switched auto-sort to CPU.
- Overhead measured at ~0.15% CPU, ~120 MB RAM.
- Per-app I/O comes from process I/O counters, so it includes network and pipe traffic (true per-app disk I/O needs admin ETW). It is labelled "most I/O" in the UI.
- Swap, RAM, VRAM and low-disk alerts are implemented, but this machine had no such pressure, so they were not triggered live.
- "Start with Windows" is a right-click menu toggle (HKCU Run key). Not enabled automatically.
- Local git repo only; no remote, so no PR/merge.

## PROMPT #2 — Single-core detection, command lines, temps/throttling, history, decluttered layout
**Status:** ✅ Shipped
**Date:** 2026-11-16
**Commit:** afeb7a3
**Files changed:** Collector.cs, Native.cs, Sensors.cs (new), History.cs (new), MainWindow.xaml, MainWindow.xaml.cs, App.xaml.cs, PerfWidget.csproj, README.md, screenshot*.png
**Notes:**
- Per-thread CPU from the same NtQuerySystemInformation snapshot. Verified: one pinned thread (28% total CPU) raised "Claude is maxing one core" within ~6 s.
- Command lines read via NtQueryInformationProcess (no admin). Script-based naming verified ("server.js (node)").
- CPU clock and firmware limit via PDH (4.49 GHz vs 3.30 base observed).
- GPU temp, power, clocks and throttle reasons via NVML (verified 41°C / 27 W). NVIDIA's "Reliability" flag (0x400) is deliberately ignored.
- CPU temperature: this machine has no driver-free source (no ACPI thermal zones, no HW monitor). Reader for LibreHardwareMonitor WMI added but UNTESTED, since LHM isn't installed. Installing it needs the user's OK.
- History: 10 s JSONL lines, 7-day retention. Verified the --history digest reconstructs a synthetic CPU burst, its culprit, the minimum clock and a single-core event.
- Memory-creep alert needs 30+ min of runtime to arm; not triggered live.
- Layout redesigned after "wall of data" feedback: title + detail alerts, one line per resource, one number per app, details in tooltips. Settings "Rows" renamed to "AppRows" (default 5).
- Overhead re-measured: ~0.12% CPU, ~140 MB private RAM.

## PROMPT #3 — Live JSON feed for Orbit + Orbit Machine Health prompt
**Status:** ✅ Shipped
**Date:** 2026-11-16
**Commit:** 188ac50, 96079ef
**Files changed:** LiveFeed.cs (new), App.xaml.cs, MainWindow.xaml.cs, README.md
**Notes:**
- The widget now rewrites %APPDATA%\PerfWidget\live.json every 2 s, replacing the whole file at once. Schema v1 covers the verdict, issues, cpu, memory, gpu, disks, drives, the top 20 apps, and the exe path.
- New `--json <file>` one-shot mode. Verified on this machine: real values, refreshes every 2 s.
- The Orbit side was queued through Orbit's own queue as orbit #8543, on the Windows node, because the default node is the Mac, where the feed doesn't exist. It covers a reader module, /api/machine-health, the orbit_machine_health MCP tool and a Diagnostics "Machine" panel. It is optional and does nothing when PerfWidget is absent. As of logging it is pending Orbit's AI review and approval; not built yet.

## PROMPT #4 — macOS and Linux versions
**Status:** ⚠️ Partial (built; not yet run on a real Mac or Linux machine)
**Date:** 2026-11-16
**Commit:** 0e792cd, 7e64d13, a788888, f10d901
**Files changed:** src/PerfWidget.Core/* (new shared core + Windows/Mac/Linux samplers), src/PerfWidget.Wpf/* (moved), src/PerfWidget.App/* (new Avalonia app), .github/workflows/ci.yml, scripts/check-snapshot.ps1, scripts/make-mac-app.sh, .gitattributes, README.md
**Notes:**
- Split into a shared Core (collector, grouping, alerts, history, live feed) plus an IPlatform sampler per OS.
- Windows behaviour is unchanged. Checked by a dump, a screenshot, and the live feed after the refactor.
- The macOS and Linux samplers were written by two Opus subagents. Both build cleanly. They were checked on Windows only:
  - macOS: the self-test (argument parser, bundle-name parser, struct sizes and offsets) passes, and grouping was simulated.
  - Linux: 128 harness checks pass, against a fake /proc and /sys plus the parsers.
- The Avalonia UI was verified on Windows. It matches the WPF layout at about 0.3% CPU.
- CI (Windows/macOS/Ubuntu live sample with a CPU hog, plus packaging) is written, and its checker passes locally on Windows. It has NOT run: creating the private GitHub repo was blocked by the permission classifier, so it is waiting for the user's OK.
- macOS limits: no per-app GPU; no CPU temperature or clock on Apple Silicon; processes run by root or other users show zero CPU and RAM without root.
- Linux could not be tested locally, because virtualization is disabled in this PC's firmware so WSL won't start.
- Orbit: an amendment to the running #8543 was not delivered (agent disconnected). Queued follow-up orbit #8544 (depends on #8543) so Orbit reads the macOS and Linux feed paths and fields. It is pending review and approval.

## PROMPT #5 — GitHub repo + CI on real macOS/Linux, Mac grouping fix
**Status:** ✅ Shipped
**Date:** 2026-11-17
**Commit:** 9744389
**Files changed:** src/PerfWidget.Core/Collector.cs, src/PerfWidget.Core/Fmt.cs, scripts/check-snapshot.ps1, .github/workflows/ci.yml
**Notes:**
- Follows up #4. With the user's OK, created the private repo Badmrpotatohead/PerfWidget and pushed. The first CI run passed on windows-latest, macos-latest (Apple Silicon) and ubuntu-latest, and all four packages built.
- That real-runner run showed a macOS bug. The Python hog was folded into the runner's `hosted-compute-agent` daemon, because the Mac sampler followed launchd's "responsible process" link to a non-app. It now only follows that link to a real .app that isn't a launcher.
- CI now also checks that the hog is listed under its own name. Merged via PR #1 after CI passed on all three OSes.
- Report text now prints n/a for unknown clocks and "shared with RAM" for unified GPU memory. CI actions moved to v5 (Node 20 deprecation).
- Orbit #8543 and #8544 both shipped to Orbit's master (macOS and Linux feed paths included). They take effect at Orbit's next restart, which was not done here.
- Still not run on the user's own Mac. The .app is ad-hoc signed, not notarized, so Gatekeeper needs a right-click → Open the first time.

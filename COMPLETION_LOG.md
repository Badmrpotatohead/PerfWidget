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

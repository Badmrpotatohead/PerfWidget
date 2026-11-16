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

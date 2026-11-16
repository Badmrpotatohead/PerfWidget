#!/usr/bin/env bash
# Wraps a `dotnet publish` output folder into PerfWidget.app and zips it.
# usage: make-mac-app.sh <publish-dir> <out.zip>
set -euo pipefail
src="$1"; zip="$2"
app="PerfWidget.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$src"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/PerfWidget"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>PerfWidget</string>
  <key>CFBundleDisplayName</key><string>PerfWidget</string>
  <key>CFBundleIdentifier</key><string>com.sivants.perfwidget</string>
  <key>CFBundleExecutable</key><string>PerfWidget</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.1.0</string>
  <key>CFBundleVersion</key><string>1.1.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <!-- menu-bar utility: no Dock icon -->
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
# ad-hoc signature so Apple Silicon will launch it (not notarized)
codesign --force --deep --sign - "$app" || true
ditto -c -k --keepParent "$app" "$zip"
echo "built $zip"

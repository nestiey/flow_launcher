#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
out="${1:-$PWD/package}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
app="$stage/root/Applications/FLOW Launcher.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$stage/scripts"
for arch in arm64 x64; do
  dotnet publish FlowMac.csproj -c Release -r "osx-$arch" --self-contained true -o "$app/Contents/Resources/osx-$arch"
  chmod +x "$app/Contents/Resources/osx-$arch/flow-mac"
done
for arch in arm64 x86_64; do
  xcrun swiftc Launcher.swift -target "$arch-apple-macos13.0" -o "$stage/flow-$arch"
done
xcrun lipo -create "$stage/flow-arm64" "$stage/flow-x86_64" -output "$app/Contents/MacOS/FLOW Launcher"
cp Hero.png "$app/Contents/Resources/"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>kr.flowsv.launcher</string>
<key>CFBundleExecutable</key><string>FLOW Launcher</string>
<key>CFBundleName</key><string>FLOW Launcher</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
</dict></plist>
PLIST
cat > "$stage/scripts/postinstall" <<'SCRIPT'
#!/bin/sh
set -eu
[ "$3" = "/" ] || exit 0
user="$(/usr/bin/stat -f '%Su' /dev/console)"
case "$user" in root|loginwindow|'') exit 0 ;; esac
home="$(/usr/bin/dscl . -read "/Users/$user" NFSHomeDirectory | /usr/bin/sed 's/^NFSHomeDirectory: //')"
if [ -d "$home/Desktop" ]; then
  /usr/bin/sudo -u "$user" /bin/ln -s "/Applications/FLOW Launcher.app" "$home/Desktop/FLOW Launcher" 2>/dev/null || true
fi
SCRIPT
chmod +x "$stage/scripts/postinstall"
pkgbuild --root "$stage/root" --scripts "$stage/scripts" --identifier kr.flowsv.launcher --version 1.0 --install-location / "$out/FLOW-Launcher-macOS.pkg"

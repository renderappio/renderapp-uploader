#!/usr/bin/env bash
# Builds the download for one platform into dist/.
#   build/package.sh <runtime id> [git ref name]
# A ref of v1.2.3 versions the build 1.2.3; anything else builds 0.0.0-dev.
set -euo pipefail

rid=$1
ref=${2:-}
version=0.0.0-dev
if [[ $ref == v[0-9]* ]]; then version=${ref#v}; fi

root=$(cd "$(dirname "$0")/.." && pwd)
project=$root/src/Renderapp.Uploader
out=$root/out/$rid
dist=$root/dist
name=renderapp-uploader-$version-$rid
rm -rf "$out" && mkdir -p "$dist"

publish() {
  dotnet publish "$project" -c Release -r "$rid" --self-contained -p:Version="$version" -o "$out" "$@"
}

case $rid in
  win-*)
    publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
    pwsh -NoProfile -Command "Compress-Archive -Path '$out/*' -DestinationPath '$dist/$name.zip' -Force"
    ;;
  linux-*)
    publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
    tar -czf "$dist/$name.tar.gz" -C "$out" .
    ;;
  osx-*)
    # Not single-file: that unpacks its native libraries at run time, unsigned, and Apple
    # silicon refuses to load unsigned code. As a bundle, everything is signed in place.
    publish -p:DebugType=none
    app=$root/out/$rid-app/Renderapp Uploader.app
    rm -rf "$root/out/$rid-app" && mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$out/." "$app/Contents/MacOS/"

    iconset=$root/out/Logo.iconset
    rm -rf "$iconset" && mkdir -p "$iconset"
    sips -s format png "$project/Resources/Logo.ico" --out "$root/out/Logo.png" >/dev/null
    for size in 16 32 128 256; do
      sips -z $size $size "$root/out/Logo.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
      sips -z $((size * 2)) $((size * 2)) "$root/out/Logo.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "$iconset" -o "$app/Contents/Resources/Logo.icns"

    cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Renderapp Uploader</string>
  <key>CFBundleDisplayName</key><string>Renderapp Uploader</string>
  <key>CFBundleIdentifier</key><string>io.renderapp.uploader</string>
  <key>CFBundleExecutable</key><string>renderapp-uploader</string>
  <key>CFBundleIconFile</key><string>Logo.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${version%%-*}</string>
  <key>CFBundleVersion</key><string>${version%%-*}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

    # An ad-hoc signature: enough for Apple silicon to run it, not a Developer ID.
    codesign --force --deep --sign - "$app"
    ditto -c -k --keepParent "$app" "$dist/$name.zip"
    ;;
  *)
    echo "No packaging for $rid" >&2
    exit 1
    ;;
esac

ls -l "$dist"

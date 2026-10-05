#!/bin/sh
# Builds Clawd.app (Apple silicon) next to this script.
#
#   ./build.sh               build and sign Clawd.app
#   ./build.sh --notarize    also notarize with Apple and staple the ticket
#   ./build.sh --zip         also package dist/<APP_NAME>-<VERSION>.zip
#   ./build.sh --dmg         also package dist/<APP_NAME>-<VERSION>-macOS.dmg for a release,
#                            signed, and notarized and stapled with --notarize
#   ./build.sh --install     also copy the app to /Applications and launch it
#
# Flags can be combined. Signing uses a "Developer ID Application" certificate from the
# keychain when one exists (hardened runtime), otherwise an ad-hoc signature for local use.
#
# Environment:
#   CLAWD_SIGN_IDENTITY    signing identity to use instead of the auto-detected one
#                          ("-" forces ad-hoc)
#   CLAWD_NOTARY_PROFILE   notarytool keychain profile for --notarize (default: clawd-notary)
set -eu

# ---------------------------------------------------------------------------------------------
# Product settings
# ---------------------------------------------------------------------------------------------
APP_NAME="Clawd"                                   # bundle name and display name
BUNDLE_ID="com.joonseo1227.clawd-for-orca"
VERSION_FILE="VERSION"                                # CFBundleShortVersionString and CFBundleVersion, e.g. 1.0.0
MIN_MACOS="15.0"
COPYRIGHT="© 2026 joonseo1227"
CATEGORY="public.app-category.developer-tools"
SWIFT_PRODUCT="Clawd"                              # executable product in Package.swift
ICON_NAME="Clawd"                                  # Icon/<ICON_NAME>.icon
STRINGS="Resources/Localizable.xcstrings"          # String Catalog, English source with Korean
BRIDGE_SCRIPT="../Bridge/clawd-bridge.js"
UPDATE_FEED="https://raw.githubusercontent.com/joonseo1227/clawd-for-orca/main/macOS/appcast.xml"
UPDATE_PUBLIC_KEY="ALKjd+64XLds76DBuKJMiGI5mYA+eE53DGaNRPoe9XM="  # Sparkle EdDSA key; Scripts/appcast.py signs with its private half
NOTARY_PROFILE="${CLAWD_NOTARY_PROFILE:-clawd-notary}"
# ---------------------------------------------------------------------------------------------

cd "$(dirname "$0")"

step() { printf '==> %s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

usage() {
  sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
}

NOTARIZE=0
PACKAGE=0
DMG=0
INSTALL=0
for arg in "$@"; do
  case "$arg" in
    --notarize) NOTARIZE=1 ;;
    --zip | --package) PACKAGE=1 ;;
    --dmg) DMG=1 ;;
    --install) INSTALL=1 ;;
    -h | --help) usage; exit 0 ;;
    *) usage >&2; die "unknown option: $arg" ;;
  esac
done

[ -f "$VERSION_FILE" ] || die "missing $VERSION_FILE (the version file at the repository root)"
VERSION=$(tr -d ' \t\r\n' < "$VERSION_FILE")
[ -n "$VERSION" ] || die "$VERSION_FILE is empty"
# The version doubles as the build number, which Sparkle compares to find newer releases, so it
# goes up with every release whatever happens to the git history (a commit count drops when the
# history is squashed, and is always 1 in CI's shallow clone).
case "$VERSION" in
  *[!0-9.]* | .* | *. | *..*) die "$VERSION_FILE must be numbers and dots, such as 1.0.1 (it is also CFBundleVersion)" ;;
esac
APP="$APP_NAME.app"

command -v swift >/dev/null 2>&1 || die "swift not found; install Xcode 26 or later"
xcrun --find actool >/dev/null 2>&1 || die "actool not found; install Xcode (not just the Command Line Tools)"
xcrun --find xcstringstool >/dev/null 2>&1 || die "xcstringstool not found; install Xcode 15 or later"

WORK=$(mktemp -d "${TMPDIR:-/tmp}/clawd-build.XXXXXX")
trap 'rm -rf "$WORK"' EXIT INT TERM

# --- Compile ---------------------------------------------------------------------------------
# Apple silicon only: Xcode 27 deprecates x86_64 for current macOS targets.
step "Building $APP_NAME $VERSION for arm64"
swift build -c release --arch arm64
BIN="$(swift build -c release --arch arm64 --show-bin-path)/$SWIFT_PRODUCT"
[ -x "$BIN" ] || die "build produced no executable at $BIN"
SPARKLE="$(dirname "$BIN")/Sparkle.framework"
[ -d "$SPARKLE" ] || die "build produced no Sparkle.framework next to $BIN"
# Bare binary for development and the --orca debug modes; it loads Sparkle from the build folder.
cp "$BIN" clawd
install_name_tool -add_rpath "$(dirname "$BIN")" clawd 2>/dev/null || die "could not add an rpath to clawd"
codesign --force --sign - clawd

# --- Assemble the bundle ---------------------------------------------------------------------
# Start from an empty bundle every time: overwriting a signed binary in place leaves a stale
# code signature cache, and macOS kills the process on launch.
step "Assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Frameworks"
cp "$BIN" "$APP/Contents/MacOS/$APP_NAME"
install_name_tool -add_rpath "@executable_path/../Frameworks" "$APP/Contents/MacOS/$APP_NAME" 2>/dev/null \
  || die "could not add the Frameworks rpath to $APP_NAME"
cp "$BRIDGE_SCRIPT" "$APP/Contents/Resources/"

# Sparkle without its XPC services, which only sandboxed apps need; Clawd isn't sandboxed.
FRAMEWORK="$APP/Contents/Frameworks/Sparkle.framework"
ditto "$SPARKLE" "$FRAMEWORK"
rm -rf "$FRAMEWORK/XPCServices" "$FRAMEWORK/Versions/Current/XPCServices"

# SwiftPM doesn't compile String Catalogs into an app bundle the way Xcode does: build the
# en.lproj / ko.lproj tables (.strings, .stringsdict for plurals) that Bundle.main reads.
xcrun xcstringstool compile "$STRINGS" --output-directory "$APP/Contents/Resources"
[ -d "$APP/Contents/Resources/ko.lproj" ] || die "the string catalog produced no Korean strings"

# Icon Composer icon: Assets.car for macOS 26+ (Liquid Glass), <ICON_NAME>.icns for older systems.
xcrun actool "Icon/$ICON_NAME.icon" --compile "$APP/Contents/Resources" --platform macosx \
  --minimum-deployment-target "$MIN_MACOS" --app-icon "$ICON_NAME" \
  --output-partial-info-plist "$WORK/icon-partial.plist" >/dev/null

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>$APP_NAME</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string><string>ko</string></array>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleIconFile</key><string>$ICON_NAME</string>
  <key>CFBundleIconName</key><string>$ICON_NAME</string>
  <key>LSMinimumSystemVersion</key><string>$MIN_MACOS</string>
  <key>LSApplicationCategoryType</key><string>$CATEGORY</string>
  <key>LSUIElement</key><true/>
  <key>NSHumanReadableCopyright</key><string>$COPYRIGHT</string>
  <key>SUFeedURL</key><string>$UPDATE_FEED</string>
  <key>SUPublicEDKey</key><string>$UPDATE_PUBLIC_KEY</string>
  <key>SUEnableAutomaticChecks</key><true/>
  <key>SUVerifyUpdateBeforeExtraction</key><true/>
  <key>SURequireSignedFeed</key><true/>
</dict></plist>
PLIST
plutil -lint -s "$APP/Contents/Info.plist" || die "generated Info.plist is invalid"

# --- Sign ------------------------------------------------------------------------------------
IDENTITY="${CLAWD_SIGN_IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null \
  | sed -n 's/.*"\(Developer ID Application[^"]*\)".*/\1/p' | head -1)}"
if [ -n "$IDENTITY" ] && [ "$IDENTITY" != "-" ]; then
  step "Signing with $IDENTITY (hardened runtime)"
  sign() { codesign --force --options runtime --timestamp --sign "$IDENTITY" "$@"; }
else
  [ "$IDENTITY" = "-" ] && reason="CLAWD_SIGN_IDENTITY=-" || reason="no Developer ID Application certificate found"
  IDENTITY=""
  step "Signing ad-hoc ($reason)"
  sign() { codesign --force --sign - "$@"; }
fi
# Inside out and without --deep, as Sparkle documents: its helpers, the framework, then the app.
sign "$FRAMEWORK/Versions/Current/Autoupdate"
sign "$FRAMEWORK/Versions/Current/Updater.app"
sign "$FRAMEWORK"
sign "$APP"
codesign --verify --strict --deep "$APP"

# --- Notarize --------------------------------------------------------------------------------
if [ "$NOTARIZE" = 1 ]; then
  [ -n "$IDENTITY" ] || die "--notarize needs a Developer ID Application signature"
  step "Notarizing with keychain profile \"$NOTARY_PROFILE\""
  ditto -c -k --keepParent "$APP" "$WORK/$APP_NAME.zip"
  xcrun notarytool submit "$WORK/$APP_NAME.zip" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  spctl --assess --type execute --verbose "$APP"
fi

# --- Package ---------------------------------------------------------------------------------
if [ "$PACKAGE" = 1 ]; then
  [ "$NOTARIZE" = 1 ] || warn "packaging a build that is not notarized; Gatekeeper will block it on other Macs"
  ZIP="dist/$APP_NAME-$VERSION.zip"
  step "Packaging $ZIP"
  mkdir -p dist
  rm -f "$ZIP"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
  shasum -a 256 "$ZIP"
fi

# --- Disk image ------------------------------------------------------------------------------
# The usual drag-to-Applications window: Clawd.app beside a link to /Applications.
if [ "$DMG" = 1 ]; then
  [ "$NOTARIZE" = 1 ] || warn "packaging a build that is not notarized; Gatekeeper will block it on other Macs"
  DMG_PATH="dist/$APP_NAME-$VERSION-macOS.dmg"
  step "Packaging $DMG_PATH"
  mkdir -p dist
  rm -f "$DMG_PATH"
  STAGE="$WORK/dmg"
  rm -rf "$STAGE" && mkdir -p "$STAGE"
  ditto "$APP" "$STAGE/$APP"
  ln -s /Applications "$STAGE/Applications"
  RW="$WORK/$APP_NAME-rw.dmg"
  rm -f "$RW"
  hdiutil create -quiet -volname "$APP_NAME" -srcfolder "$STAGE" -fs HFS+ -format UDRW "$RW"
  MOUNT=$(hdiutil attach -readwrite -noverify -noautoopen "$RW" | sed -n 's#.*\(/Volumes/.*\)#\1#p' | tail -1)
  [ -n "$MOUNT" ] || die "could not mount $RW"
  # Lay out the window in Finder; without it the image still works, in Finder's default view.
  osascript >/dev/null 2>&1 <<APPLESCRIPT || warn "could not lay out the disk image window (Finder automation); using the default view"
tell application "Finder"
  tell disk "$(basename "$MOUNT")"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, 740, 460}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to 112
    set text size of opts to 13
    set position of item "$APP" of container window to {140, 150}
    set position of item "Applications" of container window to {400, 150}
    update without registering applications
    delay 1
    close
  end tell
end tell
APPLESCRIPT
  rm -rf "$MOUNT/.fseventsd"
  sync
  hdiutil detach -quiet "$MOUNT" || hdiutil detach -quiet -force "$MOUNT"
  hdiutil convert -quiet "$RW" -format ULFO -o "$DMG_PATH"
  rm -f "$RW"
  if [ -n "$IDENTITY" ]; then
    codesign --force --timestamp --sign "$IDENTITY" "$DMG_PATH"
  fi
  if [ "$NOTARIZE" = 1 ]; then
    step "Notarizing $DMG_PATH"
    xcrun notarytool submit "$DMG_PATH" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG_PATH"
    spctl --assess --type open --context context:primary-signature --verbose "$DMG_PATH"
  fi
  shasum -a 256 "$DMG_PATH"
fi

# --- Install ---------------------------------------------------------------------------------
if [ "$INSTALL" = 1 ]; then
  DEST="/Applications/$APP"
  step "Installing to $DEST"
  # Quit the running copy first; replacing a running app's binary gets it killed mid-flight.
  osascript -e "quit app id \"$BUNDLE_ID\"" >/dev/null 2>&1 || true
  i=0
  while pgrep -x "$APP_NAME" >/dev/null 2>&1 && [ $i -lt 25 ]; do
    sleep 0.2
    i=$((i + 1))
  done
  pkill -x "$APP_NAME" 2>/dev/null || true
  rm -rf "$DEST" || die "could not remove the existing $DEST"
  ditto "$APP" "$DEST" || die "could not copy $APP to /Applications"
  open "$DEST"
fi

step "Done: $APP ($VERSION, $BUNDLE_ID)"

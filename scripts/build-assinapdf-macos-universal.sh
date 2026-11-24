#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT_DIR/src/AssinaPDF.CrossPlatform/AssinaPDF.MacPrototype.csproj"
VERSION="${ASSINAPDF_VERSION:-$(awk -F'[<>]' '/<Version>/{print $3; exit}' "$PROJECT")}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || { echo "Versão inválida: $VERSION" >&2; exit 2; }

for tool in dotnet lipo hdiutil codesign sips iconutil ditto; do
  command -v "$tool" >/dev/null || { echo "Ferramenta obrigatória ausente: $tool (este build requer runner macOS)." >&2; exit 2; }
done

RUN_ROOT="${RUNNER_TEMP:-$ROOT_DIR/artifacts}/assinapdf-macos-$VERSION"
PUBLISH_ROOT="$RUN_ROOT/publish"
X64="$PUBLISH_ROOT/osx-x64"
ARM64="$PUBLISH_ROOT/osx-arm64"
APP="$RUN_ROOT/AssinaPDF.app"
MACOS="$APP/Contents/MacOS"
STAGING="$RUN_ROOT/dmg-staging"
DIST="$RUN_ROOT/dist"
EXECUTABLE="AssinaPDF.MacPrototype"

rm -rf "$RUN_ROOT"
mkdir -p "$X64" "$ARM64" "$MACOS" "$APP/Contents/Resources" "$STAGING" "$DIST"

echo "== Restore =="
dotnet restore "$PROJECT"

echo "== Publish Intel x86_64 =="
dotnet publish "$PROJECT" -c Release -r osx-x64 --self-contained true \
  -p:UseAppHost=true -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None \
  -p:Version="$VERSION" -p:AssemblyVersion="$VERSION.0" -p:FileVersion="$VERSION.0" \
  -p:InformationalVersion="$VERSION" -o "$X64"

echo "== Publish Apple Silicon arm64 =="
dotnet publish "$PROJECT" -c Release -r osx-arm64 --self-contained true \
  -p:UseAppHost=true -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None \
  -p:Version="$VERSION" -p:AssemblyVersion="$VERSION.0" -p:FileVersion="$VERSION.0" \
  -p:InformationalVersion="$VERSION" -o "$ARM64"

test -s "$X64/$EXECUTABLE"
test -s "$ARM64/$EXECUTABLE"
ditto "$ARM64/." "$MACOS/"

echo "== Merge every native Mach-O file =="
while IFS= read -r -d '' afile; do
  rel="${afile#"$ARM64/"}"
  xfile="$X64/$rel"
  if /usr/bin/file -b "$afile" | grep -q 'Mach-O'; then
    if [[ ! -f "$xfile" ]] || ! /usr/bin/file -b "$xfile" | grep -q 'Mach-O'; then
      echo "ERRO: binário ARM64 sem par Intel: $rel" >&2; exit 1
    fi
    mkdir -p "$MACOS/$(dirname "$rel")"
    lipo -create "$xfile" "$afile" -output "$MACOS/$rel.universal"
    mv "$MACOS/$rel.universal" "$MACOS/$rel"
  fi
done < <(find "$ARM64" -type f -print0)

while IFS= read -r -d '' xfile; do
  rel="${xfile#"$X64/"}"
  afile="$ARM64/$rel"
  if /usr/bin/file -b "$xfile" | grep -q 'Mach-O'; then
    if [[ ! -f "$afile" ]] || ! /usr/bin/file -b "$afile" | grep -q 'Mach-O'; then
      echo "ERRO: binário Intel sem par ARM64: $rel" >&2; exit 1
    fi
  fi
  dest="$MACOS/$rel"
  if [[ ! -e "$dest" ]]; then
    mkdir -p "$(dirname "$dest")"
    cp -p "$xfile" "$dest"
  fi
done < <(find "$X64" -type f -print0)

chmod +x "$MACOS/$EXECUTABLE"
ARCHS="$(lipo -archs "$MACOS/$EXECUTABLE")"
echo "AppHost: $ARCHS"
[[ " $ARCHS " == *" arm64 "* && " $ARCHS " == *" x86_64 "* ]] || { echo "AppHost não é universal." >&2; exit 1; }

ICONSET="$RUN_ROOT/AssinaPDF.iconset"
mkdir -p "$ICONSET"
LOGO="$ROOT_DIR/src/AssinaPDF.CrossPlatform/logo-future.png"
if [[ -f "$LOGO" ]]; then
  sips -z 16 16 "$LOGO" --out "$ICONSET/icon_16x16.png" >/dev/null
  sips -z 32 32 "$LOGO" --out "$ICONSET/icon_16x16@2x.png" >/dev/null
  sips -z 32 32 "$LOGO" --out "$ICONSET/icon_32x32.png" >/dev/null
  sips -z 64 64 "$LOGO" --out "$ICONSET/icon_32x32@2x.png" >/dev/null
  sips -z 128 128 "$LOGO" --out "$ICONSET/icon_128x128.png" >/dev/null
  sips -z 256 256 "$LOGO" --out "$ICONSET/icon_128x128@2x.png" >/dev/null
  sips -z 256 256 "$LOGO" --out "$ICONSET/icon_256x256.png" >/dev/null
  sips -z 512 512 "$LOGO" --out "$ICONSET/icon_256x256@2x.png" >/dev/null
  sips -z 512 512 "$LOGO" --out "$ICONSET/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$LOGO" --out "$ICONSET/icon_512x512@2x.png" >/dev/null
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AssinaPDF.icns"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>AssinaPDF</string>
  <key>CFBundleDisplayName</key><string>AssinaPDF | Future Soluções</string>
  <key>CFBundleIdentifier</key><string>com.futuresolucoes.assinapdf</string>
  <key>CFBundleExecutable</key><string>$EXECUTABLE</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleIconFile</key><string>AssinaPDF</string>
</dict>
</plist>
PLIST

plutil -lint "$APP/Contents/Info.plist"
# Assinatura ad-hoc para testes. Distribuição pública exige Developer ID e notarização.
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

ditto "$APP" "$STAGING/AssinaPDF.app"
ln -s /Applications "$STAGING/Applications"
cat > "$STAGING/LEIA-ME.txt" <<'INFO'
AssinaPDF | Future Soluções

Arraste AssinaPDF.app para Applications.
Pacote de teste assinado ad-hoc; a distribuição pública deverá receber assinatura Developer ID e notarização Apple.
INFO

DMG="$DIST/AssinaPDF-macOS-Universal-$VERSION.dmg"
hdiutil create -volname "AssinaPDF | Future Soluções" -srcfolder "$STAGING" -ov -format UDZO "$DMG"

echo "== Auditoria de todos os binários nativos no .app =="
while IFS= read -r -d '' f; do
  if /usr/bin/file -b "$f" | grep -q 'Mach-O'; then
    arches="$(lipo -archs "$f")"
    echo "$arches  $f"
    [[ " $arches " == *" arm64 "* && " $arches " == *" x86_64 "* ]] || {
      echo "ERRO: binário nativo não universal: $f ($arches)" >&2; exit 1;
    }
  fi
done < <(find "$MACOS" -type f -print0)

shasum -a 256 "$DMG" | tee "$DMG.sha256"
echo "DMG pronto: $DMG"

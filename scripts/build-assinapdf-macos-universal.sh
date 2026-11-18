#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT_DIR/src/AssinaPDF.CrossPlatform/AssinaPDF.MacPrototype.csproj"
ARTIFACTS="$ROOT_DIR/artifacts"
RUN_ID="$(date +%Y%m%d-%H%M%S)"
RUN_ROOT="$ARTIFACTS/release-assinapdf/$RUN_ID"
PUBLISH_ROOT="$RUN_ROOT/publish"
ARM64="$PUBLISH_ROOT/osx-arm64"
X64="$PUBLISH_ROOT/osx-x64"
UNIVERSAL="$PUBLISH_ROOT/osx-universal"
APP="$RUN_ROOT/AssinaPDF.app"
STAGING="$RUN_ROOT/macos-dmg-staging"
VERSION="${ASSINAPDF_VERSION:-0.4.2}"
APP_EXECUTABLE="AssinaPDF.MacPrototype"

command -v dotnet >/dev/null || { echo "Instale o .NET 10 SDK."; exit 1; }
command -v lipo >/dev/null || { echo "Este script deve ser executado no macOS (lipo não encontrado)."; exit 1; }
command -v hdiutil >/dev/null || { echo "hdiutil não encontrado."; exit 1; }

if [[ -e "$RUN_ROOT" ]]; then echo "Pasta de saída já existe: $RUN_ROOT"; exit 1; fi
mkdir -p "$PUBLISH_ROOT" "$RUN_ROOT/installers"
dotnet restore "$PROJECT"
dotnet publish "$PROJECT" -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None -o "$ARM64"
dotnet publish "$PROJECT" -c Release -r osx-x64 --self-contained true \
  -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None -o "$X64"

mkdir -p "$UNIVERSAL" "$APP/Contents/MacOS" "$APP/Contents/Resources" "$STAGING"
ditto "$ARM64/." "$UNIVERSAL/"

echo "Mesclando binários nativos arm64 + x86_64..."
while IFS= read -r -d '' ARM_FILE; do
  RELATIVE="${ARM_FILE#"$ARM64/"}"
  X64_FILE="$X64/$RELATIVE"
  DEST="$UNIVERSAL/$RELATIVE"
  [[ -f "$X64_FILE" ]] || continue

  if file "$ARM_FILE" | grep -q "Mach-O"; then
    ARM_ARCHS="$(lipo -archs "$ARM_FILE" 2>/dev/null || true)"
    X64_ARCHS="$(lipo -archs "$X64_FILE" 2>/dev/null || true)"
    if [[ "$ARM_ARCHS" == *arm64* && "$ARM_ARCHS" == *x86_64* ]]; then
      continue
    elif [[ "$X64_ARCHS" == *arm64* && "$X64_ARCHS" == *x86_64* ]]; then
      cp -p "$X64_FILE" "$DEST"
    elif [[ "$ARM_ARCHS" == *arm64* && "$X64_ARCHS" == *x86_64* ]]; then
      mkdir -p "$(dirname "$DEST")"
      lipo -create "$ARM_FILE" "$X64_FILE" -output "$DEST"
    fi
  fi
done < <(find "$ARM64" -type f -print0)

# Inclui arquivos exclusivos do publish Intel, sem substituir os equivalentes mesclados.
while IFS= read -r -d '' X64_FILE; do
  RELATIVE="${X64_FILE#"$X64/"}"
  DEST="$UNIVERSAL/$RELATIVE"
  if [[ ! -e "$DEST" ]]; then
    mkdir -p "$(dirname "$DEST")"
    cp -p "$X64_FILE" "$DEST"
  fi
done < <(find "$X64" -type f -print0)

ditto "$UNIVERSAL/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$APP_EXECUTABLE"

ICONSET="$RUN_ROOT/AssinaPDF.iconset"
mkdir -p "$ICONSET"
SOURCE_LOGO="$ROOT_DIR/src/AssinaPDF.CrossPlatform/logo-future.png"
if [[ -f "$SOURCE_LOGO" ]]; then
  sips -z 16 16 "$SOURCE_LOGO" --out "$ICONSET/icon_16x16.png" >/dev/null
  sips -z 32 32 "$SOURCE_LOGO" --out "$ICONSET/icon_16x16@2x.png" >/dev/null
  sips -z 32 32 "$SOURCE_LOGO" --out "$ICONSET/icon_32x32.png" >/dev/null
  sips -z 64 64 "$SOURCE_LOGO" --out "$ICONSET/icon_32x32@2x.png" >/dev/null
  sips -z 128 128 "$SOURCE_LOGO" --out "$ICONSET/icon_128x128.png" >/dev/null
  sips -z 256 256 "$SOURCE_LOGO" --out "$ICONSET/icon_128x128@2x.png" >/dev/null
  sips -z 256 256 "$SOURCE_LOGO" --out "$ICONSET/icon_256x256.png" >/dev/null
  sips -z 512 512 "$SOURCE_LOGO" --out "$ICONSET/icon_256x256@2x.png" >/dev/null
  sips -z 512 512 "$SOURCE_LOGO" --out "$ICONSET/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$SOURCE_LOGO" --out "$ICONSET/icon_512x512@2x.png" >/dev/null
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
  <key>CFBundleExecutable</key><string>$APP_EXECUTABLE</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleIconFile</key><string>AssinaPDF</string>
</dict>
</plist>
PLIST

# Assinatura ad-hoc para facilitar testes locais. Distribuição pública exige Developer ID + notarização.
codesign --force --deep --sign - "$APP"

ditto "$APP" "$STAGING/AssinaPDF.app"
ln -s /Applications "$STAGING/Applications"
cat > "$STAGING/LEIA-ME.txt" <<'INFO'
AssinaPDF | Future Soluções

Arraste AssinaPDF.app para a pasta Applications.
Na primeira abertura, o macOS pode solicitar confirmação de segurança porque este pacote de teste não está notarizado.

A versão pública deve ser assinada com Developer ID e notarizada pela Apple.
INFO

DMG="$RUN_ROOT/installers/AssinaPDF-macOS-Universal-$VERSION.dmg"
hdiutil create -volname "AssinaPDF | Future Soluções" -srcfolder "$STAGING" -ov -format UDZO "$DMG"

echo "Instalador macOS Universal gerado: $DMG"
shasum -a 256 "$DMG"

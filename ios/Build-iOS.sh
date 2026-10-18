#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
if ! command -v xcodegen >/dev/null; then
  echo 'XcodeGen is required. Install with: brew install xcodegen'; exit 1
fi
xcodegen generate
xcodebuild -project CellRelay.xcodeproj -scheme CellRelay -configuration Release -sdk iphoneos -destination 'generic/platform=iOS' -derivedDataPath build CODE_SIGNING_ALLOWED=NO build
mkdir -p build/package/Payload
ditto build/Build/Products/Release-iphoneos/CellRelay.app build/package/Payload/CellRelay.app
cd build/package
ditto -c -k --sequesterRsrc --keepParent Payload ../CellRelay-unsigned.ipa
echo 'Created ios/build/CellRelay-unsigned.ipa (requires signing before installation).'

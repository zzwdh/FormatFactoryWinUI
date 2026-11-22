# Optical fallback engines

The application first uses Windows native CDDA and sector-reading APIs. The fallback payload contains only icedax.exe or readom.exe and cygwin1.dll; disc-writing executables, development files and unrelated utilities are excluded.

These pinned Cygwin x64 binaries run natively on x64 and use Windows 11 x64 emulation on ARM64. Packaging checks PE architecture, SHA256/SHA512 and bounded version probes. Physical-drive operations require separate hardware testing; a version probe is not a successful disc-read test.

cdrkit 1.1.11 is distributed under GNU GPL v2; see licenses/CDRKIT_COPYING.txt. Cygwin 3.6.10 includes GNU GPL v3 and its additional permissions; see licenses/CYGWIN_COPYING.txt and licenses/CYGWIN_LICENSE. Exact binary and corresponding source URLs and official Cygwin index hashes are in sources.lock.json. The preparation script downloads and verifies both corresponding source archives into the build cache for redistribution alongside a release.

Rebuild from the repository root:

```powershell
./packaging/optical/Prepare-OpticalEngines.ps1 -Destination ./work/prepared-engines/x64/Engines -Architecture x64 -Offline
```

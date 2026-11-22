# 7-Zip archive conversion runtime

This application includes the unmodified official 7-Zip 26.05 command-line
runtime, Copyright (C) 1999-2026 Igor Pavlov. `7z.exe` and `7z.dll` are extracted
from the official Windows installer without installing or registering it.
The native x64 or ARM64 runtime matches the application package architecture.

7-Zip is licensed under GNU LGPL 2.1 or later, with the additional BSD notices
and unRAR restriction described in `License.txt`. Complete LGPL and unRAR texts
are in `licenses/`. The RAR decompression code must not be used to develop a
RAR-compatible compressor. This application reads RAR archives; it does not
create RAR archives.

The matching, unmodified source archive is included at
`licenses/7z2605-src.tar.xz`. Its official source and release locations are:

- https://www.7-zip.org/
- https://github.com/ip7z/7zip/releases/tag/26.05
- https://github.com/ip7z/7zip/releases/download/26.05/7z2605-src.tar.xz

`packaging/engine-sources.json` fixes the installer, source, and build-only
public-domain `7zr.exe` bootstrap SHA256 values. The bootstrap, GUI, shell
extensions, installer, and uninstaller are not part of the runtime payload.

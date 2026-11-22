# Compatibility decoder source build

This directory rebuilds the bundled LibreMPEG compatibility pair for the
application's decoding role. It does not replace or reduce the primary FFmpeg
encoder. The source commit and archive/tool SHA256 values are pinned in
`sources.lock.json`; the MSVC compiler, linker and Windows SDK remain governed by
`build/toolchain.lock.json`.

The profile keeps `ffmpeg.exe`, `ffprobe.exe`, shared conversion libraries, the
source's native input decoders, demuxers, parsers, muxers and bitstream filters.
It enables only FFV1, PCM signed 32-bit little-endian, PCM signed 16-bit
little-endian and wrapped-frame output encoders. The first two produce the
application's lossless Matroska intermediate; the latter two support its null
decode preflight. Subtitle/attachment copying, metadata and chapters remain part
of that contract. Device capture, playback and hardware acceleration are outside
this compatibility profile. Main-engine hardware detection still needs `lavfi`
and is unaffected by these settings.

Compatibility filters are restricted to the buffer/sink, format/resample, scale,
null, right-angle/arbitrary rotation, split, timestamp/aspect, crop/pad, frame-rate and trim primitives
needed by automatic conversion and preflight. The fork also requires the implicit
`premultiply_dynamic`, `acl2cl`, `ardftsrc` and `asf2sf` converters for alpha,
channel-layout, rate and audio sample-format negotiation; `apad` remains available
for audio padding. These are required even when the caller provides no filter
string. User-facing visual/HDR filters continue to run in the primary engine.
Broad unrelated filters are not compiled into this helper runtime.

The retained external decode dependencies are dav1d, JPEG XL (including animation
and boxes), zlib-ng's zlib-compatible API, BZip2, libiconv with extra encodings,
libxml2 with its iconv/zlib support enabled, and LCMS2.
JPEG XL also uses Brotli and Highway. AV1 needs an actual software decoder: retaining only the
native hardware-backed `av1` implementation is insufficient. The zlib and XML
dependencies preserve compressed input and DASH/IMF parsing. BZip2 keeps compressed
Matroska tracks readable, and libiconv retains character conversion used by
metadata and subtitles. LZMA was not enabled in the original runtime and remains
outside this profile. The build downloads no dependency source beyond the fixed
original release source bundle.
The pinned exclusion list omits one generated LCMS2 Libtool symlink and twelve
JPEG XL benchmark wrapper symlinks from extraction; neither is used by this
build. Every other extracted path retains the strict traversal/reparse checks.

## Build

Use PowerShell 7.2+, the locked MSVC/Windows SDK, a full Python 3 installation and
Git for Windows Bash. Pass Python explicitly; the script does not install a
system Python or change global environment settings. Windows `tar.exe` must
support the locked XZ/Zstandard archives. The pinned portable Make, NASM, Meson,
Ninja, CMake, pkgconf and Coreutils are extracted only into the build directory.

```powershell
./packaging/ffmpeg/Build-CompatibilityRuntime.ps1 `
  -PythonPath C:/Python312/python.exe `
  -CacheDirectory ./work/engine-downloads `
  -OutputDirectory ./work/compatibility-candidate `
  -Jobs 8 -Offline
```

Omit `-Offline` to populate the checksum-locked cache. `-VcRuntimeCacheDirectory`
can select a separate existing cache for the Microsoft redistributable already
locked by `packaging/vcredist`. `-VCToolsRoot`, `-WindowsSdkRoot` and `-GitRoot`
select explicit development-tool locations without installing or updating them.
`-SourceArchivePath` accepts an already downloaded original source bundle and
still verifies its fixed SHA256. Python can also be provided with
`FORMAT_FACTORY_FFMPEG_BUILD_PYTHON`; the prior prepared CAJ interpreter and
ordinary non-Store Python commands are fallbacks when no explicit path is given.

NASM cannot resolve this checkout's Unicode absolute paths. All source, object
and dependency-prefix paths therefore live in a dedicated ASCII directory under
the Windows temporary directory by default. `-BuildDirectory` can select a fresh
ASCII path without spaces inside that temporary directory or the repository's
`work/` directory. The recipe creates an identity marker, refuses unrelated
nonempty directories and does not automatically delete source/build/output
directories. A completed runtime output must be a fresh directory under `work/`.

Successful builds keep a private runtime cache under their owned build
directory. Its key covers the recipes, source/tool lock, compiler/SDK lock,
Python/Bash runtime identity and dependency-test setting. Reuse checks the
receipt and every staged file before copying bytes into a fresh output; a
second application architecture can reuse this same x64 compatibility runtime.

Some restricted process runners cannot create the kernel objects needed by
MSYS. Run the recipe in an ordinary local Windows process in that case; the
recipe itself does not require an administrator installation or registry change.

All static dependencies and LibreMPEG use the dynamic MSVC CRT (`/MD`). Generated
pkg-config prefixes are made relocatable, use MSYS paths for environment path
lists, and retain the static-library preprocessor definitions. Exact COFF
archive aliases resolve upstream pkg-config/MSVC naming differences; no library
bytes or upstream source files are patched. The Windows CRT supplies `libm`, so
the generated Unix `-lm` metadata flag is removed.
libiconv uses its unchanged upstream Autoconf configuration and MSVC wrappers,
followed by native compilation of its three static-library translation units.
It is built before libxml2 so XML input retains character-set and gzip support.
The fork's `--disable-autodetect` path probes iconv as a libc feature, so the
profile explicitly links `iconv.lib` to preserve subtitle character conversion
in the shared codec library as well as libxml2's separate format-library use.

Dependency checks run by default: zlib-ng's upstream CTest suite, dav1d header
checks and its SIMD/reference checkasm tests. dav1d checkasm uses a separate
`trim_dsp=false` test build; it does not install that test library over the
production library. `-SkipDependencyTests` explicitly records a skipped status.
`-ConfigureOnly` still builds the dependencies but stops after LibreMPEG's
configure stage.

The final staging step adds the verified private x64 Microsoft CRT, preserves
component notices (including libxml2's `Copyright`), validates ordinary/delay/
transitive private PE imports with `Assert-FfmpegPeDependencies`, and writes a
per-file `build-receipt.json`. This receipt establishes build provenance and
staged bytes; it does not claim successful conversion validation or acceptance
for release. The build is reproducible from locked inputs in the sense of a
documented rebuild; byte-identical compiler output is not assumed.
Passing `-PrimaryEngineDirectory` additionally runs the finite corpus as a
candidate self-check against fixed primary-engine observers, and records that
scope separately. It is not the old-versus-new comparison below.

## Validate before packaging

Keep the original compatibility runtime available as the baseline. Run the
comparison against a clean staged runtime, not the compiler's object directory:

```powershell
./packaging/ffmpeg/Compare-Capabilities.ps1 `
  -Baseline ./work/original-compatibility/ffmpeg.exe `
  -Candidate ./work/compatibility-candidate/ffmpeg.exe `
  -Primary ./work/prepared-engines/x64/Engines/ffmpeg/ffmpeg.exe `
  -OutputDirectory ./work/compatibility-capabilities

./work/prepared-engines/x64/Engines/caj2pdf/python.exe -I -B `
  ./packaging/ffmpeg/smoke-compatibility.py `
  --baseline ./work/original-compatibility/ffmpeg.exe `
  --candidate ./work/compatibility-candidate/ffmpeg.exe `
  --primary ./work/prepared-engines/x64/Engines/ffmpeg/ffmpeg.exe `
  --output ./work/compatibility-conversions
```

The capability gate compares decodable canonical codec IDs, every baseline
demuxer/muxer/bitstream-filter name and the required intermediate/preflight
encoders, filters and protocols. It separately checks BVC2, AC-4, Blackmagic RAW
and Bink 2. Hardware/wrapper implementation names are reported separately from
codec coverage. FFmpeg does not provide a `-parsers` listing; keep and inspect
the generated `CONFIG_*_PARSER` settings from the pinned source build.

The finite conversion corpus uses fixed primary-engine observers to compare
decoded streams, dimensions, color metadata, selected/multiple audio tracks,
RGBA alpha negotiation, 5.1 channel layouts, BZip2-compressed Matroska tracks,
subtitles, attachments, chapters, Unicode paths, AV1 software decoding and JPEG
XL. It restricts PATH to Windows System32 and verifies that inputs and staged
payloads remain unchanged. This is not complete DLL-loader isolation; the native
import gate is required as well. Original BVC2/AC-4/BRAW/Bink 2 recordings and
native ARM64 execution are not covered by the generated corpus. Enumeration
success does not prove every input file can decode.

Only after these gates pass should the source-built runtime replace the
prepared compatibility payload selected by `engine-sources.json`. Package-time
receipts and MSIX checks must bind the exact staged bytes again. Preserve the corresponding
source archive, these build definitions and every applicable license/notice
when distributing this GPL/AGPL-enabled runtime.

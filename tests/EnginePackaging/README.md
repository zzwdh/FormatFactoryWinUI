# Engine packaging dependency regressions

Run from the repository root with PowerShell 7:

```powershell
./tests/EnginePackaging/Run.ps1
```

The suite creates synthetic PE fixtures under `work/engine-packaging-tests-*`.
It does not launch payloads, install packages, or access the network.

It verifies that CAJ and FFmpeg payload validation reads both normal and delay imports,
requires app-local Visual C++ runtime DLLs, checks transitive runtime imports,
and inspects every `.exe` and `.dll` for the runtime architecture (plus CAJ's `.pyd`).
It also verifies executable versus DLL search directories, malformed PE import
tables, and dependency validation through prepared receipts and MSIX validation.
The missing-runtime fixtures remain rejected when matching DLLs are on `PATH`.

The CAJ validator deliberately targets the separate Visual C++ redistributable
families (`msvcp`, `vcruntime`, `concrt`, `vcomp`, and `msvcr`, including numbered
and debug variants). Windows-provided `msvcrt.dll` is exempt. Other Windows
imports are outside this focused check; the suite does not use an OS-wide DLL
allowlist or consider developer-machine DLLs to be bundled dependencies.

`Assert-FfmpegPeDependencies` validates both shared and static distributions,
including `ffmpeg-compatibility`. It checks FFmpeg's private `av*`/`sw*`/`postproc`
libraries, common MinGW runtime families (including GCC and winpthread), and the
Visual C++ runtime families. Every packaged DLL is parsed even if omitted from
the receipt's `peFiles` list, so architecture and transitive/delay dependencies
are checked. Other packaged modules must also resolve beside their importer.
The flat BtbN `bin` layout can be checked either directly or from an extraction
root; an unrelated subdirectory cannot satisfy a private import. Windows system
imports and API sets do not require copied operating-system DLLs.
In particular, Windows-provided `msvcrt.dll` and `msvcp_win.dll` are exempt from
the FFmpeg check's private VC runtime rule.

The FFmpeg fixtures cover x64 and ARM64 metadata, missing direct/transitive/delay
imports, host-PATH isolation, undeclared DLL architecture, wrong directories,
and the receipt and MSIX validation gates. Synthetic PE files are never runtime
or release evidence; real archive payloads still require conversion validation.

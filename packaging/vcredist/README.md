# Private Visual C++ runtimes

`Prepare-VcRuntime.ps1` extracts private DLLs from Microsoft's signed Visual C++ Redistributable 14.51.36247.0: four ARM64 files for LibreOffice and three x64 files for CAJ/MuPDF. It never executes the installer, installs a product, or changes the system runtime. Fixed Microsoft download URLs, archive SHA-256 values, exact embedded CAB ranges, per-file SHA-256 values, and original license files are locked in `sources.lock.json` (ARM64) and `sources.x64.lock.json` (x64). Windows `expand.exe` extracts only the named entries. Both archives and selected DLLs must retain valid Microsoft Authenticode signatures; each selected DLL must match its native PE machine (`AA64` or `8664`).

The DLLs are copied into the LibreOffice `program` folder or alongside CAJ's `python.exe` and `mutool.exe`. The original English and Simplified Chinese license texts, this notice, and the architecture's source lock are retained in each component's `vcredist-notices` folder. The x64 bootstrapper links to Microsoft's license webpage instead of embedding RTFs, so its lock also pins the signed, same-version ARM64 archive containing the original license texts; no ARM64 DLL is copied into CAJ. Distribution remains subject to the Microsoft Visual Studio license terms and redistributable-code rights; these files are not relicensed under the conversion engines' licenses.

## CAJ/MuPDF x64 dependencies

MuPDF 1.26.2 `mutool.exe` imports `msvcp140.dll`, `vcruntime140.dll`, and `vcruntime140_1.dll`. The pinned files' own normal and delay imports require only the same runtime set and Windows system libraries. Both x64 and ARM64 application packages use this x64 CAJ runtime (Windows x64 emulation on ARM64). The Python embed archive already includes older VCRUNTIME files; preparation replaces them with the verified, same-version private set.

CAJ preparation and receipt validation inspect normal and delay imports of all CAJ `.exe`, `.dll`, and `.pyd` files and reject missing private VC dependencies or wrong architectures, without consulting the build machine's system libraries. The launcher also requires all three files before probing MuPDF, and its error mode prevents native loader dialogs in child helpers. The CAJ smoke corpus verifies each missing-runtime failure in an isolated copy.

## LibreOffice ARM64 dependencies

Static normal-import and delay-import analysis of the official LibreOffice 26.8.0 ARM64 program files found `msvcp140.dll`, `msvcp140_1.dll`, `msvcp140_2.dll`, and `vcruntime140.dll`. Their native imports are closed by the same four files and Windows system libraries. The outer packaging manifest requires and verifies all four, then records every output file's hash.

The official package also contains `vcruntime140_1.dll_arm64`, whose PE machine is actually AMD64 (`8664`). No native ARM64 LibreOffice file imports it. It is deliberately omitted; a filename suffix alone is not accepted as architecture evidence. Optional MFC, AMP, concurrency, and other unused runtime files are omitted as well. A new LibreOffice/runtime version requires repeating dependency analysis and updating the lock. No ARM64 execution is claimed by static validation on an x64 build machine.

## Usage

```powershell
./packaging/vcredist/Prepare-VcRuntime.ps1 -Destination ./work/prepared-engines/arm64/Engines/libreoffice/program -CacheDirectory ./work/engine-downloads -Architecture arm64 -Offline
./packaging/vcredist/Prepare-VcRuntime.ps1 -Destination ./work/vcredist-validation/caj-x64 -CacheDirectory ./work/engine-downloads -Architecture x64 -Offline
```

The destination must be a directory below this repository's `work` directory. Offline mode still validates cached bytes and signatures. It fails if the locked archive is missing or modified. Local DLL updates are delivered with the application package.

Microsoft references:
- [Latest supported Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170)
- [Redistribute Visual C++ files and licensing](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170)
- [Deployment and application-local DLLs](https://learn.microsoft.com/en-us/cpp/windows/deployment-in-visual-cpp?view=msvc-170)

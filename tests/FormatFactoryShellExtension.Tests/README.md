# Native C++ Shell integration checks

These Windows-only tests use read-only COM memory streams, real 32-bit HBITMAPs and hidden Win32 child windows. They do not register the extension or modify Explorer settings.

```powershell
pwsh -NoProfile -File tests/FormatFactoryShellExtension.Tests/Run.ps1 -Architecture all -ValidateManifest
```

The C++ executable covers EPUB 2/3 metadata and covers, readable text, malformed XML, external paths, ZIP traversal and entry-count limits, stream ownership, bitmap shape, preview resize/reparent/unload, parent destruction, and repeated GDI resource recovery. It also verifies menu selection short-circuiting, cover decode reuse, and cleanup/retry after window creation failures.

The native build uses the Windows SDK and MSVC x64/ARM64 tools. Outputs are under `work/native-shell/Release/<architecture>`. Both architectures can be compiled on x64; ARM64 execution still requires a compatible device. The former managed Shell project and its test executable have been retired.

`Validate-Package.ps1` invokes the production manifest generator and Windows SDK MakeAppx for x64 and ARM64, checks exact format associations, and verifies that thumbnail/preview registration applies only to EPUB. The generated packages contain sentinel payloads for schema validation; they must not be installed or distributed.

The handler reads EPUBs up to 64 MiB with at most 4,096 entries. XML resources are limited to 2 MiB, JPEG/PNG covers to 8 MiB and 40 million pixels, and readable text to 40,000 characters from up to eight spine entries. ZIP64/multipart archives are rejected. Scripts, HTML rendering, external resources and file extraction are not used. Files without covers still have a text preview; thumbnail requests fail normally so Explorer can use its standard icon.

Real Explorer activation requires an installed MSIX package; it is separate from these non-installing checks.

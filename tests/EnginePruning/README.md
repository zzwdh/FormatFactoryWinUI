# Conversion-runtime pruning checks

Run the small module regression suite with PowerShell 7:

```powershell
./tests/EnginePruning/Run.ps1
```

It uses synthetic resource directories without launching programs. The checks
protect Writer/Chart/Math/VBA registrations and DLLs, the Calibre frozen plugin
archive and workers, fonts, hyphenation, SVG support and original license text.
They also verify that pruning is idempotent and refuses incomplete directories,
root or descendant junctions, and ordinary directories reached through ancestor
junctions before altering any payload. The suite checks these invariants rather
than duplicating each module's list of files to remove. Fixtures remain under
`work/engine-pruning-tests-*` for inspection.

The shared synthetic PE builder also tests retained native dependency closure:
Windows imports pass, missing direct or delayed transitive private DLLs fail even
when present on the host PATH, and undeclared retained plugins must have the
correct architecture. Original module inventory entries must be leaf names.
An actual synthetic PE32 CLI header verifies that neutral AnyCPU DLLs pass,
while 32-bit-required/native-entry managed DLLs and native x86 DLLs still fail
the x64 architecture check.
Intentional foreign-architecture helpers from the original payload require an
exception bound to their exact relative path, PE machine/magic and SHA256.
Changed or renamed helpers, mismatched metadata, unsafe paths and duplicate
exception records fail; allowed helpers still undergo private dependency checks.

Run the real Windows A/B corpus using the prepared isolated Python:

```powershell
./work/prepared-engines/x64/Engines/caj2pdf/python.exe -I -B ./packaging/smoke-office-ebook-pruning.py --before <original-Engines> --after <optimized-Engines> --output <new-output-directory>
```

Both roots need `libreoffice/` and `calibre/`. The original root's
`caj2pdf/mutool.exe` inspects PDF output; `--mutool <absolute-executable>` can
select a separate fixed observer. Omit `--before` to perform the same content
checks on one prepared payload; the after payload then supplies the observers.
No FFmpeg payload, installed converters, network services, or third-party Python
packages are required. Child process PATH contains Windows System32 only. All
profiles and caches remain in the output directory and payload hashes must stay
unchanged.

The corpus creates Chinese paths containing spaces and genuine DOCX/EPUB ZIP
documents with Chinese text, PNG images, a native DrawingML chart, table cells,
explicit page breaks, hyperlinks, page headers/footers, and ebook navigation.
It also exercises an SVG cover, inline SVG, embedded font/CSS and an ebook with
no provided cover, with English and Chinese Calibre UI locales.

Product routes tested are DOC/DOCX to PDF, DOC to DOCX normalization, DOCX to
DOCX repair, and EPUB to MOBI/AZW3. Reference-only DOCX to DOC creates a common
binary DOC input. Reference-only MOBI/AZW3 to EPUB decodes both sides' outputs
for content inspection. These observation steps do not add product input/output
formats. HTML/FB2 fixtures are generated to document the boundary, without
requiring the conversion-only Calibre profile to support those unused inputs or
Calibre's PDF pipeline.

PDF checks compare extracted text, page counts, headers/footers, image/chart
drawing operations, external annotations and per-page rendered pixels. They do
not compare timestamp-bearing PDF bytes. Ebook checks compare metadata, full
spine text, images, internal/external links, navigation and embedded font content
after decoding with a fixed observer. The observer disables its own random
default-cover generation; Adobe/IDPF UUID-based font obfuscation is decoded before
hashing the complete font bytes. This keeps fresh book identifiers and observer
cover artwork from masquerading as changed content or lost glyphs.
Commands, logs and artifacts stay in a timestamped run directory; its
`results.json` is also copied to `latest-results.json`. The report has
`schemaVersion: 1`, `status: passed|failed`, `mode: comparison|standalone`, a
`results` array with per-case statuses, and an explicit `scope` record.

These generated corpus tests complement existing app, engine and upstream
regressions; they do not replace package installation or architecture acceptance.

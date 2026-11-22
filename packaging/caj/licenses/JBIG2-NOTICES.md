# JBIG/JBIG2 component notices

The supplied `app/lib/bin/libjbigdec-w64.dll` and `libjbig2codec-w64.dll` are the Windows x64 binaries from caj2pdf revision `6c4bc32b15ce748d211f45d536f5d5511ef9f368`:
https://github.com/caj2pdf/caj2pdf/tree/6c4bc32b15ce748d211f45d536f5d5511ef9f368/lib

The decoder wrappers and standalone JBIG implementation are Copyright 2020-2021 Hin-Tak Leung. Their retained source headers select the FreeType Project License; a full copy is included as `FTL.TXT`. The wrapper source and build instructions are included in `caj2pdf-native-source/`.

The supplied JBIG2 binary contains jbig2dec. jbig2dec is an Artifex Software project distributed under GNU AGPL version 3 or later; the complete license text is included as `mupdf-COPYING.txt`. Upstream source and copyright notices are available at https://github.com/ArtifexSoftware/jbig2dec . The caj2pdf prebuilt binary does not report the linked jbig2dec revision; this record does not assert an exact source revision for that embedded library.

Full license texts obtained from their upstream publishers:
- LGPL v3: https://www.gnu.org/licenses/lgpl-3.0.txt
- GPL v3: https://www.gnu.org/licenses/gpl-3.0.txt
- FreeType Project License: https://github.com/freetype/freetype/blob/VER-2-13-3/docs/FTL.TXT

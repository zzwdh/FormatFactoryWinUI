# Public archive regression fixtures

The RAR5 archives come from the libarchive project's public test suite at fixed
commit `8509b99a248c52b676491a8675c3f92c7b5673bc`. They contain synthetic test data
and test executables; the tests only extract and compare their bytes and never
execute their contents.

`sources.json` records every original `.uu` source URL, the encoded-file SHA-256,
and the decoded archive's SHA-256. Decode each source's standard uuencoded body to
reproduce the corresponding `.rar`. The small stored and compressed archives
exercise RAR5 extraction; the eight-part archive exercises volume grouping,
selection of a non-first volume, and incomplete-volume rejection.

The fixtures retain the BSD two-clause terms from libarchive's corresponding
`libarchive/test/test_read_format_rar5.c`; see `LICENSE.txt`. The two encrypted
fixtures are pinned for future regression work; successful decryption is not
claimed by the present tests because their passwords have not been established.

Upstream source:
https://github.com/libarchive/libarchive/tree/8509b99a248c52b676491a8675c3f92c7b5673bc/libarchive/test

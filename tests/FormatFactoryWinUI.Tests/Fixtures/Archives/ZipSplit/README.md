# Public split ZIP regression fixture

These three archive volumes and two byte-comparison reference files are copied
unchanged from Apache Commons Compress's public `COMPRESS-477` regression suite
at commit `0df4d084182c2c2662b777ee2c3b04a1c42bbc66`.

The archive contains Apache Java source files; tests extract and compare their
bytes only and never compile or execute the contents. Selecting `.z01`, `.z02`,
or the terminal `.zip` must identify one logical input. Missing any volume must
fail without publishing a partial result.

`sources.json` records the pinned URL and SHA-256 for every fixture and reference
file. `LICENSE.txt` and `NOTICE.txt` are retained unchanged from the same commit.

Upstream directory:
https://github.com/apache/commons-compress/tree/0df4d084182c2c2662b777ee2c3b04a1c42bbc66/src/test/resources/COMPRESS-477/split_zip_created_by_zip

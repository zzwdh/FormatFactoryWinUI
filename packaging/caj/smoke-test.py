"""Real portable CAJ smoke tests; run with the prepared bundle's python.exe."""
from __future__ import annotations
import argparse
from concurrent.futures import ThreadPoolExecutor
from io import BytesIO
import os
import shutil
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

arguments = argparse.ArgumentParser()
arguments.add_argument("--engine", required=True)
settings, remaining = arguments.parse_known_args()
ROOT = Path(settings.engine).resolve()
sys.path[:0] = [str(ROOT / "Lib" / "site-packages"), str(ROOT / "app")]
from PyPDF2 import PdfReader, PdfWriter
from PIL import Image


class PortableCajSmoke(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="caj-smoke-")
        self.folder = Path(self.temporary.name) / "中文 空格"
        self.folder.mkdir()
        writer = PdfWriter()
        writer.add_blank_page(width=200, height=300)
        writer.add_blank_page(width=400, height=500)
        stream = BytesIO()
        writer.write(stream)
        self.pdf = stream.getvalue()
        self.input = self.folder / "原始 多页.caj"
        self.input.write_bytes(self.pdf)

    def tearDown(self):
        self.temporary.cleanup()

    def invoke(self, *args, engine=ROOT):
        environment = os.environ.copy()
        environment["PATH"] = ""
        environment["PYTHONPATH"] = str(self.folder / "nonexistent-python")
        return subprocess.run([str(engine / "python.exe"), "-I", "-B", str(engine / "launcher.py"), *map(str, args)],
                              cwd=self.folder, env=environment, capture_output=True, text=True,
                              encoding="utf-8", timeout=30, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def assert_pdf(self, path, pages=2):
        with path.open("rb") as stream:
            self.assertEqual(len(PdfReader(stream).pages), pages)

    def caj_container(self):
        header = bytearray(0x400)
        header[:4] = b"CAJ\0"
        struct.pack_into("<i", header, 0x10, 2)
        struct.pack_into("<i", header, 0x14, 0x20)
        struct.pack_into("<i", header, 0x20, len(header))
        struct.pack_into("<i", header, 0x110, 0)
        # The proprietary CAJ path rebuilds xref from embedded PDF objects. Its
        # reverse scan operates in 4096-byte blocks, so use a realistic size.
        body = self.pdf[self.pdf.index(b"\n") + 1:].replace(b"\n", b"\r")
        wrapped = self.folder / "PDF包装 两页.caj"
        wrapped.write_bytes(header + b"%" + b"padding " * 600 + b"\r" + body)
        return wrapped

    def test_full_dependency_probe_without_system_path(self):
        result = self.invoke("--version")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("mutool version 1.26.2", result.stdout)

    def test_missing_dependencies_report_failure(self):
        isolated = self.folder / "isolated-engine"
        shutil.copytree(ROOT, isolated)
        for relative in (
            "mutool.exe", "app/cajparser.py", "app/lib/bin/libjbig2codec-w64.dll",
            "Lib/site-packages/PyPDF2/__init__.py",
        ):
            with self.subTest(dependency=relative):
                original = isolated / relative
                displaced = original.with_name(original.name + ".missing")
                original.rename(displaced)
                try:
                    result = self.invoke("--version", engine=isolated)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("Missing bundled dependency: " + relative, result.stderr)
                finally:
                    displaced.rename(original)

    def test_missing_private_visual_cpp_runtime_reports_failure(self):
        isolated = self.folder / "isolated-engine"
        shutil.copytree(ROOT, isolated)
        for relative in ("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"):
            with self.subTest(dependency=relative):
                original = isolated / relative
                displaced = original.with_name(original.name + ".missing")
                original.rename(displaced)
                try:
                    result = self.invoke("--version", engine=isolated)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("Missing bundled Microsoft Visual C++ dependency: " + relative, result.stderr)
                finally:
                    displaced.rename(original)

    def test_success_does_not_write_inside_engine_directory(self):
        before = {str(path.relative_to(ROOT)): (path.stat().st_size, path.stat().st_mtime_ns)
                  for path in ROOT.rglob("*") if path.is_file()}
        result = self.invoke("convert", self.caj_container(), "-o", self.folder / "immutable.pdf")
        self.assertEqual(result.returncode, 0, result.stderr)
        after = {str(path.relative_to(ROOT)): (path.stat().st_size, path.stat().st_mtime_ns)
                 for path in ROOT.rglob("*") if path.is_file()}
        self.assertEqual(before, after)

    def test_pdf_disguised_as_caj_and_unicode_paths(self):
        output = self.folder / "直通 结果.pdf"
        result = self.invoke("convert", self.input, "-o", output)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(output.read_bytes(), self.pdf)
        self.assert_pdf(output)

    def test_actual_caj_container_uses_bundled_mutool(self):
        output = self.folder / "CAJ修复 结果.pdf"
        result = self.invoke("convert", self.caj_container(), "-o", output)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assert_pdf(output)

    def test_parallel_conversions_use_separate_scratch_files(self):
        wrapped = self.caj_container()
        outputs = [self.folder / f"并发 {index}.pdf" for index in range(4)]
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(lambda output: self.invoke("convert", wrapped, "-o", output), outputs))
        for result, output in zip(results, outputs):
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assert_pdf(output)

    def test_unknown_format_does_not_replace_existing_output(self):
        self.input.write_bytes(b"NOT A SUPPORTED CAJ FORMAT")
        output = self.folder / "已有文件.pdf"
        output.write_bytes(b"keep existing content")
        result = self.invoke("convert", self.input, "-o", output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Unknown file type", result.stderr)
        self.assertEqual(output.read_bytes(), b"keep existing content")

    def test_unsupported_teb_returns_failure(self):
        self.input.write_bytes(b"TEB\0" + b"\0" * 1024)
        output = self.folder / "不支持.pdf"
        result = self.invoke("convert", self.input, "-o", output)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(output.exists())

    def test_image_list_assembly_used_by_hn(self):
        from pdfwutils import Colorspace, ImageFormat, convert_ImageList
        images = []
        for color in ("red", "blue"):
            data = BytesIO()
            Image.new("RGB", (64, 48), color).save(data, format="JPEG")
            images.append((Colorspace.RGB, (300, 300), ImageFormat.JPEG, data.getvalue(), 64, 48, [], False, 8, 0))
        output = self.folder / "图像合成 两页.pdf"
        output.write_bytes(convert_ImageList(images))
        self.assert_pdf(output)


if __name__ == "__main__":
    unittest.main(argv=[sys.argv[0], *remaining], verbosity=2)

"""Portable entry point for FormatFactory's pinned caj2pdf engine."""
from __future__ import annotations
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent
APP = ROOT / "app"
sys.dont_write_bytecode = True
sys.path.insert(0, str(ROOT / "Lib" / "site-packages"))
sys.path.insert(0, str(APP))
VERSION = "caj2pdf 6c4bc32b15ce748d211f45d536f5d5511ef9f368 (FormatFactory portable)"


def suppress_windows_error_dialogs():
    """Let missing native dependencies return a failure instead of a Windows dialog."""
    if os.name == "nt":
        from ctypes import WinDLL, c_uint
        kernel32 = WinDLL("kernel32", use_last_error=True)
        kernel32.GetErrorMode.restype = c_uint
        kernel32.SetErrorMode.argtypes = [c_uint]
        kernel32.SetErrorMode.restype = c_uint
        # These flags are inherited by the bundled native helper subprocesses.
        kernel32.SetErrorMode(kernel32.GetErrorMode() | 0x0001 | 0x0002 | 0x8000)


def mutool_output(arguments, **kwargs):
    """Only run the bundled helper, without a shell or a visible console."""
    if not isinstance(arguments, (list, tuple)) or not arguments or arguments[0] != "mutool":
        raise RuntimeError("Unsupported CAJ helper command")
    kwargs["shell"] = False
    kwargs["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    return subprocess.check_output([str(ROOT / "mutool.exe"), *arguments[1:]], **kwargs)


def check_dependencies():
    for relative in ("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"):
        if not (ROOT / relative).is_file():
            raise RuntimeError(f"Missing bundled Microsoft Visual C++ dependency: {relative}")
    for relative in (
        "python312.dll", "python312.zip", "python312._pth", "mutool.exe",
        "app/cajparser.py", "app/HNParsePage.py", "app/utils.py", "app/pdfwutils.py",
        "app/lib/bin/libjbigdec-w64.dll", "app/lib/bin/libjbig2codec-w64.dll",
        "Lib/site-packages/PyPDF2/__init__.py", "Lib/site-packages/imagesize.py",
        "Lib/site-packages/PIL/__init__.py",
    ):
        if not (ROOT / relative).is_file():
            raise RuntimeError(f"Missing bundled dependency: {relative}")
    from PIL import Image
    import imagesize
    import PyPDF2
    import pdfwutils
    import jbigdec
    import jbig2dec
    if PyPDF2.__version__ != "2.2.0":
        raise RuntimeError("The bundled CAJ converter requires PyPDF2 2.2.0")
    Image.new("RGB", (1, 1)).tobytes()
    if not callable(jbigdec.jbigDecode) or not callable(jbig2dec.decode_jbig2data_c):
        raise RuntimeError("The bundled JBIG/JBIG2 libraries are incomplete")
    result = mutool_output(["mutool", "-v"], stderr=subprocess.STDOUT, timeout=15)
    if b"mutool" not in result.lower():
        raise RuntimeError("The bundled mutool did not report its version")
    return result.decode("utf-8", errors="replace").strip()


def convert(input_path: Path, output_path: Path):
    import cajparser
    from PyPDF2 import PdfReader
    cajparser.check_output = mutool_output
    if not input_path.is_file():
        raise ValueError(f"Input file does not exist: {input_path}")
    if input_path == output_path:
        raise ValueError("Input and output must be different files")
    if not output_path.parent.is_dir():
        raise ValueError(f"Output directory does not exist: {output_path.parent}")
    parser = cajparser.CAJParser(str(input_path))
    if parser.format not in {"CAJ", "HN", "C8", "PDF", "KDH"}:
        raise ValueError(f"Unsupported file type: {parser.format}")
    scratch_pdf = Path.cwd() / "converted.pdf"
    parser.convert(str(scratch_pdf))
    if not scratch_pdf.is_file() or scratch_pdf.stat().st_size == 0:
        raise RuntimeError("The CAJ converter did not produce a PDF")
    with scratch_pdf.open("rb") as document:
        if len(PdfReader(document, strict=False).pages) < 1:
            raise RuntimeError("The converted PDF contains no pages")
    # Publish a validated result on the output filesystem, preserving an existing
    # destination when conversion fails.
    descriptor, staged_name = tempfile.mkstemp(prefix=".caj2pdf-", suffix=".pdf", dir=output_path.parent)
    os.close(descriptor)
    try:
        shutil.copyfile(scratch_pdf, staged_name)
        os.replace(staged_name, output_path)
    finally:
        if os.path.exists(staged_name):
            os.unlink(staged_name)


def main():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    command = argparse.ArgumentParser(description="Bundled CAJ to PDF converter")
    command.add_argument("--version", action="store_true", help="Validate all dependencies and show version")
    commands = command.add_subparsers(dest="command")
    conversion = commands.add_parser("convert")
    conversion.add_argument("input")
    conversion.add_argument("-o", "--output", required=True)
    args = command.parse_args()
    if not args.version and args.command != "convert":
        command.error("Specify --version or convert INPUT -o OUTPUT")
    input_path = Path(args.input).resolve() if args.command == "convert" else None
    output_path = Path(args.output).resolve() if args.command == "convert" else None
    previous_directory = Path.cwd()
    try:
        suppress_windows_error_dialogs()
        # Upstream writes fixed filenames. Give each
        # process a writable private workspace; never write into the MSIX package.
        with tempfile.TemporaryDirectory(prefix="FormatFactory-caj-") as scratch:
            os.chdir(scratch)
            try:
                helper_version = check_dependencies()
                if args.version:
                    print(f"{VERSION}; Python {sys.version.split()[0]}; {helper_version}")
                else:
                    convert(input_path, output_path)
            finally:
                os.chdir(previous_directory)
        return 0
    except (Exception, SystemExit) as error:
        print(f"CAJ conversion failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

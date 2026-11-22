"""Exercise the prepared Windows engine payload without PATH-installed tools.

Invoked by smoke-engines.ps1 with the bundled isolated Python. The generated
fixtures, command logs and machine-readable results remain in the output folder.
No optical media are accessed and no programs are installed.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import time
import traceback
import xml.etree.ElementTree as ET
import zipfile
import zlib

parser = argparse.ArgumentParser()
parser.add_argument('--engines', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--timeout', type=int, default=180)
args = parser.parse_args()
ROOT = args.engines.resolve(strict=True)
OUT = args.output.resolve()
OUT.mkdir(parents=True, exist_ok=True)
RUN = OUT / (datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + str(os.getpid()))
RUN.mkdir()
ENV = os.environ.copy()
# LibreOffice's Windows launcher requires PATH to be nonempty. Limit it to the
# OS directory so DLL startup works without discovering installed converters.
SYSTEM_PATH = str(Path(os.environ['SystemRoot']) / 'System32')
ENV.update(PATH=SYSTEM_PATH, PYTHONPATH='', PYTHONNOUSERSITE='1', PYTHONDONTWRITEBYTECODE='1', QT_QPA_PLATFORM='offscreen',
           CALIBRE_CONFIG_DIRECTORY=str(RUN / 'calibre-profile'), CALIBRE_CACHE_DIRECTORY=str(RUN / 'calibre-cache'),
           CALIBRE_TEMP_DIR=str(RUN / 'calibre-temp'))
# The packaged engine is read-only. Skip LibreOffice's OpenCL probe, which
# creates a lock beside the bundled test spreadsheet in writable staging.
ENV['SAL_DISABLE_OPENCL'] = '1'
(RUN / 'calibre-temp').mkdir()
RESULTS = []
COMMANDS = []
MARKER = 'Bundled engine smoke'
CHINESE = '中文验证'
TITLE = 'Portable Engine Book'


def engine(relative):
    path = (ROOT / relative).resolve(strict=True)
    if not path.is_relative_to(ROOT):
        raise AssertionError(f'Executable is outside the bundle: {path}')
    return path


def invoke(label, executable, *arguments):
    started = time.monotonic()
    command = [str(executable), *map(str, arguments)]
    process = subprocess.Popen(command, cwd=RUN, env=ENV, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE,
                               creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    try:
        stdout, stderr = process.communicate(timeout=args.timeout)
    except subprocess.TimeoutExpired as exc:
        # A LibreOffice launcher can own a child process. Stop only this test's
        # process tree so a failed smoke run cannot leave a background converter.
        taskkill = Path(os.environ['SystemRoot']) / 'System32' / 'taskkill.exe'
        subprocess.run([str(taskkill), '/PID', str(process.pid), '/T', '/F'],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=15,
                       creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        if process.poll() is None:
            process.kill()
        (RUN / f'{label}.stdout.log').write_bytes(exc.stdout or b'')
        (RUN / f'{label}.stderr.log').write_bytes(exc.stderr or b'')
        raise AssertionError(f'{label} exceeded {args.timeout} seconds') from exc
    result = subprocess.CompletedProcess(command, process.returncode, stdout, stderr)
    (RUN / f'{label}.stdout.log').write_bytes(result.stdout)
    (RUN / f'{label}.stderr.log').write_bytes(result.stderr)
    COMMANDS.append(dict(label=label, command=command, exitCode=result.returncode,
                         seconds=round(time.monotonic() - started, 3)))
    if result.returncode:
        raise AssertionError(f'{label} exited {result.returncode}: ' +
                             (result.stderr + result.stdout).decode('utf-8', errors='replace')[-3000:])
    return result.stdout


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def write_zip(path, entries):
    with zipfile.ZipFile(path, 'w') as archive:
        for name, value in entries.items():
            archive.writestr(name, value, compress_type=zipfile.ZIP_STORED if name == 'mimetype' else zipfile.ZIP_DEFLATED)


def png_image():
    def chunk(name, data):
        return struct.pack('>I', len(data)) + name + data + struct.pack('>I', zlib.crc32(name + data) & 0xffffffff)
    pixels = b''.join(b'\0' + b''.join(bytes((255 if x < 32 else 30, 50 if y < 32 else 200, 100))
                                        for x in range(64)) for y in range(64))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 64, 64, 8, 2, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b''))


def fixtures():
    image = png_image()
    (RUN / 'smoke-image.png').write_bytes(image)
    document = f'''<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
 xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
 xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
 xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
 xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"
 xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart">
<w:body><w:p><w:r><w:rPr><w:rFonts w:ascii="Arial" w:eastAsia="Microsoft YaHei"/><w:sz w:val="24"/></w:rPr><w:t>{MARKER} {CHINESE}</w:t></w:r></w:p>
<w:p><w:r><w:drawing><wp:inline><wp:extent cx="914400" cy="914400"/><wp:docPr id="1" name="Smoke image"/>
<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="smoke-image.png"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="rIdImage"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
<w:p><w:r><w:drawing><wp:inline><wp:extent cx="4572000" cy="2286000"/><wp:docPr id="2" name="Smoke chart"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart r:id="rIdChart"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="720" w:right="720" w:bottom="720" w:left="720"/></w:sectPr></w:body></w:document>'''
    chart = '''<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><c:chart><c:autoTitleDeleted val="1"/><c:plotArea><c:layout/><c:barChart><c:barDir val="col"/><c:grouping val="clustered"/><c:ser><c:idx val="0"/><c:order val="0"/><c:tx><c:v>Smoke Series</c:v></c:tx><c:spPr><a:solidFill><a:srgbClr val="4472C4"/></a:solidFill><a:ln><a:noFill/></a:ln></c:spPr><c:cat><c:strLit><c:ptCount val="3"/><c:pt idx="0"><c:v>Chart Alpha</c:v></c:pt><c:pt idx="1"><c:v>Chart Beta</c:v></c:pt><c:pt idx="2"><c:v>Chart Gamma</c:v></c:pt></c:strLit></c:cat><c:val><c:numLit><c:formatCode>General</c:formatCode><c:ptCount val="3"/><c:pt idx="0"><c:v>3</c:v></c:pt><c:pt idx="1"><c:v>7</c:v></c:pt><c:pt idx="2"><c:v>5</c:v></c:pt></c:numLit></c:val></c:ser><c:gapWidth val="100"/><c:axId val="10"/><c:axId val="20"/></c:barChart><c:catAx><c:axId val="10"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:axPos val="b"/><c:tickLblPos val="nextTo"/><c:crossAx val="20"/><c:crosses val="autoZero"/><c:lblAlgn val="ctr"/><c:lblOffset val="100"/></c:catAx><c:valAx><c:axId val="20"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:axPos val="l"/><c:majorGridlines/><c:numFmt formatCode="0" sourceLinked="0"/><c:tickLblPos val="nextTo"/><c:crossAx val="10"/><c:crosses val="autoZero"/><c:crossBetween val="between"/></c:valAx></c:plotArea><c:plotVisOnly val="1"/><c:dispBlanksAs val="gap"/></c:chart></c:chartSpace>'''
    docx = RUN / '中文 图表样例.docx'
    write_zip(docx, {
        '[Content_Types].xml': '''<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/charts/chart1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawingml.chart+xml"/></Types>''',
        '_rels/.rels': '''<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>''',
        'word/document.xml': document,
        'word/_rels/document.xml.rels': '''<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdImage" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/><Relationship Id="rIdChart" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart" Target="charts/chart1.xml"/></Relationships>''',
        'word/media/image1.png': image, 'word/charts/chart1.xml': chart,
    })
    epub = RUN / '中文 电子书样例.epub'
    write_zip(epub, {
        'mimetype': 'application/epub+zip',
        'META-INF/container.xml': '''<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>''',
        'OEBPS/content.opf': f'''<?xml version="1.0" encoding="utf-8"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:uuid:72b15639-e3cc-41a3-a682-391ad7e6d8af</dc:identifier><dc:title>{TITLE}</dc:title><dc:creator>Engine Smoke Test</dc:creator><dc:language>zh-CN</dc:language><meta property="dcterms:modified">2026-09-23T00:00:00Z</meta></metadata><manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="image" href="image.png" media-type="image/png"/></manifest><spine><itemref idref="chapter"/></spine></package>''',
        'OEBPS/chapter.xhtml': f'''<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>{TITLE}</title></head><body><h1>{TITLE}</h1><p>{MARKER} {CHINESE}</p><p><img src="image.png" alt="Smoke image"/></p></body></html>''',
        'OEBPS/nav.xhtml': '''<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="chapter.xhtml">Chapter one</a></li></ol></nav></body></html>''',
        'OEBPS/image.png': image,
    })
    return docx, epub


def office_convert(label, source, extension, conversion_filter):
    output = RUN / label
    output.mkdir()
    profile = (output / 'profile').as_uri() + '/'
    invoke(label, engine('libreoffice/program/soffice.com'), '-env:UserInstallation=' + profile,
           '--headless', '--nologo', '--nodefault', '--nofirststartwizard', '--nolockcheck',
           '--convert-to', conversion_filter, '--outdir', output, source)
    artifact = output / (source.stem + extension)
    require(artifact.is_file() and artifact.stat().st_size > 100, f'{label} produced no readable output')
    return artifact


def validate_pdf(label, pdf):
    require(pdf.read_bytes().startswith(b'%PDF-'), 'Office output does not have a PDF header')
    text = RUN / (label + '.txt')
    invoke(label + '-text', engine('caj2pdf/mutool.exe'), 'draw', '-F', 'txt', '-o', text, pdf)
    contents = text.read_text(encoding='utf-8')
    for expected in (MARKER, CHINESE, 'Chart Alpha', 'Chart Beta', 'Chart Gamma'):
        require(expected in contents, f'PDF dropped expected text: {expected}')
    trace = RUN / (label + '-trace.xml')
    invoke(label + '-trace', engine('caj2pdf/mutool.exe'), 'draw', '-F', 'trace', '-o', trace, pdf)
    drawing = trace.read_text(encoding='utf-8')
    require('<fill_image ' in drawing, 'PDF dropped the inline PNG image')
    require('<fill_path ' in drawing, 'PDF has no filled chart paths')


def office_smoke(docx):
    with ThreadPoolExecutor(max_workers=2) as pool:
        jobs = [pool.submit(office_convert, f'office-concurrent-{n}', docx, '.pdf', 'pdf') for n in (1, 2)]
        pdfs = [job.result() for job in jobs]
    for n, pdf in enumerate(pdfs, 1):
        validate_pdf(f'office-concurrent-{n}-validation', pdf)
    doc = office_convert('office-doc-fixture', docx, '.doc', 'doc:MS Word 97')
    require(doc.read_bytes()[:8] == bytes.fromhex('d0cf11e0a1b11ae1'), 'DOC fixture is not a compound binary document')
    roundtrip = office_convert('office-doc-to-docx', doc, '.docx', 'docx:Office Open XML Text')
    with zipfile.ZipFile(roundtrip) as archive:
        xml = ET.fromstring(archive.read('word/document.xml'))
        text = ''.join(xml.itertext())
        require(MARKER in text and CHINESE in text, 'DOC to DOCX lost document text')
        require(any(name.startswith('word/media/') for name in archive.namelist()), 'DOC to DOCX lost the embedded image')
    # Check the legacy Word import and subsequent PDF export, too.
    validate_pdf('office-doc-validation', office_convert('office-doc-to-pdf', doc, '.pdf', 'pdf'))
    return dict(concurrentPdfConversions=2, verifiedText=[MARKER, CHINESE, 'Chart Alpha', 'Chart Beta', 'Chart Gamma'],
                pdfImageAndChartVerified=True, legacyDocToDocx=True, legacyDocToPdf=True)


def ebook_smoke(epub):
    details = []
    for extension in ('mobi', 'azw3'):
        output = RUN / ('converted-book.' + extension)
        invoke('calibre-' + extension, engine('calibre/ebook-convert.exe'), epub, output, '--title', TITLE)
        require(output.is_file() and output.stat().st_size > 100, f'Calibre created no {extension} book')
        header = output.read_bytes()[:68]
        require(header[60:68] == b'BOOKMOBI', f'{extension} is not a Palm/MOBI container')
        metadata = invoke('calibre-meta-' + extension, engine('calibre/ebook-meta.exe'), output)
        require(TITLE.encode() in metadata, f'ebook-meta could not read the {extension} title')
        details.append(dict(format=extension, bytes=output.stat().st_size, metadataTitle=TITLE))
    return details


def ffmpeg_smoke():
    ffmpeg = engine('ffmpeg/ffmpeg.exe')
    ffprobe = engine('ffmpeg/ffprobe.exe')
    source, output = RUN / 'source.mkv', RUN / 'converted.mp4'
    invoke('ffmpeg-source', ffmpeg, '-hide_banner', '-nostdin', '-y', '-f', 'lavfi', '-i',
           'testsrc2=size=160x96:rate=10', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000',
           '-t', '1.2', '-c:v', 'ffv1', '-c:a', 'pcm_s16le', source)
    invoke('ffmpeg-convert', ffmpeg, '-hide_banner', '-nostdin', '-y', '-i', source, '-c:v', 'libx264',
           '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-movflags', '+faststart', output)
    probe = json.loads(invoke('ffprobe-output', ffprobe, '-v', 'error', '-show_streams', '-show_format',
                             '-of', 'json', output))
    video = next((stream for stream in probe['streams'] if stream['codec_type'] == 'video'), {})
    audio = next((stream for stream in probe['streams'] if stream['codec_type'] == 'audio'), {})
    require(video.get('codec_name') == 'h264' and video.get('width') == 160 and video.get('height') == 96,
            'FFmpeg output has an unexpected video stream')
    require(audio.get('codec_name') == 'aac' and int(audio.get('sample_rate', 0)) == 48000,
            'FFmpeg output has an unexpected audio stream')
    require(1 <= float(probe['format']['duration']) <= 2, 'FFmpeg output duration is invalid')
    invoke('ffmpeg-decode-check', ffmpeg, '-hide_banner', '-nostdin', '-v', 'error', '-i', output,
           '-map', '0:v', '-map', '0:a', '-f', 'null', '-')
    return dict(video='h264', width=160, height=96, audio='aac', sampleRate=48000,
                duration=probe['format']['duration'], decodeVerified=True)


def payload_snapshot():
    snapshot = {}
    for path in sorted(ROOT.rglob('*')):
        if path.is_file():
            digest = hashlib.sha256()
            with path.open('rb') as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(block)
            snapshot[path.relative_to(ROOT).as_posix()] = digest.hexdigest()
    return snapshot


def main():
    require(sys.platform == 'win32', 'These smoke tests target the Windows payload')
    original_payload = payload_snapshot()
    docx, epub = fixtures()
    for name, action in [('ffmpeg', ffmpeg_smoke), ('libreoffice', lambda: office_smoke(docx)),
                         ('calibre', lambda: ebook_smoke(epub))]:
        start = time.monotonic()
        try:
            details = action()
            RESULTS.append(dict(engine=name, status='passed', details=details, seconds=round(time.monotonic()-start, 3)))
            print(f'PASS {name}', flush=True)
        except Exception as exc:
            RESULTS.append(dict(engine=name, status='failed', error=str(exc), seconds=round(time.monotonic()-start, 3)))
            traceback.print_exc()
            print(f'FAIL {name}: {exc}', flush=True)
    final_payload = payload_snapshot()
    changed = sorted(path for path in original_payload.keys() | final_payload.keys()
                     if original_payload.get(path) != final_payload.get(path))
    RESULTS.append(dict(engine='payload-immutable', status='failed' if changed else 'passed',
                        filesVerified=len(original_payload), changedFiles=changed))
    print(('FAIL' if changed else 'PASS') + ' payload-immutable', flush=True)
    report = dict(schemaVersion=1, completedAt=datetime.now(timezone.utc).isoformat(), engines=str(ROOT),
                  workingDirectory=str(RUN), externalToolSearchPathDisabled=True, permittedPathEntries=[SYSTEM_PATH], hostArchitecture=os.environ.get('PROCESSOR_ARCHITECTURE'),
                  results=RESULTS, commands=COMMANDS, fixtures=[dict(path=str(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest())
                                                             for path in (docx, epub)])
    report_path = RUN / 'results.json'
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
    (OUT / 'latest-results.json').write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
    print('Results: ' + str(report_path), flush=True)
    return 0 if all(result['status'] == 'passed' for result in RESULTS) else 1


if __name__ == '__main__':
    raise SystemExit(main())

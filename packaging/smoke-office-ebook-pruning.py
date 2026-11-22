"""Compare conversion-only LibreOffice/Calibre payloads using generated real files.

Run with the prepared caj2pdf/python.exe -I -B. Only Python's standard library is
used. Engine processes have a System32-only PATH and profiles outside payloads.
The observer (mutool and Calibre roundtrip reader) is held constant across A/B.
"""
from __future__ import annotations

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import posixpath
import re
import struct
import subprocess
import sys
import time
import traceback
import uuid
import xml.etree.ElementTree as ET
import zipfile
import zlib

MARKER = 'PRUNING 中文图文保真'
PAGE_TWO = 'SECOND PAGE 分页验证'
TABLE_MARKERS = ('Table Alpha', 'Table Beta', '表格中文')
CHART_MARKERS = ('Chart Alpha', 'Chart Beta', 'Chart Gamma')
TITLE = 'PRUNING BOOK 中文电子书'
LINK = 'https://example.com/pruning'
W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def compact(text):
    return ''.join(text.split())


def digest(path):
    sha = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            sha.update(block)
    return sha.hexdigest()


def write_zip(path, entries):
    with zipfile.ZipFile(path, 'w') as archive:
        for name, value in entries.items():
            archive.writestr(name, value, compress_type=zipfile.ZIP_STORED if name == 'mimetype' else zipfile.ZIP_DEFLATED)


def png_image():
    def chunk(name, value):
        return struct.pack('>I', len(value)) + name + value + struct.pack('>I', zlib.crc32(name + value) & 0xffffffff)
    pixels = b''.join(b'\0' + b''.join(bytes((255 if x < 40 else 20, 40 if y < 30 else 220, 90))
                                       for x in range(80)) for y in range(60))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 80, 60, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b'')


def fixtures(root, font):
    root.mkdir()
    image = png_image()
    (root / '中文 图片.png').write_bytes(image)
    para = lambda text: '<w:p><w:r><w:rPr><w:rFonts w:ascii="Arial" w:eastAsia="Microsoft YaHei"/><w:sz w:val="24"/></w:rPr><w:t>' + text + '</w:t></w:r></w:p>'
    chart_points = ''.join(f'<c:pt idx="{i}"><c:v>{name}</c:v></c:pt>' for i, name in enumerate(CHART_MARKERS))
    chart_values = ''.join(f'<c:pt idx="{i}"><c:v>{value}</c:v></c:pt>' for i, value in enumerate((3, 7, 5)))
    chart = f'''<c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><c:chart><c:autoTitleDeleted val="1"/><c:plotArea><c:layout/><c:barChart><c:barDir val="col"/><c:grouping val="clustered"/><c:ser><c:idx val="0"/><c:order val="0"/><c:tx><c:v>Pruning Series</c:v></c:tx><c:spPr><a:solidFill><a:srgbClr val="4472C4"/></a:solidFill></c:spPr><c:cat><c:strLit><c:ptCount val="3"/>{chart_points}</c:strLit></c:cat><c:val><c:numLit><c:formatCode>General</c:formatCode><c:ptCount val="3"/>{chart_values}</c:numLit></c:val></c:ser><c:gapWidth val="100"/><c:axId val="10"/><c:axId val="20"/></c:barChart><c:catAx><c:axId val="10"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:axPos val="b"/><c:tickLblPos val="nextTo"/><c:crossAx val="20"/><c:crosses val="autoZero"/><c:lblAlgn val="ctr"/><c:lblOffset val="100"/></c:catAx><c:valAx><c:axId val="20"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:axPos val="l"/><c:majorGridlines/><c:numFmt formatCode="0" sourceLinked="0"/><c:tickLblPos val="nextTo"/><c:crossAx val="10"/><c:crosses val="autoZero"/><c:crossBetween val="between"/></c:valAx></c:plotArea><c:plotVisOnly val="1"/></c:chart></c:chartSpace>'''
    drawing = '''<w:p><w:r><w:drawing><wp:inline><wp:extent cx="1828800" cy="1371600"/><wp:docPr id="1" name="Fixture picture"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="image.png"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="rImage"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="1828800" cy="1371600"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>'''
    chart_drawing = '''<w:p><w:r><w:drawing><wp:inline><wp:extent cx="4572000" cy="2286000"/><wp:docPr id="2" name="Native chart"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart r:id="rChart"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>'''
    table = '<w:tbl><w:tblPr><w:tblW w:w="7000" w:type="dxa"/><w:tblBorders><w:top w:val="single" w:sz="8"/><w:bottom w:val="single" w:sz="8"/><w:insideH w:val="single" w:sz="8"/><w:insideV w:val="single" w:sz="8"/></w:tblBorders></w:tblPr><w:tblGrid><w:gridCol w:w="3500"/><w:gridCol w:w="3500"/></w:tblGrid>' + ''.join('<w:tr>' + ''.join('<w:tc><w:tcPr><w:tcW w:w="3500" w:type="dxa"/></w:tcPr>' + para(value) + '</w:tc>' for value in row) + '</w:tr>' for row in ((TABLE_MARKERS[0], TABLE_MARKERS[2]), (TABLE_MARKERS[1], '123.45'))) + '</w:tbl>'
    document = f'''<w:document xmlns:w="{W}" xmlns:r="{R}" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"><w:body>{para(MARKER)}{drawing}{table}<w:p><w:hyperlink r:id="rLink"><w:r><w:t>External hyperlink 中文链接</w:t></w:r></w:hyperlink></w:p><w:p><w:r><w:br w:type="page"/></w:r></w:p>{para(PAGE_TWO)}{chart_drawing}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="720" w:right="720" w:bottom="720" w:left="720"/></w:sectPr></w:body></w:document>'''
    docx = root / '中文 图表 分页.docx'
    write_zip(docx, {
        '[Content_Types].xml': '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/charts/chart1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawingml.chart+xml"/></Types>',
        '_rels/.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rDoc" Type="' + R + '/officeDocument" Target="word/document.xml"/></Relationships>',
        'word/document.xml': document,
        'word/_rels/document.xml.rels': f'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rImage" Type="{R}/image" Target="media/image.png"/><Relationship Id="rChart" Type="{R}/chart" Target="charts/chart1.xml"/><Relationship Id="rLink" Type="{R}/hyperlink" Target="{LINK}" TargetMode="External"/></Relationships>',
        'word/media/image.png': image, 'word/charts/chart1.xml': chart,
    })
    with zipfile.ZipFile(docx) as archive:
        docx_entries = {name: archive.read(name) for name in archive.namelist()}
    docx_entries['word/document.xml'] = document.replace('<w:sectPr>', '<w:sectPr><w:headerReference w:type="default" r:id="rHeader"/><w:footerReference w:type="default" r:id="rFooter"/>')
    docx_entries['word/header1.xml'] = f'<w:hdr xmlns:w="{W}">{para("HEADER 页眉验证")}</w:hdr>'
    docx_entries['word/footer1.xml'] = f'<w:ftr xmlns:w="{W}">{para("FOOTER 页脚验证")}</w:ftr>'
    docx_entries['word/_rels/document.xml.rels'] = docx_entries['word/_rels/document.xml.rels'].decode().replace('</Relationships>', f'<Relationship Id="rHeader" Type="{R}/header" Target="header1.xml"/><Relationship Id="rFooter" Type="{R}/footer" Target="footer1.xml"/></Relationships>')
    docx_entries['[Content_Types].xml'] = docx_entries['[Content_Types].xml'].decode().replace('</Types>', '<Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/><Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/></Types>')
    write_zip(docx, docx_entries)
    chapter = f'<html xmlns="http://www.w3.org/1999/xhtml"><head><title>{TITLE}</title><link rel="stylesheet" href="style.css" type="text/css"/></head><body><h1 id="chapter-one">Chapter One 第一章</h1><p>{MARKER}</p><img src="image.png" alt="Fixture image"/><table><tr><td>Table Alpha</td><td>表格中文</td></tr><tr><td>Table Beta</td><td>123.45</td></tr></table><p><a href="{LINK}">External link</a></p><p><a href="chapter2.xhtml#chapter-two">Next chapter</a></p></body></html>'
    chapter_two = f'<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter Two</title></head><body><h1 id="chapter-two">Chapter Two 第二章</h1><p>{PAGE_TWO}</p><p>Ending marker 完结保留</p></body></html>'
    epub = root / '中文 无封面 电子书.epub'
    epub_entries = {
        'mimetype': 'application/epub+zip',
        'META-INF/container.xml': '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>',
        'OEBPS/content.opf': f'<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:uuid:342a7dab-7839-41e1-97db-92e4e9df79ab</dc:identifier><dc:title>{TITLE}</dc:title><dc:creator>Pruning Verification</dc:creator><dc:language>zh-CN</dc:language><meta property="dcterms:modified">2026-10-25T00:00:00Z</meta></metadata><manifest><item id="one" href="chapter.xhtml" media-type="application/xhtml+xml"/><item id="two" href="chapter2.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="image" href="image.png" media-type="image/png"/></manifest><spine><itemref idref="one"/><itemref idref="two"/></spine></package>',
        'OEBPS/chapter.xhtml': chapter, 'OEBPS/chapter2.xhtml': chapter_two,
        'OEBPS/nav.xhtml': '<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="chapter.xhtml#chapter-one">Chapter One 第一章</a></li><li><a href="chapter2.xhtml#chapter-two">Chapter Two 第二章</a></li></ol></nav></body></html>',
        'OEBPS/image.png': image,
    }
    epub_entries['OEBPS/content.opf'] = epub_entries['OEBPS/content.opf'].replace('</manifest>', '<item id="css" href="style.css" media-type="text/css"/><item id="font" href="font.ttf" media-type="font/ttf"/></manifest>')
    epub_entries['OEBPS/style.css'] = '@font-face{font-family:PruningLatin;src:url(font.ttf)} body{font-family:PruningLatin,serif} table{border-collapse:collapse} td{border:1px solid #135;padding:.3em} h1{color:#135}'
    epub_entries['OEBPS/font.ttf'] = font.read_bytes()
    write_zip(epub, epub_entries)
    svg_epub = root / '中文 SVG封面 电子书.epub'
    svg_entries = dict(epub_entries)
    svg = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="600" viewBox="0 0 400 600"><rect width="400" height="600" fill="#163e68"/><circle cx="200" cy="210" r="110" fill="#f0aa22"/><text x="20" y="400" font-size="28" fill="white">PRUNING SVG COVER</text></svg>'
    svg_entries['OEBPS/cover.svg'] = svg
    svg_entries['OEBPS/illustration.svg'] = svg.replace('PRUNING SVG COVER', 'INLINE SVG DRAWING')
    svg_entries['OEBPS/content.opf'] = svg_entries['OEBPS/content.opf'].replace('</metadata>', '<meta name="cover" content="svg-cover"/></metadata>').replace('</manifest>', '<item id="svg-cover" href="cover.svg" media-type="image/svg+xml" properties="cover-image"/><item id="svg-inline" href="illustration.svg" media-type="image/svg+xml"/></manifest>')
    svg_entries['OEBPS/chapter.xhtml'] = chapter.replace('<img src="image.png"', '<img src="illustration.svg" alt="Inline SVG"/><img src="image.png"')
    svg_entries['OEBPS/cover.xhtml'] = '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>SVG Cover</title></head><body><svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="400" height="600" viewBox="0 0 400 600"><image width="400" height="600" xlink:href="cover.svg"/></svg></body></html>'
    svg_entries['OEBPS/content.opf'] = svg_entries['OEBPS/content.opf'].replace('</manifest>', '<item id="cover-page" href="cover.xhtml" media-type="application/xhtml+xml"/></manifest>').replace('<spine>', '<spine><itemref idref="cover-page" linear="no"/>').replace('</package>', '<guide><reference type="cover" title="Cover" href="cover.xhtml"/></guide></package>')
    write_zip(svg_epub, svg_entries)
    # Supplemental inputs document the capability boundary; these are not product
    # routes and do not become preservation requirements for conversion-only Calibre.
    (root / '中文 扩展输入.html').write_text(chapter.replace('src="image.png"', 'src="中文 图片.png"'), encoding='utf-8')
    (root / '中文 扩展输入.fb2').write_text(f'<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink"><description><title-info><genre>nonfiction</genre><author><first-name>Pruning</first-name><last-name>Verification</last-name></author><book-title>{TITLE}</book-title><lang>zh</lang></title-info></description><body><section><title><p>Chapter One 第一章</p></title><p>{MARKER}</p><image l:href="#image"/><p><a l:href="{LINK}">External link</a></p></section><section><title><p>Chapter Two 第二章</p></title><p>{PAGE_TWO}</p></section></body><binary id="image" content-type="image/png">{base64.b64encode(image).decode()}</binary></FictionBook>', encoding='utf-8')
    return docx, epub, svg_epub


class Comparison:
    def __init__(self, args):
        self.args = args
        self.sides = ('before', 'after') if args.before else ('after',)
        self.roots = {'after': args.after.resolve(strict=True)}
        self.roots['before'] = args.before.resolve(strict=True) if args.before else self.roots['after']
        output = args.output.resolve()
        require(all(not output.is_relative_to(root) for root in self.roots.values()), 'Output must be outside both engine payloads')
        output.mkdir(parents=True, exist_ok=True)
        self.output = output
        self.run = output / (datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + str(os.getpid()))
        self.run.mkdir()
        self.system_path = str(Path(os.environ['SystemRoot']) / 'System32')
        self.commands = []
        self.results = []
        self.mutool = args.mutool.resolve(strict=True) if args.mutool else self.engine('before', 'caj2pdf/mutool.exe')

    def engine(self, side, relative):
        path = (self.roots[side] / relative).resolve(strict=True)
        require(path.is_relative_to(self.roots[side]), 'Engine executable resolves outside its payload')
        return path

    def invoke(self, label, executable, *arguments, language='en'):
        profile = self.run / (label + '-state')
        profile.mkdir()
        env = os.environ.copy()
        # Do not let LibreOffice's OpenCL probe write beside the bundled spreadsheet.
        env['SAL_DISABLE_OPENCL'] = '1'
        env.update(PATH=self.system_path, PYTHONPATH='', PYTHONHOME='', PYTHONNOUSERSITE='1', PYTHONDONTWRITEBYTECODE='1',
                   PYTHONIOENCODING='utf-8', QT_QPA_PLATFORM='offscreen', CALIBRE_OVERRIDE_LANG=language,
                   CALIBRE_CONFIG_DIRECTORY=str(profile / 'config'), CALIBRE_CACHE_DIRECTORY=str(profile / 'cache'),
                   CALIBRE_TEMP_DIR=str(profile / 'temp'))
        (profile / 'temp').mkdir()
        command = [str(executable), *map(str, arguments)]
        started = time.monotonic()
        process = subprocess.Popen(command, cwd=self.run, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        timed_out = False
        try:
            stdout, stderr = process.communicate(timeout=self.args.timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            subprocess.run([str(Path(self.system_path) / 'taskkill.exe'), '/PID', str(process.pid), '/T', '/F'],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20,
                           creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            if process.poll() is None:
                process.kill()
            stdout, stderr = process.communicate()
        finally:
            self.commands.append(dict(label=label, command=command, exitCode=process.poll(), seconds=round(time.monotonic()-started, 3)))
        (self.run / (label + '.stdout.log')).write_bytes(stdout)
        (self.run / (label + '.stderr.log')).write_bytes(stderr)
        require(not timed_out, f'{label} exceeded {self.args.timeout} seconds')
        require(process.returncode == 0, f'{label} exited {process.returncode}: {(stderr + stdout).decode("utf-8", errors="replace")[-3000:]}')
        return stdout

    def office(self, side, label, source, extension, conversion_filter):
        label = side + '-' + label
        target = self.run / label
        target.mkdir()
        self.invoke(label, self.engine(side, 'libreoffice/program/soffice.com'),
                    '-env:UserInstallation=' + (target / 'profile').as_uri() + '/', '--headless', '--nologo',
                    '--nodefault', '--nofirststartwizard', '--nolockcheck', '--convert-to', conversion_filter,
                    '--outdir', target, source)
        artifact = target / (source.stem + extension)
        require(artifact.is_file() and artifact.stat().st_size > 100, f'{label} produced no usable output')
        return artifact

    def pdf_snapshot(self, label, pdf):
        target = self.run / (label + '-inspection')
        target.mkdir()
        text_path, trace_path = target / 'text.txt', target / 'trace.xml'
        self.invoke(label + '-text', self.mutool, 'draw', '-F', 'txt', '-o', text_path, pdf)
        self.invoke(label + '-trace', self.mutool, 'draw', '-F', 'trace', '-o', trace_path, pdf)
        text = text_path.read_text(encoding='utf-8')
        for marker in (MARKER, PAGE_TWO, *TABLE_MARKERS, *CHART_MARKERS, 'HEADER 页眉验证', 'FOOTER 页脚验证'):
            require(compact(marker) in compact(text), f'{label} PDF lost text: {marker}')
        trace = ET.parse(trace_path).getroot()
        pages = trace.findall('.//page')
        if trace.tag == 'page':
            pages.insert(0, trace)
        require(len(pages) >= 2, f'{label} lost the explicit page break')
        images = sum(1 for item in trace.iter() if item.tag == 'fill_image')
        paths = sum(1 for item in trace.iter() if item.tag == 'fill_path')
        require(images > 0 and paths > 0, f'{label} lost the inline image or chart drawing')
        objects = self.invoke(label + '-objects', self.mutool, 'show', pdf, 'grep').decode('utf-8', errors='replace')
        require(LINK in objects, f'{label} lost the external hyperlink')
        self.invoke(label + '-render', self.mutool, 'draw', '-F', 'ppm', '-r', '72', '-o', target / 'page-%03d.ppm', pdf)
        renders = [dict(bytes=path.stat().st_size, sha256=digest(path)) for path in sorted(target.glob('page-*.ppm'))]
        require(len(renders) == len(pages), f'{label} could not render every PDF page')
        snapshot = dict(text=compact(text), pageCount=len(pages), imageDraws=images, filledPaths=paths, externalLink=LINK, renders=renders)
        (target / 'semantic.json').write_text(json.dumps(snapshot, ensure_ascii=False, indent=2), encoding='utf-8')
        return snapshot

    def docx_snapshot(self, path):
        with zipfile.ZipFile(path) as archive:
            xml = ET.fromstring(archive.read('word/document.xml'))
            text = ''.join(item.text or '' for item in xml.iter('{' + W + '}t'))
            for marker in (MARKER, PAGE_TWO, *TABLE_MARKERS):
                require(compact(marker) in compact(text), f'Normalized DOCX dropped {marker}')
            links = []
            for name in archive.namelist():
                if name.endswith('.rels'):
                    links.extend(item.attrib.get('Target') for item in ET.fromstring(archive.read(name)) if item.attrib.get('TargetMode') == 'External')
            require(LINK in links, 'Normalized DOCX dropped the hyperlink')
            media = [digest_bytes(archive.read(name)) for name in archive.namelist() if name.startswith('word/media/') and not name.endswith('/')]
            require(media, 'Normalized DOCX dropped the embedded image')
            return dict(text=compact(text), tables=len(xml.findall('.//{' + W + '}tbl')), media=sorted(media), externalLinks=sorted(links))

    def ebook(self, side, label, source, extension, language='en'):
        label = side + '-' + label
        output = self.run / (label + '.' + extension)
        options = ['--no-default-epub-cover'] if extension == 'epub' else []
        self.invoke(label, self.engine(side, 'calibre/ebook-convert.exe'), source, output, *options, language=language)
        require(output.is_file() and output.stat().st_size > 100, f'{label} produced no ebook')
        return output

    def ebook_snapshot(self, side, label, book):
        require(book.read_bytes()[60:68] == b'BOOKMOBI', f'{label} is not a MOBI-family container')
        opf = self.run / (side + '-' + label + '-metadata.opf')
        self.invoke(side + '-' + label + '-metadata', self.engine(side, 'calibre/ebook-meta.exe'), book, '--to-opf', opf)
        metadata = ET.parse(opf).getroot()
        fields = {name: [item.text for item in metadata.iter('{http://purl.org/dc/elements/1.1/}' + name)] for name in ('title', 'creator', 'language')}
        require(TITLE in fields['title'], f'{label} lost its title metadata')
        # A fixed, unpruned observer decodes both outputs. This is an output
        # inspection mechanism, not a claim that MOBI input is a product route.
        epub = self.ebook('before', side + '-' + label + '-observation', book, 'epub')
        with zipfile.ZipFile(epub) as archive:
            container = ET.fromstring(archive.read('META-INF/container.xml'))
            opf_path = next(container.iter('{urn:oasis:names:tc:opendocument:xmlns:container}rootfile')).attrib['full-path']
            package = ET.fromstring(archive.read(opf_path))
            manifest = {item.attrib['id']: item for item in package.find('{http://www.idpf.org/2007/opf}manifest')}
            texts, links, images, fonts, navigation = [], [], [], [], []
            next_chapter_links = 0
            for ref in package.find('{http://www.idpf.org/2007/opf}spine'):
                item = manifest[ref.attrib['idref']]
                page = ET.fromstring(archive.read(posixpath.normpath(posixpath.join(posixpath.dirname(opf_path), item.attrib['href']))))
                body = page.find('{http://www.w3.org/1999/xhtml}body')
                texts.append(compact(''.join((body if body is not None else page).itertext())))
                links.extend(node.attrib['href'] for node in page.iter() if node.attrib.get('href', '').startswith('https://'))
                next_chapter_links += sum(1 for node in page.iter() if 'href' in node.attrib and
                                          compact('Next chapter') in compact(''.join(node.itertext())) and
                                          not node.attrib['href'].startswith(('http:', 'https:')))
            for item in manifest.values():
                member = posixpath.normpath(posixpath.join(posixpath.dirname(opf_path), item.attrib['href']))
                if item.attrib.get('media-type', '').startswith('image/'):
                    value = archive.read(member)
                    images.append(digest_bytes(value))
                if item.attrib['href'].lower().endswith(('.ttf', '.otf', '.woff', '.woff2')):
                    fonts.append(digest_bytes(normalize_font(archive, member, archive.read(member), package)))
                if item.attrib.get('media-type') == 'application/x-dtbncx+xml':
                    toc = ET.fromstring(archive.read(member))
                    navigation.extend(compact(''.join(node.itertext())) for node in toc.iter() if node.tag.endswith('}navLabel'))
            text = ''.join(texts)
            for marker in (MARKER, PAGE_TWO, *TABLE_MARKERS, 'Chapter One 第一章', 'Chapter Two 第二章', 'Ending marker 完结保留'):
                require(compact(marker) in text, f'{label} lost ebook content: {marker}')
            require(images and LINK in links and next_chapter_links > 0, f'{label} lost image content or external/internal link')
            require(any(compact('Chapter One 第一章') in value for value in navigation) and any(compact('Chapter Two 第二章') in value for value in navigation), f'{label} lost chapter navigation')
            return dict(metadata=fields, text=text, images=sorted(images), embeddedFonts=sorted(fonts), navigation=navigation,
                        nextChapterLinks=next_chapter_links, externalLinks=sorted(set(links)))

    def check(self, name, action):
        started = time.monotonic()
        try:
            details = action()
            self.results.append(dict(name=name, status='passed', seconds=round(time.monotonic()-started, 3), details=details))
            print('PASS ' + name, flush=True)
        except Exception as exc:
            self.results.append(dict(name=name, status='failed', seconds=round(time.monotonic()-started, 3), error=str(exc)))
            traceback.print_exc()
            print(f'FAIL {name}: {exc}', flush=True)

    def compare(self, label, before, after):
        (self.run / (label + '-semantics.json')).write_text(json.dumps(dict(before=before, after=after), ensure_ascii=False, indent=2), encoding='utf-8')
        changed = [key for key in before.keys() | after.keys() if before.get(key) != after.get(key)]
        require(not changed, f'{label} changed semantic fields {changed}; inspect the retained artifacts')
        return dict(semanticContentEqual=True, before=before, after=after)

    def payload_snapshot(self):
        result = {}
        for side in self.sides:
            root = self.roots[side]
            for engine in ('libreoffice', 'calibre'):
                for path in sorted((root / engine).rglob('*')):
                    if path.is_file():
                        result[side + '/' + path.relative_to(root).as_posix()] = digest(path)
        return result

    def run_checks(self):
        initial = self.payload_snapshot()
        docx, epub, svg_epub = fixtures(self.run / '中文 空格 语料', self.engine('before', 'calibre/app/resources/fonts/liberation/LiberationSans-Regular.ttf'))
        # Generate the binary DOC once with the reference payload, ensuring A/B
        # both receive byte-identical input rather than testing different exports.
        doc = self.office('before', 'fixture-doc', docx, '.doc', 'doc:MS Word 97')
        require(doc.read_bytes()[:8] == bytes.fromhex('d0cf11e0a1b11ae1'), 'Reference DOC is not a binary Word compound file')
        for source, name in ((docx, 'docx-pdf'), (doc, 'doc-pdf')):
            def pdf_case(source=source, name=name):
                snapshots = {side: self.pdf_snapshot(side + '-' + name, self.office(side, name, source, '.pdf', 'pdf')) for side in self.sides}
                return self.compare(name, snapshots['before'], snapshots['after']) if self.args.before else dict(contentValidated=True, after=snapshots['after'])
            self.check(name, pdf_case)
        for source, name in ((doc, 'doc-docx'), (docx, 'docx-repair')):
            def normalized_case(source=source, name=name):
                snapshots = {}
                for side in self.sides:
                    normalized = self.office(side, name, source, '.docx', 'docx:Office Open XML Text')
                    snapshots[side] = dict(document=self.docx_snapshot(normalized), pdf=self.pdf_snapshot(side + '-' + name + '-pdf', self.office(side, name + '-pdf', normalized, '.pdf', 'pdf')))
                return self.compare(name, snapshots['before'], snapshots['after']) if self.args.before else dict(contentValidated=True, after=snapshots['after'])
            self.check(name, normalized_case)
        for extension in ('mobi', 'azw3'):
            for language in ('en', 'zh_CN'):
                name = 'epub-' + extension + '-' + language
                def ebook_case(extension=extension, language=language, name=name):
                    snapshots = {side: self.ebook_snapshot(side, name, self.ebook(side, name, epub, extension, language)) for side in self.sides}
                    return self.compare(name, snapshots['before'], snapshots['after']) if self.args.before else dict(contentValidated=True, after=snapshots['after'])
                self.check(name, ebook_case)
            name = 'epub-svg-cover-' + extension
            def svg_case(extension=extension, name=name):
                snapshots = {side: self.ebook_snapshot(side, name, self.ebook(side, name, svg_epub, extension, 'zh_CN')) for side in self.sides}
                return self.compare(name, snapshots['before'], snapshots['after']) if self.args.before else dict(contentValidated=True, after=snapshots['after'])
            self.check(name, svg_case)
        final = self.payload_snapshot()
        changed = sorted(key for key in initial.keys() | final.keys() if initial.get(key) != final.get(key))
        self.results.append(dict(name='payload-immutable', status='failed' if changed else 'passed', filesVerified=len(initial), changedFiles=changed))
        report = dict(schemaVersion=1, status='passed' if all(item['status'] == 'passed' for item in self.results) else 'failed', mode='comparison' if self.args.before else 'standalone', completedAt=datetime.now(timezone.utc).isoformat(), before=str(self.roots['before']) if self.args.before else None, after=str(self.roots['after']),
                      workingDirectory=str(self.run), observerMutool=str(self.mutool), observerCalibre=str(self.engine('before', 'calibre/ebook-convert.exe')),
                      permittedPathEntries=[self.system_path], externalToolSearchPathDisabled=True, results=self.results, commands=self.commands,
                      scope={'production': ['DOC/DOCX -> PDF', 'DOC -> DOCX normalization', 'DOCX -> DOCX repair', 'EPUB -> MOBI/AZW3'],
                             'fixtureOnly': ['DOCX -> DOC'], 'observationOnly': ['MOBI/AZW3 -> EPUB with a fixed observer and no generated default cover', 'EPUB font obfuscation decoded before hashing'],
                             'notProductRoutes': ['HTML/FB2 input', 'MOBI/AZW3 input', 'Calibre PDF input/output'], 'supplementalFixturesGenerated': ['HTML', 'FB2']},
                      fixtures=[dict(path=str(path), sha256=digest(path)) for path in sorted((self.run / '中文 空格 语料').iterdir()) if path.is_file()])
        value = json.dumps(report, ensure_ascii=False, indent=2)
        (self.run / 'results.json').write_text(value, encoding='utf-8')
        (self.output / 'latest-results.json').write_text(value, encoding='utf-8')
        print('Results: ' + str(self.run / 'results.json'), flush=True)
        return 0 if all(item['status'] == 'passed' for item in self.results) else 1


def digest_bytes(value):
    return hashlib.sha256(value).hexdigest()


def normalize_font(archive, member, value, package):
    # EPUB export obfuscates embedded font headers with a fresh book UUID. Decode
    # the standard Adobe/IDPF transforms so the complete actual font, including
    # its glyph data, is compared instead of per-run obfuscation bytes.
    if 'META-INF/encryption.xml' not in archive.namelist():
        return value
    namespace = '{http://www.w3.org/2001/04/xmlenc#}'
    for entry in ET.fromstring(archive.read('META-INF/encryption.xml')).iter(namespace + 'EncryptedData'):
        reference = entry.find('.//' + namespace + 'CipherReference')
        if reference is None or reference.attrib.get('URI') != member:
            continue
        algorithm = entry.find(namespace + 'EncryptionMethod').attrib['Algorithm']
        identifier = next(node.text for node in package.iter() if node.attrib.get('id') == package.attrib['unique-identifier'])
        if algorithm == 'http://ns.adobe.com/pdf/enc#RC':
            key, length = uuid.UUID(identifier.removeprefix('urn:uuid:')).bytes, 1024
        elif algorithm == 'http://www.idpf.org/2008/embedding':
            key, length = hashlib.sha1(re.sub(r'\s', '', identifier).encode('utf-8')).digest(), 1040
        else:
            raise AssertionError('Unsupported embedded font encryption: ' + algorithm)
        decoded = bytearray(value)
        for index in range(min(length, len(decoded))):
            decoded[index] ^= key[index % len(key)]
        require(decoded[:4] in (b'\0\1\0\0', b'OTTO', b'true', b'ttcf', b'wOFF', b'wOF2'), 'Embedded font could not be decoded')
        return bytes(decoded)
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before', type=Path)
    parser.add_argument('--after', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mutool', type=Path)
    parser.add_argument('--timeout', type=int, default=180)
    args = parser.parse_args()
    require(sys.platform == 'win32', 'These tests require the actual Windows conversion payloads')
    return Comparison(args).run_checks()


if __name__ == '__main__':
    raise SystemExit(main())

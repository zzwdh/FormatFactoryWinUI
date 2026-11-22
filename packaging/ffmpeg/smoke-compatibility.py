"""Finite real A/B corpus for FormatFactory's compatibility decoder contract.

Use a bundled Python with -I -B. No third-party Python packages or installed
media tools are used. Both sides are observed by the same primary FFmpeg pair.
"""
from __future__ import annotations

import argparse
import bz2
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import struct
import sys
import time
import traceback


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def digest(path):
    sha = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            sha.update(chunk)
    return sha.hexdigest()


def ebml_element(element_id, value):
    """Encode finite EBML elements for the independent compressed-block fixture."""
    for width in range(1, 9):
        if len(value) < (1 << (7 * width)) - 1:
            size = ((1 << (7 * width)) | len(value)).to_bytes(width, 'big')
            return bytes.fromhex(element_id) + size + value
    raise ValueError('EBML fixture value is too large')


def ebml_uint(element_id, value):
    return ebml_element(element_id, value.to_bytes(max(1, (value.bit_length() + 7) // 8), 'big'))


def bzip2_matroska(compressed):
    """One raw BGR24 frame, optionally using Matroska ContentCompAlgo=1."""
    frame = bytes((index * 11 + 7) % 256 for index in range(4 * 2 * 3))
    bitmap = struct.pack('<IiiHHIIiiII', 40, 4, 2, 1, 24, 0, len(frame), 0, 0, 0, 0)
    header = ebml_element('1A45DFA3', b''.join((
        ebml_uint('4286', 1), ebml_uint('42F7', 1), ebml_uint('42F2', 4), ebml_uint('42F3', 8),
        ebml_element('4282', b'matroska'), ebml_uint('4287', 4), ebml_uint('4285', 2))))
    info = ebml_element('1549A966', ebml_uint('2AD7B1', 1000000) +
                        ebml_element('4489', struct.pack('>d', 1000.0)) +
                        ebml_element('4D80', b'FormatFactoryCorpus') +
                        ebml_element('5741', b'FormatFactoryCorpus'))
    track = (ebml_uint('D7', 1) + ebml_uint('73C5', 1) + ebml_uint('83', 1) + ebml_uint('9C', 0) +
             ebml_uint('23E383', 1000000000) + ebml_element('86', b'V_MS/VFW/FOURCC') +
             ebml_element('63A2', bitmap) + ebml_element('E0', ebml_uint('B0', 4) + ebml_uint('BA', 2)))
    if compressed:
        compression = ebml_element('5034', ebml_uint('4254', 1))
        encoding = ebml_uint('5031', 0) + ebml_uint('5032', 1) + ebml_uint('5033', 0) + compression
        track += ebml_element('6D80', ebml_element('6240', encoding))
    tracks = ebml_element('1654AE6B', ebml_element('AE', track))
    packet = bz2.compress(frame) if compressed else frame
    cluster = ebml_element('1F43B675', ebml_uint('E7', 0) + ebml_element('A3', b'\x81\0\0\x80' + packet))
    return header + ebml_element('18538067', info + tracks + cluster)


class Corpus:
    def __init__(self, args):
        self.args = args
        self.baseline = args.baseline.resolve(strict=True)
        self.candidate = args.candidate.resolve(strict=True)
        self.primary = args.primary.resolve(strict=True)
        require(self.baseline.with_name('ffprobe.exe').is_file(), 'Baseline paired ffprobe is missing')
        require(self.candidate.with_name('ffprobe.exe').is_file(), 'Candidate paired ffprobe is missing')
        self.primary_probe = self.primary.with_name('ffprobe.exe')
        require(self.primary_probe.is_file(), 'Fixed primary ffprobe is missing')
        output = args.output.resolve()
        for binary in (self.baseline, self.candidate, self.primary):
            require(not output.is_relative_to(binary.parent), 'Corpus output must be outside payload directories')
        output.mkdir(parents=True, exist_ok=True)
        self.output = output
        self.run = output / (datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + str(os.getpid()))
        self.run.mkdir()
        self.fixtures_dir = self.run / 'fixtures 中文 paths'
        self.fixtures_dir.mkdir()
        self.commands = []
        self.results = []
        self.fixtures = []
        self.fixture_checks = []
        self.system_path = str(Path(os.environ.get('SystemRoot', r'C:\Windows')) / 'System32')
        self.env = os.environ.copy()
        self.env['PATH'] = self.system_path
        self.env['TEMP'] = self.env['TMP'] = str(self.run)
        for name in ('FFREPORT', 'AV_LOG_FORCE_COLOR', 'FFMPEG_DATADIR'):
            self.env.pop(name, None)

    def invoke(self, label, executable, *args):
        command = [str(executable)] + [str(arg) for arg in args]
        start = time.monotonic()
        timed_out = False
        try:
            process = subprocess.run(command, cwd=self.run, env=self.env, stdin=subprocess.DEVNULL,
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     timeout=self.args.timeout, creationflags=0x08000000)
            code, stdout, stderr = process.returncode, process.stdout, process.stderr
        except subprocess.TimeoutExpired as exc:
            timed_out = True
            code, stdout, stderr = None, exc.stdout or b'', exc.stderr or b''
        index = len(self.commands) + 1
        prefix = self.run / f'{index:03d}-{label}'
        prefix.with_suffix('.stdout').write_bytes(stdout)
        prefix.with_suffix('.stderr').write_bytes(stderr)
        self.commands.append(dict(label=label, executable=str(executable), arguments=command[1:],
                                  exitCode=code, timedOut=timed_out,
                                  seconds=round(time.monotonic() - start, 3),
                                  stdout=str(prefix.with_suffix('.stdout')),
                                  stderr=str(prefix.with_suffix('.stderr')),
                                  stdoutSha256=hashlib.sha256(stdout).hexdigest(),
                                  stderrSha256=hashlib.sha256(stderr).hexdigest()))
        require(not timed_out, f'{label} timed out after {self.args.timeout} seconds')
        require(code == 0, f'{label} exited {code}: {stderr.decode("utf-8", "replace")[-3500:]}')
        return stdout.decode('utf-8', 'replace')

    def generate(self, label, output, *args):
        self.invoke('generate-' + label, self.primary, '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
                    *args, '-threads', '1', '-filter_threads', '1', '-filter_complex_threads', '1', output)
        require(output.is_file() and output.stat().st_size > 32, f'No fixture generated for {label}')
        self.fixtures.append(output)
        return output

    def make_fixtures(self):
        root = self.fixtures_dir
        subtitle = root / '字幕.srt'
        subtitle.write_text('1\n00:00:00,000 --> 00:00:00,900\nCORPUS 中文 subtitle\n', encoding='utf-8')
        attachment = root / '附件 notice.txt'
        attachment.write_text('CORPUS attachment 中文 content\n', encoding='utf-8')
        metadata = root / 'metadata.ffmeta'
        metadata.write_text(';FFMETADATA1\ntitle=COMPAT 中文 corpus\nartist=FormatFactory\ncomment=metadata preservation\n'
                            '[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=500\ntitle=CHAPTER 第一章\n'
                            '[CHAPTER]\nTIMEBASE=1/1000\nSTART=500\nEND=1000\ntitle=CHAPTER 第二章\n', encoding='utf-8')
        self.fixtures.extend((subtitle, attachment, metadata))
        rich = self.generate('rich-multitrack', root / '图像 音轨 字幕.mkv',
            '-f', 'lavfi', '-i', 'testsrc2=size=64x48:rate=6:duration=1',
            '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=1',
            '-f', 'lavfi', '-i', 'sine=frequency=880:sample_rate=48000:duration=1',
            '-f', 'srt', '-i', subtitle, '-f', 'ffmetadata', '-i', metadata,
            '-map', '0:v:0', '-map', '1:a:0', '-map', '2:a:0', '-map', '3:s:0',
            '-map_metadata', '4', '-map_chapters', '4', '-c:v', 'ffv1', '-level', '3',
            '-c:a', 'pcm_s16le', '-c:s', 'srt', '-metadata:s:a:0', 'language=eng',
            '-metadata:s:a:1', 'language=zho', '-metadata:s:a:1', 'title=SECOND 中文',
            '-attach', attachment, '-metadata:s:t:0', 'mimetype=text/plain',
            '-metadata:s:t:0', 'filename=附件 notice.txt', '-t', '1')
        h264 = self.generate('h264-aac', root / 'H264 AAC.mp4',
            '-f', 'lavfi', '-i', 'testsrc2=size=64x48:rate=6:duration=1',
            '-f', 'lavfi', '-i', 'sine=frequency=330:sample_rate=48000:duration=1',
            '-map', '0:v:0', '-map', '1:a:0', '-c:v', 'libx264', '-preset', 'ultrafast',
            '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-color_primaries', 'bt709',
            '-color_trc', 'bt709', '-colorspace', 'bt709', '-t', '1')
        rotated = self.generate('rotation180', root / 'H264 rotation180.mp4',
            '-display_rotation:v:0', '180', '-i', h264, '-map', '0', '-c', 'copy')
        rotated13 = self.generate('rotation13', root / 'H264 rotation13.mp4',
            '-display_rotation:v:0', '13', '-i', h264, '-map', '0', '-c', 'copy')
        hevc = self.generate('hevc10-flac', root / 'HEVC10 FLAC.mkv',
            '-f', 'lavfi', '-i', 'testsrc2=size=64x48:rate=4:duration=1',
            '-f', 'lavfi', '-i', 'sine=frequency=550:sample_rate=48000:duration=1',
            '-map', '0:v:0', '-map', '1:a:0', '-c:v', 'libx265', '-preset', 'ultrafast',
            '-x265-params', 'log-level=error:pools=none:frame-threads=1', '-pix_fmt', 'yuv420p10le',
            '-c:a', 'flac', '-color_primaries', 'bt2020', '-color_trc', 'smpte2084',
            '-colorspace', 'bt2020nc', '-t', '1')
        png = self.generate('png-zlib', root / 'PNG compressed video.avi',
            '-f', 'lavfi', '-i', 'testsrc=size=64x48:rate=4:duration=1',
            '-f', 'lavfi', '-i', 'sine=frequency=660:sample_rate=48000:duration=1',
            '-map', '0:v:0', '-map', '1:a:0', '-c:v', 'png', '-pix_fmt', 'rgb24',
            '-c:a', 'pcm_s16le', '-t', '1')
        flac = self.generate('audio-stereo', root / '立体声 FLAC.flac',
            '-f', 'lavfi', '-i', 'sine=frequency=220:sample_rate=48000:duration=1',
            '-f', 'lavfi', '-i', 'sine=frequency=770:sample_rate=48000:duration=1',
            '-filter_complex', '[0:a][1:a]amerge=inputs=2[a]', '-map', '[a]',
            '-c:a', 'flac', '-metadata', 'title=STEREO 中文')
        av1 = self.generate('av1-dav1d', root / 'AV1 software decode.mkv',
            '-f', 'lavfi', '-i', 'testsrc2=size=64x48:rate=3:duration=1',
            '-c:v', 'libaom-av1', '-cpu-used', '8', '-crf', '40', '-b:v', '0',
            '-pix_fmt', 'yuv420p', '-t', '1')
        jxl = self.generate('jxl-software', root / 'JPEG XL 中文.jxl',
            '-f', 'lavfi', '-i', 'testsrc=size=64x48:rate=1:duration=1',
            '-c:v', 'libjxl', '-distance', '0', '-pix_fmt', 'rgb24', '-frames:v', '1')
        rgba = self.generate('png-transparent', root / '透明 RGBA PNG.mkv',
            '-f', 'lavfi', '-i', 'color=c=red@0.25:s=8x8:r=2:d=1,format=rgba',
            '-c:v', 'png', '-pix_fmt', 'rgba', '-t', '1')
        surround = self.generate('audio-surround', root / '环绕声 5.1 FLAC.flac',
            '-f', 'lavfi', '-i',
            'aevalsrc=0.1*sin(2*PI*220*t)|0.1*sin(2*PI*330*t)|0.1*sin(2*PI*440*t)|0.1*sin(2*PI*550*t)|0.1*sin(2*PI*660*t)|0.1*sin(2*PI*770*t):s=48000:d=1:c=5.1',
            '-c:a', 'flac')
        bzip = root / 'Matroska BZip2 压缩块.mkv'
        uncompressed = root / 'Matroska 原始块.mkv'
        bzip.write_bytes(bzip2_matroska(True))
        uncompressed.write_bytes(bzip2_matroska(False))
        self.fixtures.extend((bzip, uncompressed))
        reference_hashes = []
        for name, path in (('raw', uncompressed), ('bzip2', bzip)):
            # The primary build does not include BZip2 Matroska decompression.
            # The fixed baseline's paired probe must recover the independently
            # known raw packet before both decoders process it into FFV1.
            reference = json.loads(self.invoke('fixture-bzip2-reference-' + name,
                self.baseline.with_name('ffprobe.exe'), '-hide_banner','-v','error','-show_packets',
                '-show_data_hash','sha256','-of','json',path))
            packets = reference.get('packets', [])
            require(len(packets) == 1 and packets[0].get('size') == '24', 'BZip2 reference fixture has unexpected packets')
            reference_hashes.append(packets[0].get('data_hash'))
        expected_hash = 'SHA256:' + hashlib.sha256(bytes((index * 11 + 7) % 256 for index in range(24))).hexdigest()
        require(reference_hashes == [expected_hash, expected_hash], 'Compressed Matroska fixture differs from its uncompressed source frame')
        self.fixture_checks.append(dict(name='bzip2-block-matches-uncompressed-reference',
            status='passed', packetSha256=expected_hash, packetBytes=24,
            observer=str(self.baseline.with_name('ffprobe.exe'))))
        return [
            dict(name='ffv1-two-audio-subtitles-attachment-chapters', source=rich, maps=['0:0','0:1','0:2']),
            dict(name='selected-second-audio-with-video', source=rich, maps=['0:0','0:2']),
            dict(name='audio-extraction-second-global-stream', source=rich, maps=['0:2'], audio=True),
            dict(name='image-output-from-video', source=rich, maps=['0:0'], image=True),
            dict(name='h264-aac-bt709', source=h264, maps=['0:0','0:1']),
            dict(name='h264-rotation180', source=rotated, maps=['0:0','0:1']),
            dict(name='h264-rotation13', source=rotated13, maps=['0:0','0:1'], rotation=13),
            dict(name='hevc10-flac-bt2020-pq', source=hevc, maps=['0:0','0:1']),
            dict(name='png-zlib-video', source=png, maps=['0:0','0:1']),
            dict(name='audio-only-stereo-flac', source=flac, maps=['0:0'], audio=True),
            dict(name='av1-software-dav1d', source=av1, maps=['0:0']),
            dict(name='jpegxl-software-decode', source=jxl, maps=['0:0'], image=True),
            dict(name='png-transparent-rgba', source=rgba, maps=['0:0'], image=True),
            dict(name='audio-only-surround-flac', source=surround, maps=['0:0'], audio=True),
            dict(name='matroska-bzip2-compressed-block', source=bzip, maps=['0:0']),
        ]

    def probe(self, label, path, executable=None):
        result = self.invoke(label, executable or self.primary_probe, '-hide_banner', '-v', 'error', '-show_format',
                             '-show_streams', '-show_chapters', '-show_data_hash', 'sha256', '-of', 'json', path)
        return json.loads(result)

    @staticmethod
    def stable_tags(tags):
        volatile = {'encoder', 'duration', 'bps', 'number_of_frames', 'number_of_bytes',
                    '_statistics_writing_app', '_statistics_writing_date_utc', '_statistics_tags'}
        return {key.lower(): value for key, value in tags.items() if key.lower() not in volatile}

    def source_snapshot(self, probe):
        fields = ('index','codec_type','codec_name','width','height','pix_fmt','sample_aspect_ratio',
                  'color_range','color_space','color_transfer','color_primaries','chroma_location','alpha_mode',
                  'sample_fmt','sample_rate','channels','channel_layout')
        streams = []
        for stream in probe['streams']:
            entry = {key: stream[key] for key in fields if key in stream}
            entry['tags'] = self.stable_tags(stream.get('tags', {}))
            entry['disposition'] = stream.get('disposition', {})
            rotations = [item['rotation'] for item in stream.get('side_data_list', []) if 'rotation' in item]
            if rotations:
                entry['rotations'] = rotations
            streams.append(entry)
        chapters = [dict(start=chapter['start_time'], end=chapter['end_time'],
                         tags=self.stable_tags(chapter.get('tags', {}))) for chapter in probe.get('chapters', [])]
        return dict(streams=streams, chapters=chapters, tags=self.stable_tags(probe['format'].get('tags', {})))

    def snapshot(self, label, path, audio_only):
        probe = self.probe(label + '-probe', path)
        streams = []
        for stream in probe['streams']:
            fields = ('codec_type','codec_name','width','height','pix_fmt','sample_aspect_ratio','color_range',
                      'color_space','color_transfer','color_primaries','chroma_location','alpha_mode',
                      'sample_fmt','sample_rate','channels','channel_layout')
            entry = {key: stream[key] for key in fields if key in stream}
            entry['tags'] = self.stable_tags(stream.get('tags', {}))
            entry['disposition'] = stream.get('disposition', {})
            if stream['codec_type'] in ('video', 'audio'):
                codec = 'rawvideo' if stream['codec_type'] == 'video' else 'pcm_s32le'
                args = ['-hide_banner','-loglevel','error','-nostdin','-i',str(path),'-map','0:'+str(stream['index']),
                        '-c',codec,'-threads','1','-filter_threads','1','-f','hash','-hash','sha256','-']
                entry['decodedSha256'] = self.invoke(label + '-stream-' + str(stream['index']), self.primary, *args).strip()
            elif stream['codec_type'] == 'subtitle':
                entry['content'] = self.invoke(label + '-subtitle', self.primary, '-hide_banner','-loglevel','error',
                    '-nostdin','-i',path,'-map','0:'+str(stream['index']),'-c:s','srt','-f','srt','-').replace('\r\n','\n')
            elif stream['codec_type'] == 'attachment':
                entry['extradataHash'] = stream.get('extradata_hash')
                require(entry['extradataHash'], 'Attachment has no content hash')
            streams.append(entry)
        videos = [stream for stream in streams if stream['codec_type'] == 'video']
        audios = [stream for stream in streams if stream['codec_type'] == 'audio']
        require(not videos if audio_only else len(videos) == 1, 'Intermediate has unexpected video stream count')
        require(all(stream['codec_name'] == 'ffv1' for stream in videos), 'Intermediate video is not FFV1')
        require(all(stream['codec_name'] == 'pcm_s32le' for stream in audios), 'Intermediate audio is not PCM_s32le')
        chapters = []
        for chapter in probe.get('chapters', []):
            chapters.append(dict(start=chapter['start_time'], end=chapter['end_time'], tags=self.stable_tags(chapter.get('tags', {}))))
        return dict(streams=streams, chapters=chapters, tags=self.stable_tags(probe['format'].get('tags', {})))

    def case(self, case):
        snapshots = {}
        for side, executable in (('before', self.baseline), ('after', self.candidate)):
            label = case['name'] + '-' + side
            source_probe = self.probe(label + '-paired-source-probe', case['source'], executable.with_name('ffprobe.exe'))
            if 'rotation' in case:
                rotations = [item['rotation'] for stream in source_probe['streams']
                             if stream['codec_type'] == 'video'
                             for item in stream.get('side_data_list', []) if 'rotation' in item]
                require(any(abs(abs(value) - case['rotation']) <= 1 for value in rotations),
                        label + ' fixture lacks its non-right-angle display matrix')
            common = ['-hide_banner','-loglevel','error','-nostdin','-xerror','-err_detect','explode','-i',case['source']]
            maps = []
            for stream in case['maps']:
                maps += ['-map', stream]
            preflight = self.invoke(label + '-preflight', executable, *common, *maps,
                '-t','2','-threads','1','-filter_threads','1','-filter_complex_threads','1',
                '-progress','pipe:1','-nostats','-f','null','-')
            progress = {}
            for line in preflight.splitlines():
                if '=' in line:
                    key, value = line.split('=', 1)
                    if key in ('frame','out_time_us') and value.strip().isdigit():
                        progress[key] = max(progress.get(key, 0), int(value.strip()))
            require(any(value > 0 for value in progress.values()), label + ' decoded no media')
            output = self.run / (label + '.mkv')
            args = ['-hide_banner','-loglevel','warning','-nostdin','-y','-xerror','-err_detect','explode','-i',case['source'], *maps]
            if not case.get('audio'):
                args += ['-c:v','ffv1','-level','3','-g','1']
                if not case.get('image'):
                    args += ['-map','0:s?','-map','0:t?','-c:s','copy','-c:t','copy']
            args += ['-c:a','pcm_s32le','-map_metadata','0','-map_chapters','0','-threads','1',
                     '-filter_threads','1','-filter_complex_threads','1','-progress','pipe:1','-nostats','-f','matroska',output]
            self.invoke(label + '-intermediate', executable, *args)
            snapshots[side] = self.snapshot(label, output, case.get('audio', False))
            snapshots[side]['sourceProbe'] = self.source_snapshot(source_probe)
            source_videos = [stream for stream in source_probe['streams'] if stream['codec_type'] == 'video']
            if not case.get('audio'):
                actual_video = next(stream for stream in snapshots[side]['streams'] if stream['codec_type'] == 'video')
                require((actual_video['width'], actual_video['height']) == (source_videos[0]['width'], source_videos[0]['height']),
                        label + ' changes dimensions rejected by production validation')
            actual_audio_count = sum(stream['codec_type'] == 'audio' for stream in snapshots[side]['streams'])
            expected_audio_count = len(case['maps']) if case.get('audio') else len(case['maps']) - 1
            require(actual_audio_count == expected_audio_count, label + ' changed requested audio selection')
        snapshot_path = self.run / (case['name'] + '-semantics.json')
        snapshot_path.write_text(json.dumps(snapshots, ensure_ascii=False, indent=2), encoding='utf-8')
        changed = [key for key in snapshots['before'] if snapshots['before'][key] != snapshots['after'][key]]
        require(not changed, case['name'] + ': changed semantic fields: ' + ', '.join(changed))
        return dict(semanticContentEqual=True, report=str(snapshot_path), reportSha256=digest(snapshot_path),
                    videoAndAudioDecodedHashEqual=True, pairedSourceProbesEqual=True, streams=len(snapshots['after']['streams']))

    @staticmethod
    def payload_snapshot(directory):
        return {path.relative_to(directory).as_posix(): digest(path)
                for path in sorted(directory.rglob('*')) if path.is_file()}

    def run_checks(self):
        require(sys.platform == 'win32', 'Run with actual Windows payloads')
        start = time.monotonic()
        roots = {str(binary.parent): binary.parent for binary in (self.baseline, self.candidate, self.primary)}
        payloads = {name: self.payload_snapshot(directory) for name, directory in roots.items()}
        try:
            cases = self.make_fixtures()
            fixture_hashes = {str(path): digest(path) for path in self.fixtures}
            for case in cases:
                case_start = time.monotonic()
                try:
                    details = self.case(case)
                    self.results.append(dict(name=case['name'], status='passed', seconds=round(time.monotonic()-case_start, 3), details=details))
                    print('PASS ' + case['name'], flush=True)
                except Exception as exc:
                    self.results.append(dict(name=case['name'], status='failed', seconds=round(time.monotonic()-case_start, 3), error=str(exc)))
                    traceback.print_exc()
                    print('FAIL ' + case['name'] + ': ' + str(exc), flush=True)
            changed_fixtures = [path for path, value in fixture_hashes.items() if digest(Path(path)) != value]
            final_payloads = {name: self.payload_snapshot(directory) for name, directory in roots.items()}
            changed_payloads = {name: [path for path in set(payloads[name]) | set(final_payloads[name])
                                      if payloads[name].get(path) != final_payloads[name].get(path)]
                                for name in roots}
            changed_payloads = {name: paths for name, paths in changed_payloads.items() if paths}
            self.results.append(dict(name='fixtures-and-payloads-immutable', status='failed' if changed_fixtures or changed_payloads else 'passed',
                details=dict(fixtureCount=len(fixture_hashes), payloadFiles=sum(len(snapshot) for snapshot in payloads.values()),
                             changedFixtures=changed_fixtures, changedPayloads=changed_payloads)))
        except Exception as exc:
            self.results.append(dict(name='fixture-generation-or-harness', status='failed', error=str(exc)))
            traceback.print_exc()
        report = dict(schemaVersion=1, status='passed' if self.results and all(result['status']=='passed' for result in self.results) else 'failed',
            completedAt=datetime.now(timezone.utc).isoformat(), seconds=round(time.monotonic()-start, 3),
            baseline=str(self.baseline), candidate=str(self.candidate), observer=str(self.primary), workingDirectory=str(self.run),
            permittedPathEntries=[self.system_path], externalToolSearchPathDisabled=True,
            results=self.results, commands=self.commands, fixtureChecks=self.fixture_checks,
            fixtures=[dict(path=str(path), bytes=path.stat().st_size, sha256=digest(path)) for path in self.fixtures],
            scope=dict(contract='Production compatibility preflight null decode and FFV1/PCM_s32le Matroska intermediate; fixed primary observers.',
                covered='Generated FFV1/PCM, H264/AAC, HEVC10/FLAC, PNG-zlib, transparent RGBA, stereo/5.1 FLAC, AV1, JPEG XL, independently encoded Matroska BZip2 block, multi-track selection, subtitle/attachment/chapter/Unicode metadata and 180/13-degree rotation.',
                notCovered=['Original BVC2/AC-4/BRAW/Bink2 source recordings', 'All input codecs/containers', 'Native ARM64 execution', 'Playback or final output quality/performance acceptance']))
        text = json.dumps(report, ensure_ascii=False, indent=2)
        (self.run / 'results.json').write_text(text, encoding='utf-8')
        (self.output / 'latest-results.json').write_text(text, encoding='utf-8')
        print('Results: ' + str(self.run / 'results.json'), flush=True)
        return 0 if report['status']=='passed' else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--primary', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--timeout', type=int, default=120)
    return Corpus(parser.parse_args()).run_checks()


if __name__ == '__main__':
    raise SystemExit(main())

use super::{Failure, Progress, Result, input_file, output_file};
use flate2::{Compress, Compression, Decompress, FlushCompress, FlushDecompress, Status};
use std::fs::File;
use std::io::{self, BufRead, BufReader, BufWriter, Read, Seek, SeekFrom, Write};
use std::path::Path;

const HEADER_SIZE: u64 = 24;
const PLAIN: u32 = 1 << 31;
const OFFSET_MASK: u32 = PLAIN - 1;
const MAX_BLOCK_COUNT: u64 = (i32::MAX - 1) as u64;
const BUFFER_SIZE: usize = 128 * 1024;

struct Header {
    total: u64,
    size: u32,
    count: u32,
    shift: u8,
}

fn check_size(size: u32) -> Result<()> {
    if !(512..=16 * 1024 * 1024).contains(&size) || !size.is_power_of_two() {
        return Err("ConversionUiCsoBlockSizePowerOfTwo".into());
    }
    Ok(())
}

impl Header {
    fn parse(input: &mut impl Read) -> Result<Self> {
        let mut bytes = [0; HEADER_SIZE as usize];
        read_exact(input, &mut bytes, "ConversionUiUnexpectedEndOfStream")?;
        if &bytes[..4] != b"CISO" {
            return Err("ConversionUiCsoMagicInvalid".into());
        }
        // Some historical v1 writers left header_size zero but still wrote all 24 bytes.
        if !matches!(u32::from_le_bytes(bytes[4..8].try_into().unwrap()), 0 | 24) {
            return Err("ConversionUiCsoHeaderSizeInvalid".into());
        }
        let total = u64::from_le_bytes(bytes[8..16].try_into().unwrap());
        if total == 0 || total > i64::MAX as u64 {
            return Err("ConversionUiCsoUncompressedSizeInvalid".into());
        }
        let size = u32::from_le_bytes(bytes[16..20].try_into().unwrap());
        if size > i32::MAX as u32 {
            return Err("ConversionUiCsoBlockSizeInvalid".into());
        }
        check_size(size)?;
        if bytes[20] != 1 {
            return Err("ConversionUiCsoVersion1Only".into());
        }
        let shift = bytes[21];
        if shift > 31 {
            return Err("ConversionUiCsoAlignmentInvalid".into());
        }
        if bytes[22..24] != [0, 0] {
            return Err("ConversionUiCsoReservedHeaderInvalid".into());
        }
        let count = total.div_ceil(u64::from(size));
        if count > MAX_BLOCK_COUNT {
            return Err("ConversionUiCsoTooManyBlocks".into());
        }
        Ok(Self {
            total,
            size,
            count: count as u32,
            shift,
        })
    }

    fn for_iso(total: u64, size: u32) -> Result<Self> {
        check_size(size)?;
        if total == 0 {
            return Err("ConversionUiIsoImageEmpty".into());
        }
        let count = total.div_ceil(u64::from(size));
        if count > MAX_BLOCK_COUNT {
            return Err("ConversionUiIsoTooManyCsoBlocks".into());
        }
        let entries = u128::from(count + 1);
        for shift in 0..=31 {
            let unit = 1u128 << shift;
            let worst_size =
                u128::from(HEADER_SIZE) + entries * 4 + u128::from(total) + entries * (unit - 1);
            if worst_size <= i64::MAX as u128
                && worst_size.div_ceil(unit) <= u128::from(OFFSET_MASK)
            {
                return Ok(Self {
                    total,
                    size,
                    count: count as u32,
                    shift,
                });
            }
        }
        Err("ConversionUiIsoTooLargeForCsoV1".into())
    }

    fn write(&self, output: &mut impl Write) -> Result<()> {
        output.write_all(b"CISO")?;
        output.write_all(&(HEADER_SIZE as u32).to_le_bytes())?;
        output.write_all(&self.total.to_le_bytes())?;
        output.write_all(&self.size.to_le_bytes())?;
        output.write_all(&[1, self.shift, 0, 0])?;
        Ok(())
    }

    fn table_end(&self) -> u64 {
        HEADER_SIZE + (u64::from(self.count) + 1) * 4
    }

    fn offset(&self, entry: u32) -> u64 {
        u64::from(entry & OFFSET_MASK) << self.shift
    }

    fn encoded_offset(&self, pos: u64) -> Result<u32> {
        let offset = pos >> self.shift;
        if offset > u64::from(OFFSET_MASK) {
            return Err("ConversionUiCsoBlockOffsetExceedsV1".into());
        }
        Ok(offset as u32)
    }
}

fn read_exact(input: &mut impl Read, bytes: &mut [u8], short: &'static str) -> Result<()> {
    match input.read_exact(bytes) {
        Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => Err(short.into()),
        other => Ok(other?),
    }
}

fn next_index(input: &mut impl Read) -> Result<u32> {
    let mut entry = [0; 4];
    read_exact(input, &mut entry, "ConversionUiCsoIndexTruncated")?;
    Ok(u32::from_le_bytes(entry))
}

struct Index {
    reader: BufReader<File>,
    header: Header,
    length: u64,
    current: u32,
}

struct Block {
    position: u64,
    stored: u64,
    expected: usize,
    plain: bool,
}

impl Index {
    fn open(source: &Path) -> Result<Self> {
        let file = input_file(source)?;
        let length = file.metadata()?.len();
        let mut reader = BufReader::with_capacity(BUFFER_SIZE, file);
        let header = Header::parse(&mut reader)?;
        if header.table_end() > length {
            return Err("ConversionUiCsoIndexTruncated".into());
        }
        reader.seek(SeekFrom::Start(header.table_end() - 4))?;
        if next_index(&mut reader)? & PLAIN != 0 {
            return Err("ConversionUiCsoFinalIndexFlagInvalid".into());
        }
        reader.seek(SeekFrom::Start(HEADER_SIZE))?;
        let current = next_index(&mut reader)?;
        if header.offset(current) < header.table_end() {
            return Err("ConversionUiCsoDataOverlapsIndex".into());
        }
        Ok(Self {
            reader,
            header,
            length,
            current,
        })
    }

    fn next(&mut self, block: u32) -> Result<Block> {
        let current = self.current;
        self.current = next_index(&mut self.reader)?;
        let hdr = &self.header;
        let position = hdr.offset(current);
        let end = hdr.offset(self.current);
        if end <= position || end > self.length {
            return Err(Failure::block(
                "ConversionUiCsoBlockOffsetRangeInvalid",
                block,
            ));
        }
        let stored = end - position;
        let expected =
            (hdr.total - u64::from(block) * u64::from(hdr.size)).min(u64::from(hdr.size));
        let plain = current & PLAIN != 0;
        if plain && (stored < expected || stored - expected >= 1u64 << hdr.shift) {
            return Err(Failure::block(
                "ConversionUiCsoPlainBlockStoredSizeInvalid",
                block,
            ));
        }
        Ok(Block {
            position,
            stored,
            expected: expected as usize,
            plain,
        })
    }

    fn finish(&self) -> Result<()> {
        if self.header.offset(self.current) != self.length {
            return Err("ConversionUiCsoDataBeyondFinalIndex".into());
        }
        Ok(())
    }
}

// The index and payload have independent sequential cursors. Memory depends on
// block size and the fixed index buffer, never on the total number of sectors.
pub(super) fn decode(source: &Path, destination: &Path, progress: &mut Progress) -> Result<()> {
    let mut index = Index::open(source)?;
    let output = BufWriter::with_capacity(BUFFER_SIZE, output_file(destination)?);
    decode_blocks(source, &mut index, output, Some(progress))
}

fn decode_blocks(
    source: &Path,
    index: &mut Index,
    mut output: impl Write,
    mut progress: Option<&mut Progress>,
) -> Result<()> {
    let (total, count) = (index.header.total, index.header.count);
    let mut data = BufReader::with_capacity(BUFFER_SIZE, input_file(source)?);
    let mut buffer = vec![0; index.header.size as usize + 1];
    let mut inflater = Decompress::new(false);
    let mut processed = 0;
    if let Some(p) = progress.as_deref_mut() {
        p.report(0, total, 0, count)?;
    }
    for block in 0..count {
        let Block {
            position,
            stored,
            expected,
            plain,
        } = index.next(block)?;
        if block == 0 {
            data.seek(SeekFrom::Start(position))?;
        }
        let consumed = if plain {
            read_exact(
                &mut data,
                &mut buffer[..expected],
                "ConversionUiCsoBlockDataTruncatedOrInvalid",
            )
            .map_err(|error| {
                if error.key == "io" {
                    error
                } else {
                    Failure::block(error.key, block)
                }
            })?;
            expected as u64
        } else {
            inflate(
                &mut inflater,
                &mut data,
                stored,
                &mut buffer[..expected + 1],
                block,
            )?
        };
        if stored != consumed {
            data.seek_relative((stored - consumed) as i64)?;
        }
        output.write_all(&buffer[..expected])?;
        processed += expected as u64;
        if block + 1 < count {
            if let Some(p) = progress.as_deref_mut() {
                p.report(processed, total, block + 1, count)?;
            }
        }
    }
    index.finish()?;
    output.flush()?;
    if let Some(p) = progress {
        p.report(processed, total, count, count)?;
    }
    Ok(())
}

pub(super) fn check(args: &[std::ffi::OsString]) -> Result<()> {
    let [mode, input] = args else {
        return Err("invalid-request".into());
    };
    let source = Path::new(input);
    let mut index = Index::open(source)?;
    match mode.to_str() {
        Some("inspect") => {
            for block in 0..index.header.count {
                index.next(block)?;
            }
            index.finish()?;
        }
        Some("verify") => decode_blocks(source, &mut index, io::sink(), None)?,
        _ => return Err("invalid-request".into()),
    }
    writeln!(
        io::stdout().lock(),
        "cso {} {} {}",
        index.header.total,
        index.header.size,
        index.header.count
    )?;
    Ok(())
}

// A valid compressed block can include alignment padding. Read at most its
// indexed length and require the DEFLATE end marker and the exact output size.
fn inflate(
    inflater: &mut Decompress,
    data: &mut impl BufRead,
    stored: u64,
    output: &mut [u8],
    block: u32,
) -> Result<u64> {
    inflater.reset(false);
    loop {
        let consumed = inflater.total_in();
        let written = inflater.total_out() as usize;
        let available = data.fill_buf()?;
        let take = (available.len() as u64).min(stored - consumed) as usize;
        let status = inflater
            .decompress(
                &available[..take],
                &mut output[written..],
                FlushDecompress::None,
            )
            .map_err(|_| Failure::block("ConversionUiCsoBlockDataTruncatedOrInvalid", block))?;
        let step = (inflater.total_in() - consumed) as usize;
        data.consume(step);
        let size = inflater.total_out() as usize;
        if size == output.len() {
            return Err(Failure::block(
                "ConversionUiCsoBlockExpandsBeyondDeclaredSize",
                block,
            ));
        }
        if status == Status::StreamEnd {
            return if size == output.len() - 1 {
                Ok(inflater.total_in())
            } else {
                Err(Failure::block(
                    "ConversionUiCsoBlockDataTruncatedOrInvalid",
                    block,
                ))
            };
        }
        if step == 0 && size == written {
            return Err(Failure::block(
                "ConversionUiCsoBlockDataTruncatedOrInvalid",
                block,
            ));
        }
    }
}

pub(super) fn encode(
    source: &Path,
    destination: &Path,
    size: u32,
    progress: &mut Progress,
) -> Result<()> {
    let file = input_file(source)?;
    let hdr = Header::for_iso(file.metadata()?.len(), size)?;
    let mut input = BufReader::with_capacity(BUFFER_SIZE, file);
    let mut output = BufWriter::with_capacity(BUFFER_SIZE, output_file(destination)?);
    hdr.write(&mut output)?;
    let unit = 1u64 << hdr.shift;
    let mut pos = hdr.table_end().next_multiple_of(unit);
    output.seek(SeekFrom::Start(pos))?;
    let mut buffer = vec![0; size as usize];
    let mut compressed = vec![0; size as usize];
    let mut compressor = Compress::new(Compression::best(), false);
    let mut index = Vec::with_capacity(64 * 1024);
    let mut index_pos = HEADER_SIZE;
    let mut processed = 0;
    progress.report(0, hdr.total, 0, hdr.count)?;
    for block in 0..hdr.count {
        let expected = (hdr.total - processed).min(u64::from(size)) as usize;
        let bytes = &mut buffer[..expected];
        read_exact(&mut input, bytes, "ConversionUiUnexpectedEndOfStream")?;
        compressor.reset();
        // A compressed stream that doesn't fit in the original length should be stored plain.
        let status = compressor
            .compress(bytes, &mut compressed[..expected], FlushCompress::Finish)
            .map_err(|_| Failure::from("io"))?;
        let len = compressor.total_out();
        let mut entry = hdr.encoded_offset(pos)?;
        let payload: &[u8] = if status == Status::StreamEnd
            && len.div_ceil(unit) < (expected as u64).div_ceil(unit)
        {
            &compressed[..len as usize]
        } else {
            entry |= PLAIN;
            bytes
        };
        index.extend_from_slice(&entry.to_le_bytes());
        output.write_all(payload)?;
        pos += payload.len() as u64;
        let aligned = pos.next_multiple_of(unit);
        if pos != aligned {
            pad(&mut output, aligned - pos)?;
            pos = aligned;
        }
        // Batch the backpatches so the memory cost is independent of the index length.
        if index.len() >= 64 * 1024 {
            flush_index(&mut output, &mut index, &mut index_pos, pos)?;
        }
        processed += expected as u64;
        if block + 1 < hdr.count {
            progress.report(processed, hdr.total, block + 1, hdr.count)?;
        }
    }
    index.extend_from_slice(&hdr.encoded_offset(pos)?.to_le_bytes());
    flush_index(&mut output, &mut index, &mut index_pos, pos)?;
    output.flush()?;
    progress.report(processed, hdr.total, hdr.count, hdr.count)
}

fn flush_index(
    output: &mut BufWriter<File>,
    bytes: &mut Vec<u8>,
    index_pos: &mut u64,
    data_pos: u64,
) -> Result<()> {
    output.seek(SeekFrom::Start(*index_pos))?;
    output.write_all(bytes)?;
    *index_pos += bytes.len() as u64;
    bytes.clear();
    output.seek(SeekFrom::Start(data_pos))?;
    Ok(())
}

fn pad(output: &mut impl Write, mut remaining: u64) -> Result<()> {
    let zeros = [0; 4096];
    while remaining > 0 {
        let count = remaining.min(zeros.len() as u64) as usize;
        output.write_all(&zeros[..count])?;
        remaining -= count as u64;
    }
    Ok(())
}

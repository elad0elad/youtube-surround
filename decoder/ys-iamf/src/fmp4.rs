//! Fragmented MP4 reader for an IAMF audio track.
//!
//! Descriptor OBUs come from the `iacb` box. Each sample is one temporal
//! unit (IAMF §6.2.5) and is returned on its own so the streaming decoder
//! can emit PCM as fragments arrive.

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

pub struct IamfTrack {
    pub descriptors: Vec<u8>,
    pub timescale: u32,
    pub samples: Vec<Sample>,
}

pub struct Sample {
    pub duration: u32,
    pub data: Vec<u8>,
}

pub enum Polled {
    NeedMore,
    Skip(usize),
    Moov { end: usize, descriptors: Vec<u8> },
    Moof { end: usize, samples: Vec<Sample> },
}

/// One complete top-level box, or [`Polled::NeedMore`] when the bytes for
/// that box (including the media samples it points at) are not here yet.
pub fn poll_box(data: &[u8], offset: usize) -> Result<Polled, String> {
    if offset + 8 > data.len() {
        return Ok(Polled::NeedMore);
    }
    let mut size = u32::from_be_bytes(data[offset..offset + 4].try_into().unwrap()) as u64;
    let mut header = 8usize;
    let typ: [u8; 4] = data[offset + 4..offset + 8].try_into().unwrap();
    if size == 1 {
        if offset + 16 > data.len() {
            return Ok(Polled::NeedMore);
        }
        size = u64::from_be_bytes(data[offset + 8..offset + 16].try_into().unwrap());
        header = 16;
    } else if size == 0 {
        // A size of 0 means "until the end of the file". A growing stream
        // cannot know that end yet.
        return Ok(Polled::NeedMore);
    }
    if size < header as u64 {
        return Err("box smaller than its header".into());
    }
    let end = offset.saturating_add(size as usize);
    if end > data.len() {
        return Ok(Polled::NeedMore);
    }
    let body = offset + header;
    match &typ {
        b"moov" => {
            let (descriptors, _timescale) = parse_moov(&data[body..end])?;
            Ok(Polled::Moov { end, descriptors })
        }
        b"moof" => match parse_moof(data, offset, body, end) {
            Ok(samples) => Ok(Polled::Moof { end, samples }),
            Err(err) if err.contains("outside") => Ok(Polled::NeedMore),
            Err(err) => Err(err),
        },
        _ => Ok(Polled::Skip(end)),
    }
}

pub fn read_iamf_mp4(path: &Path) -> Result<IamfTrack, String> {
    let mut file = File::open(path).map_err(|e| format!("open {}: {e}", path.display()))?;
    let len = file.seek(SeekFrom::End(0)).map_err(|e| e.to_string())? as usize;
    file.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
    let mut data = Vec::with_capacity(len);
    file.read_to_end(&mut data).map_err(|e| e.to_string())?;
    parse_iamf_mp4(&data)
}

pub fn parse_iamf_mp4(data: &[u8]) -> Result<IamfTrack, String> {
    if data.len() < 8 || &data[4..8] != b"ftyp" {
        return Err("not an MP4 file".into());
    }
    let mut descriptors = None;
    let mut timescale = 48_000u32;
    let mut samples = Vec::new();
    let mut offset = 0usize;
    while offset + 8 <= data.len() {
        let (typ, body_at, end) = box_range(data, offset)?;
        match &typ {
            b"moov" => {
                let found = parse_moov(&data[body_at..end])?;
                descriptors = Some(found.0);
                timescale = found.1;
            }
            b"moof" => {
                if end > data.len() {
                    return Err("truncated moof".into());
                }
                samples.extend(parse_moof(data, offset, body_at, end)?);
            }
            _ => {}
        }
        offset = end;
    }
    let descriptors = descriptors.ok_or("no IAMF sample entry in moov")?;
    if samples.is_empty() {
        return Err("MP4 has an IAMF sample entry but no media samples".into());
    }
    Ok(IamfTrack {
        descriptors,
        timescale,
        samples,
    })
}

fn parse_moov(moov: &[u8]) -> Result<(Vec<u8>, u32), String> {
    let mut offset = 0usize;
    while offset + 8 <= moov.len() {
        let (typ, body, end) = box_range(moov, offset)?;
        if typ == *b"trak" {
            if let Some(found) = parse_trak(&moov[body..end])? {
                return Ok(found);
            }
        }
        offset = end;
    }
    Err("no IAMF track".into())
}

fn parse_trak(trak: &[u8]) -> Result<Option<(Vec<u8>, u32)>, String> {
    let mut mdia = None;
    let mut offset = 0usize;
    while offset + 8 <= trak.len() {
        let (typ, body, end) = box_range(trak, offset)?;
        if typ == *b"mdia" {
            mdia = Some(&trak[body..end]);
        }
        offset = end;
    }
    let Some(mdia) = mdia else {
        return Ok(None);
    };
    let mut timescale = 48_000u32;
    let mut stbl = None;
    let mut offset = 0usize;
    while offset + 8 <= mdia.len() {
        let (typ, body, end) = box_range(mdia, offset)?;
        if typ == *b"mdhd" {
            timescale = mdhd_timescale(&mdia[body..end])?;
        } else if typ == *b"minf" {
            stbl = find_child(&mdia[body..end], b"stbl");
        }
        offset = end;
    }
    let Some(stbl) = stbl else {
        return Ok(None);
    };
    let Some(stsd) = find_child(stbl, b"stsd") else {
        return Ok(None);
    };
    match descriptors_from_stsd(stsd) {
        Ok(d) => Ok(Some((d, timescale))),
        Err(_) => Ok(None),
    }
}

fn mdhd_timescale(body: &[u8]) -> Result<u32, String> {
    if body.is_empty() {
        return Err("empty mdhd".into());
    }
    let version = body[0];
    if version == 0 {
        if body.len() < 16 {
            return Err("short mdhd".into());
        }
        Ok(u32::from_be_bytes(body[12..16].try_into().unwrap()))
    } else if body.len() >= 24 {
        Ok(u32::from_be_bytes(body[20..24].try_into().unwrap()))
    } else {
        Err("short mdhd".into())
    }
}

fn descriptors_from_stsd(stsd: &[u8]) -> Result<Vec<u8>, String> {
    if stsd.len() < 8 {
        return Err("short stsd".into());
    }
    let count = u32::from_be_bytes(stsd[4..8].try_into().unwrap()) as usize;
    let mut offset = 8usize;
    for _ in 0..count {
        if offset + 8 > stsd.len() {
            break;
        }
        let (typ, body, end) = box_range(stsd, offset)?;
        if typ == *b"iamf" {
            // AudioSampleEntry fields after the box header: 28 bytes, then child boxes.
            if body + 28 > end {
                return Err("iamf sample entry too small".into());
            }
            let entry = &stsd[body + 28..end];
            let iacb = find_child(entry, b"iacb").ok_or("iamf sample entry has no iacb")?;
            return config_obus(iacb);
        }
        offset = end;
    }
    Err("stsd has no iamf entry".into())
}

fn config_obus(iacb_body: &[u8]) -> Result<Vec<u8>, String> {
    if iacb_body.is_empty() {
        return Err("empty iacb".into());
    }
    if iacb_body[0] != 1 {
        return Err(format!(
            "unsupported iacb configurationVersion {}",
            iacb_body[0]
        ));
    }
    let (size, used) = leb128(&iacb_body[1..])?;
    let start = 1 + used;
    let end = start + size;
    if end > iacb_body.len() {
        return Err("iacb configOBUs overrun the box".into());
    }
    Ok(iacb_body[start..end].to_vec())
}

fn parse_moof(file: &[u8], moof_at: usize, body: usize, end: usize) -> Result<Vec<Sample>, String> {
    let mut out = Vec::new();
    let mut offset = body;
    while offset + 8 <= end {
        let (typ, child, child_end) = box_range(file, offset)?;
        if typ == *b"traf" {
            out.extend(parse_traf(file, moof_at, end, child, child_end)?);
        }
        offset = child_end;
    }
    Ok(out)
}

struct Tfhd {
    base: u64,
    default_duration: u32,
    default_size: u32,
}

fn parse_traf(
    file: &[u8],
    moof_at: usize,
    moof_end: usize,
    body: usize,
    end: usize,
) -> Result<Vec<Sample>, String> {
    let mut tfhd = Tfhd {
        // ISO-BMFF: without an explicit base, samples start after the moof.
        base: moof_end as u64,
        default_duration: 0,
        default_size: 0,
    };
    let mut runs = Vec::new();
    let mut offset = body;
    while offset + 8 <= end {
        let (typ, child, child_end) = box_range(file, offset)?;
        if typ == *b"tfhd" {
            tfhd = parse_tfhd(&file[child..child_end], moof_at, moof_end)?;
        } else if typ == *b"trun" {
            runs.push((child, child_end));
        }
        offset = child_end;
    }
    let mut samples = Vec::new();
    for (start, stop) in runs {
        samples.extend(parse_trun(file, &tfhd, &file[start..stop])?);
    }
    Ok(samples)
}

fn parse_tfhd(body: &[u8], moof_at: usize, moof_end: usize) -> Result<Tfhd, String> {
    if body.len() < 8 {
        return Err("short tfhd".into());
    }
    let flags = u32::from_be_bytes(body[0..4].try_into().unwrap()) & 0x00ff_ffff;
    let mut p = 8usize; // version/flags + track_id
    let mut base = moof_end as u64;
    let mut default_duration = 0u32;
    let mut default_size = 0u32;
    if flags & 0x000001 != 0 {
        if p + 8 > body.len() {
            return Err("tfhd base-data-offset truncated".into());
        }
        base = u64::from_be_bytes(body[p..p + 8].try_into().unwrap());
        p += 8;
    }
    if flags & 0x020000 != 0 {
        base = moof_at as u64;
    }
    if flags & 0x000002 != 0 {
        p += 4;
    }
    if flags & 0x000008 != 0 {
        if p + 4 > body.len() {
            return Err("tfhd default duration truncated".into());
        }
        default_duration = u32::from_be_bytes(body[p..p + 4].try_into().unwrap());
        p += 4;
    }
    if flags & 0x000010 != 0 {
        if p + 4 > body.len() {
            return Err("tfhd default size truncated".into());
        }
        default_size = u32::from_be_bytes(body[p..p + 4].try_into().unwrap());
    }
    Ok(Tfhd {
        base,
        default_duration,
        default_size,
    })
}

fn parse_trun(file: &[u8], tfhd: &Tfhd, body: &[u8]) -> Result<Vec<Sample>, String> {
    if body.len() < 8 {
        return Err("short trun".into());
    }
    let version_flags = u32::from_be_bytes(body[0..4].try_into().unwrap());
    let version = (version_flags >> 24) as u8;
    let flags = version_flags & 0x00ff_ffff;
    let count = u32::from_be_bytes(body[4..8].try_into().unwrap()) as usize;
    let mut p = 8usize;
    let mut data_offset = 0i32;
    if flags & 0x000001 != 0 {
        if p + 4 > body.len() {
            return Err("trun data-offset truncated".into());
        }
        data_offset = i32::from_be_bytes(body[p..p + 4].try_into().unwrap());
        p += 4;
    }
    if flags & 0x000004 != 0 {
        p += 4; // first_sample_flags
    }
    let mut cursor = tfhd.base as i64 + data_offset as i64;
    if cursor < 0 {
        return Err("trun sample offset is negative".into());
    }
    let mut samples = Vec::with_capacity(count);
    for _ in 0..count {
        let mut duration = tfhd.default_duration;
        let mut size = tfhd.default_size;
        if flags & 0x000100 != 0 {
            if p + 4 > body.len() {
                return Err("trun sample duration truncated".into());
            }
            duration = u32::from_be_bytes(body[p..p + 4].try_into().unwrap());
            p += 4;
        }
        if flags & 0x000200 != 0 {
            if p + 4 > body.len() {
                return Err("trun sample size truncated".into());
            }
            size = u32::from_be_bytes(body[p..p + 4].try_into().unwrap());
            p += 4;
        }
        if flags & 0x000400 != 0 {
            p += 4;
        }
        if flags & 0x000800 != 0 {
            p += if version == 0 { 4 } else { 4 };
        }
        let start = cursor as usize;
        let stop = start + size as usize;
        if size == 0 || stop > file.len() {
            return Err(format!(
                "sample points outside the file (offset {start}, size {size})"
            ));
        }
        samples.push(Sample {
            duration,
            data: file[start..stop].to_vec(),
        });
        cursor += size as i64;
    }
    Ok(samples)
}

fn find_child<'a>(container: &'a [u8], want: &[u8; 4]) -> Option<&'a [u8]> {
    let mut offset = 0usize;
    while offset + 8 <= container.len() {
        let Ok((typ, body, end)) = box_range(container, offset) else {
            return None;
        };
        if typ == *want {
            return Some(&container[body..end]);
        }
        offset = end;
    }
    None
}

fn box_range(data: &[u8], offset: usize) -> Result<([u8; 4], usize, usize), String> {
    if offset + 8 > data.len() {
        return Err("truncated box header".into());
    }
    let mut size = u32::from_be_bytes(data[offset..offset + 4].try_into().unwrap()) as u64;
    let mut header = 8usize;
    let typ: [u8; 4] = data[offset + 4..offset + 8].try_into().unwrap();
    if size == 1 {
        if offset + 16 > data.len() {
            return Err("truncated large box".into());
        }
        size = u64::from_be_bytes(data[offset + 8..offset + 16].try_into().unwrap());
        header = 16;
    } else if size == 0 {
        size = (data.len() - offset) as u64;
    }
    if size < header as u64 {
        return Err("box smaller than its header".into());
    }
    let end = offset + size as usize;
    if end > data.len() {
        return Err(format!(
            "box {} extends past the file",
            String::from_utf8_lossy(&typ)
        ));
    }
    Ok((typ, offset + header, end))
}

fn leb128(data: &[u8]) -> Result<(usize, usize), String> {
    let mut value = 0u64;
    for i in 0..8 {
        if i >= data.len() {
            return Err("truncated leb128".into());
        }
        let byte = data[i];
        value |= u64::from(byte & 0x7f) << (7 * i);
        if byte & 0x80 == 0 {
            return Ok((value as usize, i + 1));
        }
    }
    Err("leb128 too long".into())
}

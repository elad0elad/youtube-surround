//! Decode an IAMF audio track from a (fragmented) MP4 to 5.1 PCM.
//!
//! The renderer target is ITU-R BS.2051 sound system B: L R C LFE Ls Rs.
//! That is a render of the IAMF mix, not a stereo upmix. If the decoder
//! does not actually produce six channels, this program exits with an error
//! and does not write a 5.1 file.

mod fmp4;

use std::env;
use std::fs::File;
use std::io::{Seek, SeekFrom, Write};
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::Instant;

use iamf_codecs::DefaultFactory;
use iamf_dec::layout::SoundSystem;
use iamf_dec::profile::ProfileSet;
use iamf_dec::stream::{ChannelOrdering, OutputSampleType, StreamDecoder, StreamSettings};

fn main() -> ExitCode {
    let mut args = env::args().skip(1);
    let mut input = None;
    let mut output = None;
    let mut max_seconds = None;
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "-o" => output = args.next(),
            "--max-seconds" => {
                max_seconds = args.next().and_then(|v| v.parse::<f64>().ok());
                if max_seconds.is_none() {
                    eprintln!("error: --max-seconds needs a number");
                    return ExitCode::from(2);
                }
            }
            "-h" | "--help" => {
                eprintln!(
                    "usage: ys-iamf <input.m4a> -o <output.wav> [--max-seconds N]\n\
                     Renders IAMF to 6-channel 5.1 PCM (L R C LFE Ls Rs). Fails if the\n\
                     decoder does not produce six channels."
                );
                return ExitCode::SUCCESS;
            }
            other if !other.starts_with('-') && input.is_none() => input = Some(other.to_string()),
            other => {
                eprintln!("error: unknown argument {other}");
                return ExitCode::from(2);
            }
        }
    }
    let Some(input) = input else {
        eprintln!("error: missing input file");
        return ExitCode::from(2);
    };
    let Some(output) = output else {
        eprintln!("error: missing -o output.wav");
        return ExitCode::from(2);
    };

    let started = Instant::now();
    let track = match fmp4::read_iamf_mp4(PathBuf::from(&input).as_path()) {
        Ok(track) => track,
        Err(err) => {
            eprintln!("error: {err}");
            return ExitCode::from(1);
        }
    };
    let media_seconds: f64 = track
        .samples
        .iter()
        .map(|s| s.duration as f64)
        .sum::<f64>()
        / f64::from(track.timescale.max(1));
    eprintln!(
        "demux: {} descriptor bytes, {} samples, timescale {}, media {:.3} s",
        track.descriptors.len(),
        track.samples.len(),
        track.timescale,
        media_seconds
    );

    let factory = DefaultFactory;
    let mut settings = StreamSettings::default();
    settings.layout = Some(SoundSystem::B);
    settings.sample_type = Some(OutputSampleType::Int16LittleEndian);
    settings.channel_ordering = ChannelOrdering::Android;
    settings.requested_profiles = ProfileSet::all();
    settings.enable_limiter = false;

    let mut decoder = match StreamDecoder::new_from_descriptors(&track.descriptors, settings, &factory)
    {
        Ok(decoder) => decoder,
        Err(err) => {
            eprintln!("error: decoder rejected the IAMF descriptors: {err}");
            return ExitCode::from(1);
        }
    };
    let channels = decoder.num_output_channels();
    let (mix_id, layout) = decoder.selected_mix();
    eprintln!(
        "decoder: mix_id={mix_id} layout={layout:?} channels={channels} rate={} ordering=WAVE",
        decoder.sample_rate()
    );
    if channels != 6 {
        eprintln!(
            "error: requested 5.1 but the decoder opened {channels} channels. Refusing to write a surround file."
        );
        return ExitCode::from(3);
    }
    let rate = decoder.sample_rate();
    if rate == 0 {
        eprintln!("error: decoder reported a sample rate of 0");
        return ExitCode::from(3);
    }

    let mut wav = match WavWriter::create(&output, channels as u16, rate) {
        Ok(wav) => wav,
        Err(err) => {
            eprintln!("error: cannot create {output}: {err}");
            return ExitCode::from(1);
        }
    };

    let limit_frames = max_seconds.map(|s| (s * f64::from(rate)) as u64);
    let mut written_frames = 0u64;
    let mut errors = 0u32;
    for (index, sample) in track.samples.iter().enumerate() {
        if let Err(err) = decoder.decode(&sample.data) {
            errors += 1;
            if errors <= 5 {
                eprintln!("error: sample {index}: {err}");
            }
            if errors > 32 {
                eprintln!("error: too many decode errors, stopping");
                let _ = wav.finish();
                return ExitCode::from(1);
            }
        }
        while decoder.is_temporal_unit_available() {
            match decoder.get_output_temporal_unit() {
                Ok(Some(pcm)) => {
                    let frames = (pcm.len() / 2) / channels;
                    let take = match limit_frames {
                        Some(limit) if written_frames + frames as u64 > limit => {
                            ((limit - written_frames) as usize) * channels * 2
                        }
                        _ => pcm.len(),
                    };
                    if take > 0 {
                        if let Err(err) = wav.write_bytes(&pcm[..take]) {
                            eprintln!("error: writing wav: {err}");
                            return ExitCode::from(1);
                        }
                        written_frames += (take / 2 / channels) as u64;
                    }
                    if limit_frames.is_some_and(|limit| written_frames >= limit) {
                        break;
                    }
                }
                Ok(None) => break,
                Err(err) => {
                    errors += 1;
                    if errors <= 5 {
                        eprintln!("error: render: {err}");
                    }
                }
            }
        }
        if limit_frames.is_some_and(|limit| written_frames >= limit) {
            break;
        }
        if index % 500 == 0 && index > 0 {
            let audio = written_frames as f64 / f64::from(rate);
            eprintln!(
                "progress: sample {index}/{}  audio {:.1}s  wall {:.1}s",
                track.samples.len(),
                audio,
                started.elapsed().as_secs_f64()
            );
        }
    }
    decoder.signal_end_of_decoding();
    while decoder.is_temporal_unit_available() {
        if limit_frames.is_some_and(|limit| written_frames >= limit) {
            break;
        }
        match decoder.get_output_temporal_unit() {
            Ok(Some(pcm)) => {
                if let Err(err) = wav.write_bytes(&pcm) {
                    eprintln!("error: writing wav: {err}");
                    return ExitCode::from(1);
                }
                written_frames += (pcm.len() / 2 / channels) as u64;
            }
            Ok(None) => break,
            Err(err) => {
                eprintln!("error: flush: {err}");
                break;
            }
        }
    }
    if let Err(err) = wav.finish() {
        eprintln!("error: finishing wav: {err}");
        return ExitCode::from(1);
    }
    if written_frames == 0 {
        eprintln!("error: decoder produced no PCM");
        return ExitCode::from(1);
    }
    let audio = written_frames as f64 / f64::from(rate);
    eprintln!(
        "wrote {output}: {channels} ch, {rate} Hz, {written_frames} frames ({audio:.3} s), decode_errors={errors}, wall {:.2}s",
        started.elapsed().as_secs_f64()
    );
    if errors > 0 {
        eprintln!("error: decode finished with {errors} errors");
        return ExitCode::from(1);
    }
    eprintln!("RESULT=DECODED_6CH");
    ExitCode::SUCCESS
}

struct WavWriter {
    file: File,
    data_bytes: u32,
}

impl WavWriter {
    fn create(path: &str, channels: u16, rate: u32) -> std::io::Result<Self> {
        let mut file = File::create(path)?;
        file.write_all(&header(channels, rate, 0))?;
        Ok(WavWriter {
            file,
            data_bytes: 0,
        })
    }

    fn write_bytes(&mut self, pcm: &[u8]) -> std::io::Result<()> {
        self.file.write_all(pcm)?;
        self.data_bytes = self
            .data_bytes
            .checked_add(pcm.len() as u32)
            .ok_or_else(|| {
                std::io::Error::new(std::io::ErrorKind::InvalidInput, "wav larger than 4 GiB")
            })?;
        Ok(())
    }

    fn finish(mut self) -> std::io::Result<()> {
        self.file.seek(SeekFrom::Start(0))?;
        let channels = 6u16;
        let rate = 48_000u32;
        // The header's rate and channel count were written in create(); rewrite
        // only the size fields so a later reader sees the real length.
        self.file.seek(SeekFrom::Start(4))?;
        self.file.write_all(&(36 + self.data_bytes).to_le_bytes())?;
        self.file.seek(SeekFrom::Start(40))?;
        self.file.write_all(&self.data_bytes.to_le_bytes())?;
        let _ = (channels, rate);
        Ok(())
    }
}

fn header(channels: u16, rate: u32, data_bytes: u32) -> [u8; 44] {
    let mut h = [0u8; 44];
    h[0..4].copy_from_slice(b"RIFF");
    h[4..8].copy_from_slice(&(36 + data_bytes).to_le_bytes());
    h[8..12].copy_from_slice(b"WAVE");
    h[12..16].copy_from_slice(b"fmt ");
    h[16..20].copy_from_slice(&16u32.to_le_bytes());
    h[20..22].copy_from_slice(&1u16.to_le_bytes());
    h[22..24].copy_from_slice(&channels.to_le_bytes());
    h[24..28].copy_from_slice(&rate.to_le_bytes());
    let byte_rate = rate * u32::from(channels) * 2;
    h[28..32].copy_from_slice(&byte_rate.to_le_bytes());
    h[32..34].copy_from_slice(&(channels * 2).to_le_bytes());
    h[34..36].copy_from_slice(&16u16.to_le_bytes());
    h[36..40].copy_from_slice(b"data");
    h[40..44].copy_from_slice(&data_bytes.to_le_bytes());
    h
}

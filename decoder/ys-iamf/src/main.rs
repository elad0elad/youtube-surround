//! Decode an IAMF audio track from a (fragmented) MP4 to 5.1 PCM.
//!
//! The renderer target is ITU-R BS.2051 sound system B: L R C LFE Ls Rs.
//! That is a render of the IAMF mix, not a stereo upmix. If the decoder
//! does not actually produce six channels, this program exits with an error
//! and does not write a 5.1 file.

mod fmp4;

use std::env;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom, Write};
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
    let mut follow = false;
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--follow" => follow = true,
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
                     ys-iamf --follow - -o <output.wav> [--max-seconds N]\n\
                     Renders IAMF to 6-channel 5.1 PCM (L R C LFE Ls Rs). Fails if the\n\
                     decoder does not produce six channels.\n\
                     --follow reads a growing MP4 from stdin and writes PCM as fragments arrive."
                );
                return ExitCode::SUCCESS;
            }
            "-" if input.is_none() => input = Some("-".to_string()),
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

    if follow {
        if input == "-" {
            return follow_stdin(&output, max_seconds);
        }
        return follow_file(&input, &output, max_seconds);
    }

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

fn follow_file(path: &str, output: &str, max_seconds: Option<f64>) -> ExitCode {
    let started = Instant::now();
    let done_path = format!("{path}.done");
    let mut data = Vec::new();
    let mut offset = 0usize;
    let mut decoder: Option<StreamDecoder> = None;
    let mut wav: Option<WavWriter> = None;
    let mut channels = 0usize;
    let mut rate = 0u32;
    let mut written_frames = 0u64;
    let mut errors = 0u32;
    let mut announced = false;
    let mut last_progress = 0u64;
    let mut waited = 0u32;

    loop {
        let mut grew = false;
        if let Ok(mut file) = File::open(path) {
            let len = file.metadata().map(|m| m.len() as usize).unwrap_or(data.len());
            if len > data.len() {
                if file.seek(SeekFrom::Start(data.len() as u64)).is_ok() {
                    let mut rest = vec![0u8; len - data.len()];
                    if let Ok(n) = file.read(&mut rest) {
                        if n > 0 {
                            data.extend_from_slice(&rest[..n]);
                            grew = true;
                        }
                    }
                }
            }
        }
        if grew {
            waited = 0;
            if let Err(code) = drain_buffer(
                &data,
                &mut offset,
                &mut decoder,
                &mut wav,
                output,
                &mut channels,
                &mut rate,
                &mut written_frames,
                &mut errors,
                &mut announced,
                &mut last_progress,
                max_seconds,
                &started,
            ) {
                return code;
            }
        }
        if max_seconds.is_some_and(|seconds| {
            rate > 0 && written_frames as f64 / f64::from(rate) >= seconds
        }) {
            break;
        }
        let finished = std::path::Path::new(&done_path).exists();
        if finished && !grew {
            break;
        }
        if !finished && !std::path::Path::new(path).exists() {
            waited += 1;
            if waited > 400 {
                eprintln!("error: audio file did not appear");
                return ExitCode::from(1);
            }
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    }

    if decoder.is_none() {
        eprintln!("error: no IAMF sample entry arrived");
        eprintln!("RESULT=NO_IAMF");
        return ExitCode::from(1);
    }
    if let Some(decoder) = decoder.as_mut() {
        decoder.signal_end_of_decoding();
        if let Some(wav) = wav.as_mut() {
            if let Err(code) = take_output(
                decoder,
                wav,
                channels,
                &mut written_frames,
                &mut errors,
                max_seconds,
                rate,
            ) {
                return code;
            }
        }
    }
    if let Some(wav) = wav {
        if let Err(err) = wav.finish() {
            eprintln!("error: finishing wav: {err}");
            return ExitCode::from(1);
        }
    }
    if written_frames == 0 {
        eprintln!("error: decoder produced no PCM");
        eprintln!("RESULT=NO_IAMF");
        return ExitCode::from(1);
    }
    let audio = written_frames as f64 / f64::from(rate.max(1));
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

fn follow_stdin(output: &str, max_seconds: Option<f64>) -> ExitCode {
    let started = Instant::now();
    let mut incoming = std::io::stdin();
    let mut data = Vec::new();
    let mut offset = 0usize;
    let mut chunk = [0u8; 64 * 1024];
    let mut decoder: Option<StreamDecoder> = None;
    let mut wav: Option<WavWriter> = None;
    let mut channels = 0usize;
    let mut rate = 0u32;
    let mut written_frames = 0u64;
    let mut errors = 0u32;
    let mut announced = false;
    let mut last_progress = 0u64;

    loop {
        let n = match incoming.read(&mut chunk) {
            Ok(n) => n,
            Err(err) => {
                eprintln!("error: reading audio stream: {err}");
                return ExitCode::from(1);
            }
        };
        if n == 0 {
            break;
        }
        data.extend_from_slice(&chunk[..n]);
        if let Err(code) = drain_buffer(
            &data,
            &mut offset,
            &mut decoder,
            &mut wav,
            output,
            &mut channels,
            &mut rate,
            &mut written_frames,
            &mut errors,
            &mut announced,
            &mut last_progress,
            max_seconds,
            &started,
        ) {
            return code;
        }
        if max_seconds.is_some_and(|seconds| {
            rate > 0 && written_frames as f64 / f64::from(rate) >= seconds
        }) {
            break;
        }
    }

    if decoder.is_none() {
        eprintln!("error: no IAMF sample entry arrived");
        eprintln!("RESULT=NO_IAMF");
        return ExitCode::from(1);
    }
    if let Some(decoder) = decoder.as_mut() {
        decoder.signal_end_of_decoding();
        if let Some(wav) = wav.as_mut() {
            if let Err(code) = take_output(decoder, wav, channels, &mut written_frames, &mut errors, max_seconds, rate)
            {
                return code;
            }
        }
    }
    if let Some(wav) = wav {
        if let Err(err) = wav.finish() {
            eprintln!("error: finishing wav: {err}");
            return ExitCode::from(1);
        }
    }
    if written_frames == 0 {
        eprintln!("error: decoder produced no PCM");
        eprintln!("RESULT=NO_IAMF");
        return ExitCode::from(1);
    }
    let audio = written_frames as f64 / f64::from(rate.max(1));
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

fn drain_buffer(
    data: &[u8],
    offset: &mut usize,
    decoder: &mut Option<StreamDecoder>,
    wav: &mut Option<WavWriter>,
    output: &str,
    channels: &mut usize,
    rate: &mut u32,
    written_frames: &mut u64,
    errors: &mut u32,
    announced: &mut bool,
    last_progress: &mut u64,
    max_seconds: Option<f64>,
    started: &Instant,
) -> Result<(), ExitCode> {
    loop {
        if max_seconds.is_some_and(|seconds| {
            *rate > 0 && *written_frames as f64 / f64::from(*rate) >= seconds
        }) {
            return Ok(());
        }
        match fmp4::poll_box(data, *offset).map_err(|err| {
            eprintln!("error: {err}");
            ExitCode::from(1)
        })? {
            fmp4::Polled::NeedMore => return Ok(()),
            fmp4::Polled::Skip(end) => *offset = end,
            fmp4::Polled::Moov { end, descriptors } => {
                if decoder.is_none() {
                    *decoder = Some(open_decoder(&descriptors)?);
                    let decoder = decoder.as_ref().unwrap();
                    *channels = decoder.num_output_channels();
                    *rate = decoder.sample_rate();
                    let (mix_id, layout) = decoder.selected_mix();
                    eprintln!(
                        "decoder: mix_id={mix_id} layout={layout:?} channels={} rate={} ordering=WAVE",
                        *channels, *rate
                    );
                    if *channels != 6 || *rate == 0 {
                        eprintln!("error: requested 5.1 but the decoder opened {} channels", *channels);
                        return Err(ExitCode::from(3));
                    }
                    match WavWriter::create(output, *channels as u16, *rate) {
                        Ok(writer) => *wav = Some(writer),
                        Err(err) => {
                            eprintln!("error: cannot create {output}: {err}");
                            return Err(ExitCode::from(1));
                        }
                    }
                }
                *offset = end;
            }
            fmp4::Polled::Moof { end, samples } => {
                let Some(decoder) = decoder.as_mut() else {
                    eprintln!("error: media samples arrived before the IAMF descriptors");
                    return Err(ExitCode::from(1));
                };
                let Some(wav) = wav.as_mut() else {
                    return Err(ExitCode::from(1));
                };
                for sample in &samples {
                    if let Err(err) = decoder.decode(&sample.data) {
                        *errors += 1;
                        if *errors <= 5 {
                            eprintln!("error: sample: {err}");
                        }
                        if *errors > 32 {
                            eprintln!("error: too many decode errors, stopping");
                            return Err(ExitCode::from(1));
                        }
                    }
                    if let Err(code) = take_output(
                        decoder,
                        wav,
                        *channels,
                        written_frames,
                        errors,
                        max_seconds,
                        *rate,
                    ) {
                        return Err(code);
                    }
                    note_progress(*written_frames, *rate, announced, last_progress, started);
                    if max_seconds.is_some_and(|seconds| {
                        *rate > 0 && *written_frames as f64 / f64::from(*rate) >= seconds
                    }) {
                        return Ok(());
                    }
                }
                *offset = end;
            }
        }
    }
}

fn open_decoder(descriptors: &[u8]) -> Result<StreamDecoder, ExitCode> {
    let factory = DefaultFactory;
    let mut settings = StreamSettings::default();
    settings.layout = Some(SoundSystem::B);
    settings.sample_type = Some(OutputSampleType::Int16LittleEndian);
    settings.channel_ordering = ChannelOrdering::Android;
    settings.requested_profiles = ProfileSet::all();
    settings.enable_limiter = false;
    match StreamDecoder::new_from_descriptors(descriptors, settings, &factory) {
        Ok(decoder) => Ok(decoder),
        Err(err) => {
            eprintln!("error: decoder rejected the IAMF descriptors: {err}");
            Err(ExitCode::from(1))
        }
    }
}

fn take_output(
    decoder: &mut StreamDecoder,
    wav: &mut WavWriter,
    channels: usize,
    written_frames: &mut u64,
    errors: &mut u32,
    max_seconds: Option<f64>,
    rate: u32,
) -> Result<(), ExitCode> {
    let limit = max_seconds.map(|seconds| (seconds * f64::from(rate.max(1))) as u64);
    while decoder.is_temporal_unit_available() {
        if limit.is_some_and(|limit| *written_frames >= limit) {
            break;
        }
        match decoder.get_output_temporal_unit() {
            Ok(Some(pcm)) => {
                let frames = if channels == 0 { 0 } else { (pcm.len() / 2) / channels };
                let take = match limit {
                    Some(limit) if *written_frames + frames as u64 > limit => {
                        ((limit - *written_frames) as usize) * channels * 2
                    }
                    _ => pcm.len(),
                };
                if take > 0 {
                    if let Err(err) = wav.write_bytes(&pcm[..take]) {
                        eprintln!("error: writing wav: {err}");
                        return Err(ExitCode::from(1));
                    }
                    *written_frames += (take / 2 / channels.max(1)) as u64;
                }
            }
            Ok(None) => break,
            Err(err) => {
                *errors += 1;
                if *errors <= 5 {
                    eprintln!("error: render: {err}");
                }
            }
        }
    }
    Ok(())
}

fn note_progress(written_frames: u64, rate: u32, announced: &mut bool, last_progress: &mut u64, started: &Instant) {
    if rate == 0 {
        return;
    }
    let audio = written_frames as f64 / f64::from(rate);
    if !*announced && audio >= 0.5 {
        eprintln!("ready audio={audio:.2} wall={:.2}", started.elapsed().as_secs_f64());
        let _ = std::io::Write::flush(&mut std::io::stderr());
        *announced = true;
        *last_progress = written_frames;
    } else if *announced && written_frames >= *last_progress + u64::from(rate) {
        eprintln!("progress audio={audio:.1}");
        let _ = std::io::Write::flush(&mut std::io::stderr());
        *last_progress = written_frames;
    }
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

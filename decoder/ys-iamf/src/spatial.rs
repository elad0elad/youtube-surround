//! Side channel for the Windows spatial renderer.
//!
//! The 5.1 WAV path stays on the builtin sound-system B renderer. This
//! capture runs on a second decoder whose target is the richest layout the
//! selected mix presentation actually declares. Channel planes keep that
//! layout's loudspeaker order. Object positions are the decoder's polar
//! coordinates for the temporal unit. Nothing here invents a position.

use std::fs::File;
use std::io::Write;
use std::sync::Arc;

use iamf_dec::layout::SoundSystem;
use iamf_dec::HoaOrder;
use iamf_dec::reconstruct::Reconstructed;
use iamf_dec::render::render;
use iamf_dec::renderer::{ElementFrame, ElementLayout, MixConfig, MixRenderer, MixRendererFactory};
use iamf_dec::DecodeError;

const TYPE_DYNAMIC: u32 = 1;
const TYPE_FRONT_LEFT: u32 = 1 << 1;
const TYPE_FRONT_RIGHT: u32 = 1 << 2;
const TYPE_FRONT_CENTER: u32 = 1 << 3;
const TYPE_LFE: u32 = 1 << 4;
const TYPE_SIDE_LEFT: u32 = 1 << 5;
const TYPE_SIDE_RIGHT: u32 = 1 << 6;
const TYPE_BACK_LEFT: u32 = 1 << 7;
const TYPE_BACK_RIGHT: u32 = 1 << 8;
const TYPE_TOP_FRONT_LEFT: u32 = 1 << 9;
const TYPE_TOP_FRONT_RIGHT: u32 = 1 << 10;
const TYPE_TOP_BACK_LEFT: u32 = 1 << 11;
const TYPE_TOP_BACK_RIGHT: u32 = 1 << 12;
const TYPE_BOTTOM_FRONT_LEFT: u32 = 1 << 13;
const TYPE_BOTTOM_FRONT_RIGHT: u32 = 1 << 14;
const TYPE_BOTTOM_BACK_LEFT: u32 = 1 << 15;
const TYPE_BOTTOM_BACK_RIGHT: u32 = 1 << 16;

#[derive(Clone, Copy)]
struct Speaker {
    azimuth: f32,
    elevation: f32,
    lfe: bool,
}

const fn speaker(azimuth: f32, elevation: f32, lfe: bool) -> Speaker {
    Speaker { azimuth, elevation, lfe }
}

// Loudspeaker angles copied from iamf-rs `olr_speakers` (ITU-R BS.2127
// positions the decoder itself uses). Order is IAMF rendering order.
const MONO: &[Speaker] = &[speaker(0.0, 0.0, false)];
const STEREO: &[Speaker] = &[speaker(30.0, 0.0, false), speaker(-30.0, 0.0, false)];
const BED_51: &[Speaker] = &[
    speaker(30.0, 0.0, false),
    speaker(-30.0, 0.0, false),
    speaker(0.0, 0.0, false),
    speaker(45.0, -30.0, true),
    speaker(110.0, 0.0, false),
    speaker(-110.0, 0.0, false),
];
const BED_512: &[Speaker] = &[
    speaker(30.0, 0.0, false),
    speaker(-30.0, 0.0, false),
    speaker(0.0, 0.0, false),
    speaker(45.0, -30.0, true),
    speaker(110.0, 0.0, false),
    speaker(-110.0, 0.0, false),
    speaker(30.0, 30.0, false),
    speaker(-30.0, 30.0, false),
];
const BED_514: &[Speaker] = &[
    speaker(30.0, 0.0, false),
    speaker(-30.0, 0.0, false),
    speaker(0.0, 0.0, false),
    speaker(45.0, -30.0, true),
    speaker(110.0, 0.0, false),
    speaker(-110.0, 0.0, false),
    speaker(30.0, 30.0, false),
    speaker(-30.0, 30.0, false),
    speaker(110.0, 30.0, false),
    speaker(-110.0, 30.0, false),
];
const BED_71: &[Speaker] = &[
    speaker(30.0, 0.0, false),
    speaker(-30.0, 0.0, false),
    speaker(0.0, 0.0, false),
    speaker(45.0, -30.0, true),
    speaker(90.0, 0.0, false),
    speaker(-90.0, 0.0, false),
    speaker(135.0, 0.0, false),
    speaker(-135.0, 0.0, false),
];
const BED_712: &[Speaker] = &[
    speaker(30.0, 0.0, false),
    speaker(-30.0, 0.0, false),
    speaker(0.0, 0.0, false),
    speaker(45.0, -30.0, true),
    speaker(90.0, 0.0, false),
    speaker(-90.0, 0.0, false),
    speaker(135.0, 0.0, false),
    speaker(-135.0, 0.0, false),
    speaker(45.0, 30.0, false),
    speaker(-45.0, 30.0, false),
];
const BED_714: &[Speaker] = &[
    speaker(30.0, 0.0, false),
    speaker(-30.0, 0.0, false),
    speaker(0.0, 0.0, false),
    speaker(45.0, -30.0, true),
    speaker(90.0, 0.0, false),
    speaker(-90.0, 0.0, false),
    speaker(135.0, 0.0, false),
    speaker(-135.0, 0.0, false),
    speaker(45.0, 30.0, false),
    speaker(-45.0, 30.0, false),
    speaker(135.0, 30.0, false),
    speaker(-135.0, 30.0, false),
];
const BED_312: &[Speaker] = &[
    speaker(30.0, 0.0, false),
    speaker(-30.0, 0.0, false),
    speaker(0.0, 0.0, false),
    speaker(45.0, -30.0, true),
    speaker(45.0, 30.0, false),
    speaker(-45.0, 30.0, false),
];

fn layout_speakers(layout: u8) -> Option<(&'static str, &'static [Speaker])> {
    Some(match layout {
        0 => ("mono", MONO),
        1 => ("stereo", STEREO),
        2 => ("5.1", BED_51),
        3 => ("5.1.2", BED_512),
        4 => ("5.1.4", BED_514),
        5 => ("7.1", BED_71),
        6 => ("7.1.2", BED_712),
        7 => ("7.1.4", BED_714),
        8 => ("3.1.2", BED_312),
        _ => return None,
    })
}

fn system_speakers(system: SoundSystem) -> Option<(&'static str, &'static [Speaker])> {
    let layout = match system {
        SoundSystem::Mono => 0,
        SoundSystem::A => 1,
        SoundSystem::B => 2,
        SoundSystem::C => 3,
        SoundSystem::D => 4,
        SoundSystem::I => 5,
        SoundSystem::Ext712 => 6,
        SoundSystem::J => 7,
        SoundSystem::Ext312 => 8,
        _ => return None,
    };
    layout_speakers(layout)
}

/// A Windows static type only when the loudspeaker angle is that type's
/// angle. Every other loudspeaker stays dynamic and keeps its own angle.
fn static_type(speaker: Speaker) -> u32 {
    if speaker.lfe {
        return TYPE_LFE;
    }
    const KNOWN: &[(f32, f32, u32)] = &[
        (30.0, 0.0, TYPE_FRONT_LEFT),
        (-30.0, 0.0, TYPE_FRONT_RIGHT),
        (0.0, 0.0, TYPE_FRONT_CENTER),
        (90.0, 0.0, TYPE_SIDE_LEFT),
        (-90.0, 0.0, TYPE_SIDE_RIGHT),
        (135.0, 0.0, TYPE_BACK_LEFT),
        (-135.0, 0.0, TYPE_BACK_RIGHT),
        (180.0, 0.0, TYPE_BACK_CENTER_PLACEHOLDER),
        (30.0, 30.0, TYPE_TOP_FRONT_LEFT),
        (-30.0, 30.0, TYPE_TOP_FRONT_RIGHT),
        (45.0, 30.0, TYPE_TOP_FRONT_LEFT),
        (-45.0, 30.0, TYPE_TOP_FRONT_RIGHT),
        (135.0, 30.0, TYPE_TOP_BACK_LEFT),
        (-135.0, 30.0, TYPE_TOP_BACK_RIGHT),
        (45.0, -30.0, TYPE_BOTTOM_FRONT_RIGHT),
        (-45.0, -30.0, TYPE_BOTTOM_FRONT_LEFT),
        (135.0, -30.0, TYPE_BOTTOM_BACK_LEFT),
        (-135.0, -30.0, TYPE_BOTTOM_BACK_RIGHT),
    ];
    for &(az, el, kind) in KNOWN {
        if speaker.azimuth == az && speaker.elevation == el && kind != TYPE_BACK_CENTER_PLACEHOLDER {
            return kind;
        }
    }
    TYPE_DYNAMIC
}

const TYPE_BACK_CENTER_PLACEHOLDER: u32 = u32::MAX;

struct BedChannel {
    kind: u32,
    azimuth: f32,
    elevation: f32,
    distance: f32,
    lfe: bool,
}

struct ObjectSlot {
    element: usize,
    plane: usize,
}

pub struct SpatialFactory {
    path: String,
    target: SoundSystem,
}

impl SpatialFactory {
    pub fn new(path: String, target: SoundSystem) -> Self {
        Self { path, target }
    }
}

impl MixRendererFactory for SpatialFactory {
    fn create(&self, config: &MixConfig<'_>) -> Result<Box<dyn MixRenderer>, DecodeError> {
        Capture::open(&self.path, self.target, config)
    }
}

struct BedRef {
    element: usize,
    plane: usize,
    ambisonics: bool,
    channel: BedChannel,
}

struct Capture {
    file: File,
    target: SoundSystem,
    target_channels: usize,
    beds: Vec<BedRef>,
    objects: Vec<ObjectSlot>,
    hoa_order: Vec<Option<u8>>,
}

impl Capture {
    fn open(path: &str, target: SoundSystem, config: &MixConfig<'_>) -> Result<Box<dyn MixRenderer>, DecodeError> {
        let mut beds = Vec::new();
        let mut objects = Vec::new();
        let mut hoa_order = vec![None; config.elements.len()];
        let mut name = String::from("none");
        let hoa_target = if system_speakers(target).is_some() { target } else { SoundSystem::B };
        for (index, element) in config.elements.iter().enumerate() {
            match element.layout {
                ElementLayout::Channels { loudspeaker_layout } => {
                    let Some((label, speakers)) = layout_speakers(loudspeaker_layout) else {
                        eprintln!("spatial skip element {} layout {loudspeaker_layout}: no verified loudspeaker map", element.audio_element_id);
                        continue;
                    };
                    if name == "none" {
                        name = label.to_string();
                    }
                    push_speakers(&mut beds, index, speakers, false);
                }
                ElementLayout::Objects { num_objects } => {
                    if name == "none" {
                        name = "objects".to_string();
                    }
                    for plane in 0..usize::from(num_objects) {
                        objects.push(ObjectSlot { element: index, plane });
                    }
                }
                ElementLayout::Ambisonics { order } => {
                    let Some((label, speakers)) = system_speakers(hoa_target) else { continue };
                    if name == "none" {
                        name = label.to_string();
                    }
                    hoa_order[index] = Some(order);
                    push_speakers(&mut beds, index, speakers, true);
                }
                _ => {
                    eprintln!("spatial skip element {}: unsupported element layout", element.audio_element_id);
                }
            }
        }
        let heights = beds.iter().filter(|bed| bed.channel.elevation.abs() >= 20.0 && !bed.channel.lfe).count() as u16;
        let mut file = File::create(path).map_err(|err| DecodeError::UnsupportedRenderer(err.to_string()))?;
        file.write_all(b"YSP1").map_err(|err| DecodeError::UnsupportedRenderer(err.to_string()))?;
        file.write_all(&1u16.to_le_bytes()).map_err(io_err)?;
        file.write_all(&config.sample_rate.to_le_bytes()).map_err(io_err)?;
        file.write_all(&(beds.len() as u16).to_le_bytes()).map_err(io_err)?;
        file.write_all(&(objects.len() as u16).to_le_bytes()).map_err(io_err)?;
        file.write_all(&heights.to_le_bytes()).map_err(io_err)?;
        for bed in &beds {
            let bed = &bed.channel;
            file.write_all(&bed.kind.to_le_bytes()).map_err(io_err)?;
            file.write_all(&bed.azimuth.to_le_bytes()).map_err(io_err)?;
            file.write_all(&bed.elevation.to_le_bytes()).map_err(io_err)?;
            file.write_all(&bed.distance.to_le_bytes()).map_err(io_err)?;
            file.write_all(&[u8::from(bed.lfe)]).map_err(io_err)?;
        }
        file.flush().map_err(io_err)?;
        eprintln!(
            "spatial bed={} heights={heights} objects={} name={name} rate={}",
            beds.len(),
            objects.len(),
            config.sample_rate
        );
        let _ = std::io::stderr().flush();
        Ok(Box::new(Capture {
            file,
            target,
            target_channels: target.channels(),
            beds,
            objects,
            hoa_order,
        }))
    }
}

fn push_speakers(beds: &mut Vec<BedRef>, element: usize, speakers: &[Speaker], ambisonics: bool) {
    for (plane, speaker) in speakers.iter().copied().enumerate() {
        beds.push(BedRef {
            element,
            plane,
            ambisonics,
            channel: BedChannel {
                kind: static_type(speaker),
                azimuth: speaker.azimuth,
                elevation: speaker.elevation,
                distance: 1.0,
                lfe: speaker.lfe,
            },
        });
    }
}

fn io_err(err: std::io::Error) -> DecodeError {
    DecodeError::UnsupportedRenderer(err.to_string())
}

impl MixRenderer for Capture {
    fn render(&mut self, elements: &[ElementFrame<'_>]) -> Result<Vec<Vec<f32>>, DecodeError> {
        let frames = elements.iter().find_map(|e| e.planes.first().map(Vec::len)).unwrap_or(0);
        if frames > 0 {
            let mut rendered: Vec<Option<Vec<Vec<f32>>>> = vec![None; elements.len()];
            for (index, order) in self.hoa_order.iter().enumerate() {
                let Some(order) = *order else { continue };
                let Some(element) = elements.get(index) else { continue };
                let target = if system_speakers(self.target).is_some() { self.target } else { SoundSystem::B };
                rendered[index] = ambisonics_planes(order, element.planes, target).ok();
            }
            let mut interleaved = vec![0u8; frames * self.beds.len() * 2];
            for (bed_index, bed) in self.beds.iter().enumerate() {
                let samples = if bed.ambisonics {
                    rendered.get(bed.element).and_then(|item| item.as_ref()).and_then(|planes| planes.get(bed.plane))
                } else {
                    elements.get(bed.element).and_then(|item| item.planes.get(bed.plane))
                };
                for frame in 0..frames {
                    let sample = samples.and_then(|p| p.get(frame)).copied().unwrap_or(0.0);
                    let pcm = quantize(sample);
                    let at = (frame * self.beds.len() + bed_index) * 2;
                    interleaved[at..at + 2].copy_from_slice(&pcm.to_le_bytes());
                }
            }
            self.file.write_all(&(frames as u32).to_le_bytes()).map_err(io_err)?;
            self.file.write_all(&interleaved).map_err(io_err)?;
            for slot in &self.objects {
                let element = elements.get(slot.element);
                let position = element.and_then(|item| item.object_positions.get(slot.plane));
                let (az, el, dist, valid) = match position {
                    Some(pos) => (pos.azimuth, pos.elevation, pos.distance, 1u8),
                    None => (0.0, 0.0, 0.0, 0u8),
                };
                self.file.write_all(&az.to_le_bytes()).map_err(io_err)?;
                self.file.write_all(&el.to_le_bytes()).map_err(io_err)?;
                self.file.write_all(&dist.to_le_bytes()).map_err(io_err)?;
                self.file.write_all(&[valid]).map_err(io_err)?;
                let mut pcm = vec![0u8; frames * 2];
                if valid == 1 {
                    if let Some(samples) = element.and_then(|item| item.planes.get(slot.plane)) {
                        for frame in 0..frames {
                            let sample = samples.get(frame).copied().unwrap_or(0.0);
                            pcm[frame * 2..frame * 2 + 2].copy_from_slice(&quantize(sample).to_le_bytes());
                        }
                    }
                }
                self.file.write_all(&pcm).map_err(io_err)?;
            }
            self.file.flush().map_err(io_err)?;
        }
        Ok(vec![vec![0.0; frames]; self.target_channels])
    }
}

fn quantize(sample: f32) -> i16 {
    (sample.clamp(-1.0, 1.0) * 32767.0) as i16
}

/// Richest loudspeaker layout the selected mix presentation declares.
/// Binaural is not a Windows loudspeaker bed, so it is not chosen here.
pub fn presentation_target(descriptors: &[u8]) -> Result<SoundSystem, String> {
    let parsed = iamf_dec::presentation::Descriptors::collect(descriptors)
        .map_err(|err| err.to_string())?;
    let mut best: Option<SoundSystem> = None;
    let mut best_channels = 0usize;
    for mix in &parsed.mix_presentations {
        for sub in &mix.sub_mixes {
            for (layout, _) in &sub.layouts {
                let system = match layout {
                    iamf_obu::descriptors::Layout::LoudspeakersSsConvention { sound_system } => {
                        SoundSystem::from_bitstream_sound_system(*sound_system)
                    }
                    _ => None,
                };
                let Some(system) = system else { continue };
                if system == SoundSystem::Binaural {
                    continue;
                }
                let channels = system.channels();
                if channels > best_channels {
                    best = Some(system);
                    best_channels = channels;
                }
            }
        }
    }
    best.ok_or_else(|| "the mix presentation declares no loudspeaker layout".to_string())
}

pub fn ambisonics_planes(order: u8, planes: &[Vec<f32>], target: SoundSystem) -> Result<Vec<Vec<f32>>, DecodeError> {
    let order = match order {
        0 => HoaOrder::Zoa,
        1 => HoaOrder::Foa,
        2 => HoaOrder::Soa,
        3 => HoaOrder::Toa,
        4 => HoaOrder::H4a,
        _ => return Err(DecodeError::Unimplemented("ambisonics order above 4")),
    };
    let reconstructed = Reconstructed::Hoa { order, planar: planes.to_vec() };
    render(&reconstructed, target.matrix_layout())
}

pub fn factory(path: String, target: SoundSystem) -> Arc<SpatialFactory> {
    Arc::new(SpatialFactory::new(path, target))
}

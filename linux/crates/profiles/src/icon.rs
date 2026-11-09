//! A shortcut's icon: Claude's own, turned to the shortcut's colour and
//! badged where its preset says so.
//!
//! Claude's icon is borrowed from the installed package rather than shipped,
//! so a shortcut looks like the Claude it opens. Inside the Flatpak `/usr` is
//! the runtime's, so the icon is read on the host. A machine where none can be
//! found still gets a shortcut, drawn from the plain icon in `assets/`.

use graft_core::{claude, host, IconPreset};
use std::path::{Path, PathBuf};
use std::sync::RwLock;

/// The size written into `hicolor/256x256/apps`.
pub const PIXELS: u32 = 256;
/// The size the window's colour picker shows.
pub const PREVIEW_PIXELS: u32 = 96;

const FALLBACK_SVG: &str = include_str!("../assets/fallback-icon.svg");

static SOURCE_OVERRIDE: RwLock<Option<PathBuf>> = RwLock::new(None);

/// Draw from this image instead of Claude's. For the suite, which must not
/// depend on which Claude is installed, and which also uses it to make an
/// icon that cannot be read.
pub fn set_source_override(path: Option<PathBuf>) {
    *SOURCE_OVERRIDE.write().unwrap() = path;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IconError {
    /// The image named to draw from could not be read.
    Missing,
    /// It was read and could not be drawn.
    Failed,
}

enum Source {
    Png(Vec<u8>),
    Fallback,
}

/// Where the installed Claude keeps its icon. The repackagings install their
/// resources beside the main binary, in a folder named after the launcher.
fn host_candidates() -> Vec<PathBuf> {
    let mut stems: Vec<String> = Vec::new();
    let launchers = claude::launcher().into_iter().chain(claude::LAUNCHER_CANDIDATES.iter().map(PathBuf::from));
    for launcher in launchers {
        if let Some(stem) = launcher.file_name().map(|n| n.to_string_lossy().into_owned()) {
            if !stems.contains(&stem) {
                stems.push(stem);
            }
        }
    }
    let mut out: Vec<PathBuf> = Vec::new();
    for stem in &stems {
        out.push(PathBuf::from(format!("/usr/lib/{stem}/resources/icon.png")));
        out.push(PathBuf::from(format!("/usr/share/icons/hicolor/512x512/apps/{stem}.png")));
        out.push(PathBuf::from(format!("/usr/share/icons/hicolor/256x256/apps/{stem}.png")));
    }
    // Any other repackaging under its own name. Listed through the host's
    // shell, since the sandbox's `/usr/lib` is the runtime's.
    for line in host::output("sh", &["-c", "ls -d /usr/lib/claude-desktop*/resources/icon.png 2>/dev/null"]).lines() {
        let path = PathBuf::from(line.trim());
        if !line.trim().is_empty() && !out.contains(&path) {
            out.push(path);
        }
    }
    out
}

fn source() -> Result<Source, IconError> {
    if let Some(path) = SOURCE_OVERRIDE.read().unwrap().clone() {
        return std::fs::read(&path).map(Source::Png).map_err(|_| IconError::Missing);
    }
    for candidate in host_candidates() {
        if let Some(bytes) = host::host_read(&candidate) {
            if image::load_from_memory(&bytes).is_ok() {
                return Ok(Source::Png(bytes));
            }
        }
    }
    Ok(Source::Fallback)
}

/// The icon for a preset at a size, as a PNG.
pub fn png(preset: IconPreset, pixels: u32) -> Result<Vec<u8>, IconError> {
    let image = render(preset, pixels)?;
    let mut out = Vec::new();
    image::DynamicImage::ImageRgba8(image)
        .write_to(&mut std::io::Cursor::new(&mut out), image::ImageFormat::Png)
        .map_err(|_| IconError::Failed)?;
    Ok(out)
}

/// What the colour picker shows for a preset, or `None` when nothing can be
/// drawn.
pub fn preview(preset: IconPreset) -> Option<Vec<u8>> {
    png(preset, PREVIEW_PIXELS).ok()
}

pub fn render(preset: IconPreset, pixels: u32) -> Result<image::RgbaImage, IconError> {
    let mut base = match source()? {
        Source::Png(bytes) => {
            let decoded = image::load_from_memory(&bytes).map_err(|_| IconError::Failed)?.to_rgba8();
            // Filling the whole canvas, whatever size Claude's icon is, so a
            // preview is never one corner of a larger square.
            image::imageops::resize(&decoded, pixels, pixels, image::imageops::FilterType::Lanczos3)
        }
        Source::Fallback => svg_image(FALLBACK_SVG, 256.0, pixels)?,
    };
    if preset.hue_angle().is_some() || preset.saturation() != 1.0 {
        recolour(&mut base, preset.hue_angle(), preset.saturation());
    }
    match badge_svg(preset, pixels) {
        Some(svg) => overlay(base, &svg),
        None => Ok(base),
    }
}

/// The hue rotation and saturation the Mac build gets from Core Image, as the
/// colour matrices SVG and CSS define for `hue-rotate` and `saturate`, which
/// turn Claude's orange the same way round for the same angle.
fn recolour(image: &mut image::RgbaImage, angle: Option<f64>, saturation: f64) {
    let hue = angle.map(|a| {
        let (s, c) = a.sin_cos();
        [
            [0.213 + c * 0.787 - s * 0.213, 0.715 - c * 0.715 - s * 0.715, 0.072 - c * 0.072 + s * 0.928],
            [0.213 - c * 0.213 + s * 0.143, 0.715 + c * 0.285 + s * 0.140, 0.072 - c * 0.072 - s * 0.283],
            [0.213 - c * 0.213 - s * 0.787, 0.715 - c * 0.715 + s * 0.715, 0.072 + c * 0.928 + s * 0.072],
        ]
    });
    for pixel in image.pixels_mut() {
        let mut rgb = [pixel[0] as f64 / 255.0, pixel[1] as f64 / 255.0, pixel[2] as f64 / 255.0];
        if let Some(m) = hue {
            rgb = [
                m[0][0] * rgb[0] + m[0][1] * rgb[1] + m[0][2] * rgb[2],
                m[1][0] * rgb[0] + m[1][1] * rgb[1] + m[1][2] * rgb[2],
                m[2][0] * rgb[0] + m[2][1] * rgb[1] + m[2][2] * rgb[2],
            ];
        }
        if saturation != 1.0 {
            let luma = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
            rgb = rgb.map(|v| luma + saturation * (v - luma));
        }
        for (i, value) in rgb.iter().enumerate() {
            pixel[i] = (value.clamp(0.0, 1.0) * 255.0).round() as u8;
        }
    }
}

/// White disk, the accent inside it, and a white glyph, in the corner and at
/// the proportions the Mac build draws them.
fn badge_svg(preset: IconPreset, pixels: u32) -> Option<String> {
    let glyph = match preset {
        IconPreset::Work => {
            r#"<path fill-rule="evenodd" fill="white" d="M6 1.5h4A1.5 1.5 0 0 1 11.5 3v1H14a1 1 0 0 1 1 1v8.5a1 1 0 0 1-1 1H2a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h2.5V3A1.5 1.5 0 0 1 6 1.5zM6 3v1h4V3z"/>"#
        }
        IconPreset::Personal => {
            r#"<circle fill="white" cx="8" cy="4.5" r="3.2"/><path fill="white" d="M1.5 15c0-3.6 2.9-6 6.5-6s6.5 2.4 6.5 6z"/>"#
        }
        IconPreset::Code => {
            r#"<g fill="none" stroke="white" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 3.5l5 4.5-5 4.5"/><path d="M9 13h5"/></g>"#
        }
        IconPreset::Research => {
            r#"<g fill="none" stroke="white" stroke-width="2.2" stroke-linecap="round"><circle cx="6.5" cy="6.5" r="4.5"/><path d="M10 10l4.5 4.5"/></g>"#
        }
        _ => return None,
    };
    let side = pixels as f64;
    let (cx, cy) = (side * 0.775, side * 0.78);
    let outer = side * 0.145;
    let inner = outer - side * 0.016;
    let glyph_side = side * 0.135;
    let (r, g, b) = preset.accent_rgb();
    let hex = |v: f64| (v.clamp(0.0, 1.0) * 255.0).round() as u8;
    Some(format!(
        r##"<svg xmlns="http://www.w3.org/2000/svg" width="{side}" height="{side}" viewBox="0 0 {side} {side}">
<circle cx="{cx}" cy="{cy}" r="{outer}" fill="#ffffff" fill-opacity="0.96"/>
<circle cx="{cx}" cy="{cy}" r="{inner}" fill="#{:02x}{:02x}{:02x}"/>
<g transform="translate({gx} {gy}) scale({scale})">{glyph}</g>
</svg>"##,
        hex(r),
        hex(g),
        hex(b),
        gx = cx - glyph_side / 2.0,
        gy = cy - glyph_side / 2.0,
        scale = glyph_side / 16.0,
    ))
}

fn tree(svg: &str) -> Result<resvg::usvg::Tree, IconError> {
    resvg::usvg::Tree::from_str(svg, &resvg::usvg::Options::default()).map_err(|_| IconError::Failed)
}

fn svg_image(svg: &str, drawn_at: f32, pixels: u32) -> Result<image::RgbaImage, IconError> {
    let tree = tree(svg)?;
    let mut pixmap = resvg::tiny_skia::Pixmap::new(pixels, pixels).ok_or(IconError::Failed)?;
    let scale = pixels as f32 / drawn_at;
    resvg::render(&tree, resvg::tiny_skia::Transform::from_scale(scale, scale), &mut pixmap.as_mut());
    Ok(demultiplied(&pixmap))
}

fn overlay(base: image::RgbaImage, svg: &str) -> Result<image::RgbaImage, IconError> {
    let (w, h) = base.dimensions();
    let size = resvg::tiny_skia::IntSize::from_wh(w, h).ok_or(IconError::Failed)?;
    let mut data = base.into_raw();
    for px in data.as_chunks_mut::<4>().0 {
        let a = px[3] as u32;
        for c in &mut px[..3] {
            *c = ((*c as u32 * a + 127) / 255) as u8;
        }
    }
    let mut pixmap = resvg::tiny_skia::Pixmap::from_vec(data, size).ok_or(IconError::Failed)?;
    resvg::render(&tree(svg)?, resvg::tiny_skia::Transform::identity(), &mut pixmap.as_mut());
    Ok(demultiplied(&pixmap))
}

fn demultiplied(pixmap: &resvg::tiny_skia::Pixmap) -> image::RgbaImage {
    let mut data = pixmap.data().to_vec();
    for px in data.as_chunks_mut::<4>().0 {
        let a = px[3] as u32;
        for c in &mut px[..3] {
            *c = (*c as u32 * 255 + a / 2).checked_div(a).unwrap_or(0).min(255) as u8;
        }
    }
    image::RgbaImage::from_raw(pixmap.width(), pixmap.height(), data).unwrap_or_default()
}

/// Whether a file is a PNG that decodes, for the checks that an icon written
/// is one a desktop can show.
pub fn is_readable_png(path: &Path) -> bool {
    std::fs::read(path)
        .ok()
        .and_then(|b| image::load_from_memory_with_format(&b, image::ImageFormat::Png).ok())
        .is_some()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testing::World;

    fn centre(png: &[u8]) -> image::Rgba<u8> {
        let image = image::load_from_memory(png).unwrap().to_rgba8();
        *image.get_pixel(image.width() / 2, image.height() / 2)
    }

    #[test]
    fn the_preview_fills_its_canvas_instead_of_shrinking_into_one_corner() {
        let _world = World::new();
        let preview = preview(IconPreset::Blue).unwrap();
        let image = image::load_from_memory(&preview).unwrap().to_rgba8();
        assert_eq!(image.dimensions(), (PREVIEW_PIXELS, PREVIEW_PIXELS));
        assert!(image.get_pixel(75, 75)[3] > 128, "a point three quarters across is still inside the icon");
    }

    #[test]
    fn a_colour_turns_claude_s_orange_and_the_original_leaves_it() {
        let _world = World::new();
        let original = centre(&png(IconPreset::Original, 64).unwrap());
        assert!(original[0] > original[2], "the original stays orange");
        let blue = centre(&png(IconPreset::Blue, 64).unwrap());
        assert!(blue[2] > blue[0], "blue is blue");
        let green = centre(&png(IconPreset::Green, 64).unwrap());
        assert!(green[1] > green[0] && green[1] > green[2], "green is green");
        let graphite = centre(&png(IconPreset::Graphite, 64).unwrap());
        assert!(graphite[0].abs_diff(graphite[2]) <= 2, "graphite has no colour at all");
    }

    #[test]
    fn a_badged_preset_differs_from_its_plain_colour_only_in_the_corner() {
        let _world = World::new();
        let plain = image::load_from_memory(&png(IconPreset::Blue, 128).unwrap()).unwrap().to_rgba8();
        let badged = image::load_from_memory(&png(IconPreset::Work, 128).unwrap()).unwrap().to_rgba8();
        assert_eq!(plain.get_pixel(40, 40), badged.get_pixel(40, 40), "away from the badge they are the same");
        assert_ne!(plain.get_pixel(99, 99), badged.get_pixel(99, 99), "and the badge sits in the lower right");
    }

    #[test]
    fn with_no_claude_icon_to_borrow_the_plain_one_is_drawn() {
        let _world = World::new();
        set_source_override(None);
        // Inside the suite's sandbox the host's /usr cannot be read, which is
        // exactly a machine with no Claude icon to find.
        let image = render(IconPreset::Original, 64).unwrap();
        assert!(image.get_pixel(32, 32)[3] > 200, "the fallback fills the icon");
    }

    #[test]
    fn an_unreadable_icon_is_reported_rather_than_drawn_blank() {
        let world = World::new();
        set_source_override(Some(world.sandbox.root.path().join("missing.png")));
        assert_eq!(png(IconPreset::Blue, 64), Err(IconError::Missing));
        let broken = world.sandbox.root.path().join("broken.png");
        std::fs::write(&broken, b"not a png").unwrap();
        set_source_override(Some(broken));
        assert_eq!(png(IconPreset::Blue, 64), Err(IconError::Failed));
    }
}

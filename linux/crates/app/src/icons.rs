//! The pictures a shortcut wears, drawn by the code that draws its launcher's
//! icon, so the window and the tray show what the person picked rather than
//! a second likeness of it.

use crate::shared::{emit, Topic};
use graft_core::IconPreset;
use gtk::gdk;
use gtk::glib;
use adw::prelude::*;
use std::cell::RefCell;
use std::collections::HashMap;

/// Graft's own mark, shown in the tray when no Claude in front is.
pub const OWN_ICON: &[u8] = include_bytes!("../../../flatpak/io.github.aaditya_v_more.ClaudeGraft.png");

/// How large the tray's pictures are drawn. Hosts scale them to the panel;
/// one size large enough for a high-density panel serves every host.
pub const TRAY_PIXELS: u32 = 64;

thread_local! {
    static PREVIEWS: RefCell<HashMap<IconPreset, gdk::Texture>> = RefCell::new(HashMap::new());
}

/// Every preset drawn once, off the main loop: drawing filters the whole of
/// Claude's icon. Pages showing an icon redraw when they arrive.
pub fn load_previews() {
    crate::ui::off_main(
        || {
            IconPreset::ALL
                .iter()
                .filter_map(|p| graft_profiles::icon::preview(*p).map(|png| (*p, png)))
                .collect::<Vec<_>>()
        },
        |drawn| {
            PREVIEWS.with(|cache| {
                let mut cache = cache.borrow_mut();
                for (preset, png) in drawn {
                    if let Ok(texture) = gdk::Texture::from_bytes(&glib::Bytes::from_owned(png)) {
                        cache.insert(preset, texture);
                    }
                }
            });
            emit(Topic::Store);
        },
    );
}

pub fn texture(preset: IconPreset) -> Option<gdk::Texture> {
    PREVIEWS.with(|cache| cache.borrow().get(&preset).cloned())
}

/// The preset's picture at `size`, or a stand-in until it has been drawn.
pub fn image(preset: IconPreset, size: i32) -> gtk::Image {
    let image = match texture(preset) {
        Some(texture) => gtk::Image::from_paintable(Some(&texture)),
        None => gtk::Image::from_icon_name("application-x-executable-symbolic"),
    };
    image.set_pixel_size(size);
    image.set_accessible_role(gtk::AccessibleRole::Presentation);
    image
}

/// RGBA, as an image holds it, to the ARGB in network order a tray takes.
pub fn argb(rgba: &[u8]) -> Vec<u8> {
    let mut data = rgba.to_vec();
    for pixel in data.as_chunks_mut::<4>().0 {
        pixel.rotate_right(1);
    }
    data
}

/// A preset's picture for the tray. Blocking: drawn on the thread that hands
/// the tray its pictures, never the main loop.
pub fn tray_icon(preset: IconPreset) -> Option<ksni::Icon> {
    let image = graft_profiles::icon::render(preset, TRAY_PIXELS).ok()?;
    let (width, height) = (image.width() as i32, image.height() as i32);
    Some(ksni::Icon { width, height, data: argb(&image.into_raw()) })
}

pub fn own_tray_icon() -> Option<ksni::Icon> {
    let decoded = image::load_from_memory(OWN_ICON).ok()?.to_rgba8();
    let small = image::imageops::resize(&decoded, TRAY_PIXELS, TRAY_PIXELS, image::imageops::FilterType::Lanczos3);
    Some(ksni::Icon { width: TRAY_PIXELS as i32, height: TRAY_PIXELS as i32, data: argb(&small.into_raw()) })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_tray_picture_is_handed_over_in_the_order_the_tray_reads() {
        assert_eq!(argb(&[1, 2, 3, 4, 5, 6, 7, 8]), vec![4, 1, 2, 3, 8, 5, 6, 7], "alpha first, then red, green and blue");
        let own = own_tray_icon().expect("Graft's own mark decodes");
        assert_eq!((own.width, own.height), (TRAY_PIXELS as i32, TRAY_PIXELS as i32));
        assert_eq!(own.data.len(), (TRAY_PIXELS * TRAY_PIXELS * 4) as usize);
    }
}

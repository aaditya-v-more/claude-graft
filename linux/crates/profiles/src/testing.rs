//! What every test here starts from: a sandboxed home, host commands kept
//! inside the suite's own sandbox, and a launcher and icon that exist.

use graft_core::{host, paths, testing::Sandbox};
use std::path::PathBuf;

pub struct World {
    pub sandbox: Sandbox,
}

impl World {
    pub fn new() -> World {
        let sandbox = Sandbox::new();
        // Inside the SDK the host is out of reach, and directly the process
        // list holds only the suite, so no real Claude is seen as running.
        host::set_run_directly(true);
        let launcher = sandbox.root.path().join("bin/graft-launch");
        std::fs::create_dir_all(launcher.parent().unwrap()).unwrap();
        std::fs::write(&launcher, "#!/bin/sh\nexit 0\n").unwrap();
        crate::installer::set_launcher_override(Some(launcher));
        let stock = sandbox.root.path().join("stock-icon.png");
        std::fs::write(&stock, stock_png()).unwrap();
        crate::icon::set_source_override(Some(stock));
        World { sandbox }
    }
}

/// An orange rounded square standing for Claude's icon, so the suite never
/// depends on which Claude is installed.
pub fn stock_png() -> Vec<u8> {
    let side = 128u32;
    let image = image::RgbaImage::from_fn(side, side, |x, y| {
        let inset = |v: u32| v >= 4 && v < side - 4;
        if inset(x) && inset(y) {
            image::Rgba([217, 92, 61, 255])
        } else {
            image::Rgba([0, 0, 0, 0])
        }
    });
    let mut out = Vec::new();
    image::DynamicImage::ImageRgba8(image)
        .write_to(&mut std::io::Cursor::new(&mut out), image::ImageFormat::Png)
        .unwrap();
    out
}

pub fn make_profile(name: &str, account: Option<&str>) -> PathBuf {
    let profile = paths::profile_dir(name);
    std::fs::create_dir_all(&profile).unwrap();
    if let Some(account) = account {
        let config = serde_json::json!({ "lastKnownAccountUuid": account });
        std::fs::write(profile.join("config.json"), config.to_string()).unwrap();
    }
    profile
}

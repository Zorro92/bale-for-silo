//! Subtitle overlay (subtitle ABI, `bale_sub_*` in `lib.rs`): a loaded ASS track is rendered by
//! libass at the media clock and its images are drawn on the GPU into a transparent composition
//! swap chain that a XAML `SwapChainPanel` shows above the video. This file holds the
//! platform-independent parts (ABI types, clock, layout and atlas packing); `overlay` is the
//! Direct3D 11 renderer and its thread.

#[cfg(windows)]
pub mod overlay;

use std::time::Duration;

/// Returned by `bale_sub_abi_version`; bumped whenever a `bale_sub_*` export changes.
pub const SUB_ABI_VERSION: u32 = 1;

pub const S_OK: i32 = 0;
pub const E_NOTIMPL: i32 = 0x8000_4001u32 as i32;
pub const E_POINTER: i32 = 0x8000_4003u32 as i32;
pub const E_FAIL: i32 = 0x8000_4005u32 as i32;
pub const E_UNEXPECTED: i32 = 0x8000_ffffu32 as i32;
pub const E_INVALIDARG: i32 = 0x8007_0057u32 as i32;

/// Largest swap chain side accepted (Direct3D 11's texture limit).
pub const MAX_FRAME_SIZE: u32 = 16384;

/// Counters and timings of one overlay, as read by `bale_sub_overlay_stats`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BaleSubStats {
    /// Frames presented to the swap chain.
    pub presents: u64,
    /// `ass_render_frame` calls.
    pub renders: u64,
    /// libass render + upload + draw time of the last changed frame.
    pub last_render_us: u32,
    /// Largest such time since the track loaded (shows font first-use hitches).
    pub max_render_us: u32,
    /// `ASS_Image` count of the last changed frame.
    pub images: u32,
    /// Events in the loaded track (0 = none loaded).
    pub events: u32,
    /// Swap chain size in physical pixels.
    pub width: u32,
    pub height: u32,
}

/// The media clock as last reported by the player.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Clock {
    pub position_ms: i64,
    pub rate: f64,
    pub playing: bool,
}

impl Clock {
    /// Subtitle time `elapsed` after the clock was reported: the position itself while paused,
    /// otherwise advanced by the elapsed wall time times the playback rate.
    pub fn at(&self, elapsed: Duration) -> i64 {
        if !self.playing {
            return self.position_ms;
        }
        // Float-to-int `as` saturates, and NaN (rejected by the ABI anyway) becomes 0.
        let advance = (elapsed.as_secs_f64() * 1000.0 * self.rate).round() as i64;
        self.position_ms.saturating_add(advance)
    }
}

/// What `bale_sub_overlay_set_layout` asks for, all in physical pixels.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Layout {
    pub width: u32,
    pub height: u32,
    pub scale_x: f32,
    pub scale_y: f32,
    pub video_x: f32,
    pub video_y: f32,
    pub video_w: f32,
    pub video_h: f32,
}

impl Layout {
    /// The whole frame is video, at scale 1.
    pub fn full(width: u32, height: u32) -> Self {
        Layout {
            width,
            height,
            scale_x: 1.0,
            scale_y: 1.0,
            video_x: 0.0,
            video_y: 0.0,
            video_w: width as f32,
            video_h: height as f32,
        }
    }

    pub fn is_valid(&self) -> bool {
        let finite = [
            self.scale_x,
            self.scale_y,
            self.video_x,
            self.video_y,
            self.video_w,
            self.video_h,
        ]
        .iter()
        .all(|value| value.is_finite());
        finite
            && (1..=MAX_FRAME_SIZE).contains(&self.width)
            && (1..=MAX_FRAME_SIZE).contains(&self.height)
            && self.scale_x > 0.0
            && self.scale_y > 0.0
            && self.video_w >= 0.0
            && self.video_h >= 0.0
    }

    /// The video rectangle's distances to the frame edges, `[top, bottom, left, right]`, for
    /// `ass_set_margins`. A video rectangle that sticks out of the frame gives 0 on that side.
    pub fn margins(&self) -> [i32; 4] {
        let edge = |value: f32, limit: u32| value.round().clamp(0.0, limit as f32) as i32;
        let left = edge(self.video_x, self.width);
        let top = edge(self.video_y, self.height);
        let right = self.width as i32 - edge(self.video_x + self.video_w, self.width);
        let bottom = self.height as i32 - edge(self.video_y + self.video_h, self.height);
        [top, bottom, left, right]
    }

    /// The video size for `ass_set_storage_size`; an empty rectangle falls back to the frame.
    pub fn storage_size(&self) -> (i32, i32) {
        let width = self.video_w.round() as i32;
        let height = self.video_h.round() as i32;
        if width >= 1 && height >= 1 {
            (width, height)
        } else {
            (self.width as i32, self.height as i32)
        }
    }
}

/// libass colour (`0xRRGGBBAA`, AA = transparency) as premultiplied RGBA floats.
pub fn premultiplied(color: u32) -> [f32; 4] {
    let [r, g, b, transparency] = color.to_be_bytes();
    let alpha = f32::from(255 - transparency) / 255.0;
    [
        f32::from(r) / 255.0 * alpha,
        f32::from(g) / 255.0 * alpha,
        f32::from(b) / 255.0 * alpha,
        alpha,
    ]
}

/// Initial coverage atlas size; [`atlas_size_for`] grows it up to [`ATLAS_MAX`] per side.
pub const ATLAS_START: (u32, u32) = (2048, 1024);
pub const ATLAS_MAX: u32 = 4096;

/// Shelf packer: rectangles go left to right on a shelf as tall as its tallest rectangle; a full
/// shelf starts the next one below it.
#[derive(Clone, Copy, Debug)]
pub struct Shelves {
    width: u32,
    height: u32,
    x: u32,
    y: u32,
    shelf: u32,
}

impl Shelves {
    pub fn new(width: u32, height: u32) -> Self {
        Shelves {
            width,
            height,
            x: 0,
            y: 0,
            shelf: 0,
        }
    }

    /// Top-left corner for a `w`×`h` rectangle, or `None` when the atlas is full.
    pub fn place(&mut self, w: u32, h: u32) -> Option<(u32, u32)> {
        if w == 0 || h == 0 || w > self.width || h > self.height {
            return None;
        }
        if self.x + w > self.width {
            self.y += self.shelf;
            self.x = 0;
            self.shelf = 0;
        }
        if self.y + h > self.height {
            return None;
        }
        let at = (self.x, self.y);
        self.x += w;
        self.shelf = self.shelf.max(h);
        Some(at)
    }
}

/// Packs as many of `sizes` as fit, in order, into a `width`×`height` atlas: the positions of the
/// leading rectangles that fit. Drawing order matters, so packing stops at the first that does not.
pub fn pack_prefix(sizes: &[(u32, u32)], width: u32, height: u32) -> Vec<(u32, u32)> {
    let mut shelves = Shelves::new(width, height);
    sizes
        .iter()
        .map_while(|&(w, h)| shelves.place(w, h))
        .collect()
}

/// The atlas size to use for `sizes`: `current`, widened until the widest rectangle fits, then
/// doubled (height first while it is the shorter side) until everything fits or both sides reach
/// [`ATLAS_MAX`]. What still does not fit is drawn in further batches.
pub fn atlas_size_for(sizes: &[(u32, u32)], current: (u32, u32)) -> (u32, u32) {
    let (mut width, mut height) = current;
    let widest = sizes.iter().map(|size| size.0).max().unwrap_or(0);
    while width < widest.min(ATLAS_MAX) {
        width = (width * 2).min(ATLAS_MAX);
    }
    while pack_prefix(sizes, width, height).len() < sizes.len()
        && (width < ATLAS_MAX || height < ATLAS_MAX)
    {
        if (height <= width && height < ATLAS_MAX) || width >= ATLAS_MAX {
            height = (height * 2).min(ATLAS_MAX);
        } else {
            width = (width * 2).min(ATLAS_MAX);
        }
    }
    (width, height)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stats_layout_matches_the_abi() {
        assert_eq!(size_of::<BaleSubStats>(), 40);
        assert_eq!(align_of::<BaleSubStats>(), 8);
        assert_eq!(std::mem::offset_of!(BaleSubStats, last_render_us), 16);
        assert_eq!(std::mem::offset_of!(BaleSubStats, height), 36);
    }

    #[test]
    fn clock_holds_position_while_paused() {
        let clock = Clock {
            position_ms: 2000,
            rate: 1.0,
            playing: false,
        };
        assert_eq!(clock.at(Duration::ZERO), 2000);
        assert_eq!(clock.at(Duration::from_secs(30)), 2000);
    }

    #[test]
    fn clock_extrapolates_by_rate_while_playing() {
        let mut clock = Clock {
            position_ms: 10_000,
            rate: 1.0,
            playing: true,
        };
        assert_eq!(clock.at(Duration::ZERO), 10_000);
        assert_eq!(clock.at(Duration::from_millis(250)), 10_250);
        clock.rate = 1.5;
        assert_eq!(clock.at(Duration::from_millis(1000)), 11_500);
        clock.rate = 0.5;
        assert_eq!(clock.at(Duration::from_micros(3000)), 10_002);
        clock.rate = 0.0;
        assert_eq!(clock.at(Duration::from_secs(5)), 10_000);
        clock.rate = -1.0;
        assert_eq!(clock.at(Duration::from_secs(1)), 9_000);
    }

    #[test]
    fn clock_saturates_instead_of_overflowing() {
        let clock = Clock {
            position_ms: i64::MAX - 5,
            rate: 1e300,
            playing: true,
        };
        assert_eq!(clock.at(Duration::from_secs(1)), i64::MAX);
    }

    #[test]
    fn margins_are_the_video_rect_distances_to_the_frame() {
        // 2.39:1 film letterboxed in a 4K panel.
        let layout = Layout {
            video_y: 277.0,
            video_h: 1606.0,
            ..Layout::full(3840, 2160)
        };
        assert_eq!(layout.margins(), [277, 277, 0, 0]);
        assert_eq!(layout.storage_size(), (3840, 1606));
        // 4:3 pillarboxed.
        let layout = Layout {
            video_x: 240.0,
            video_w: 1440.0,
            ..Layout::full(1920, 1080)
        };
        assert_eq!(layout.margins(), [0, 0, 240, 240]);
        assert_eq!(Layout::full(640, 360).margins(), [0; 4]);
    }

    #[test]
    fn margins_never_go_negative_for_an_overhanging_video() {
        let layout = Layout {
            video_x: -100.0,
            video_y: -50.0,
            video_w: 2200.0,
            video_h: 1200.0,
            ..Layout::full(1920, 1080)
        };
        assert_eq!(layout.margins(), [0; 4]);
        let empty = Layout {
            video_w: 0.0,
            video_h: 0.0,
            ..Layout::full(1920, 1080)
        };
        assert_eq!(empty.storage_size(), (1920, 1080));
    }

    #[test]
    fn layout_validation_rejects_bad_sizes_and_scales() {
        assert!(Layout::full(1, 1).is_valid());
        assert!(!Layout::full(0, 360).is_valid());
        assert!(!Layout::full(MAX_FRAME_SIZE + 1, 360).is_valid());
        for scale in [0.0, -1.0, f32::NAN, f32::INFINITY] {
            let layout = Layout {
                scale_x: scale,
                ..Layout::full(640, 360)
            };
            assert!(!layout.is_valid(), "scale {scale}");
        }
        let layout = Layout {
            video_w: f32::NAN,
            ..Layout::full(640, 360)
        };
        assert!(!layout.is_valid());
    }

    #[test]
    fn colour_is_premultiplied_by_inverse_transparency() {
        assert_eq!(premultiplied(0xFFFF_FF00), [1.0, 1.0, 1.0, 1.0]);
        assert_eq!(premultiplied(0xFF00_00FF), [0.0, 0.0, 0.0, 0.0]);
        let [r, g, b, a] = premultiplied(0xFF80_0080);
        let alpha = 127.0 / 255.0;
        assert!((a - alpha).abs() < 1e-6);
        assert!((r - alpha).abs() < 1e-6);
        assert!((g - 128.0 / 255.0 * alpha).abs() < 1e-6);
        assert_eq!(b, 0.0);
    }

    fn overlaps(a: ((u32, u32), (u32, u32)), b: ((u32, u32), (u32, u32))) -> bool {
        let ((ax, ay), (aw, ah)) = a;
        let ((bx, by), (bw, bh)) = b;
        ax < bx + bw && bx < ax + aw && ay < by + bh && by < ay + ah
    }

    #[test]
    fn packing_fills_shelves_without_overlap() {
        let sizes = [(60, 10), (50, 20), (40, 5), (100, 8), (1, 1), (30, 30)];
        let placed = pack_prefix(&sizes, 100, 100);
        assert_eq!(
            placed,
            [(0, 0), (0, 10), (50, 10), (0, 30), (0, 38), (1, 38)]
        );
        for (i, (&at, &size)) in placed.iter().zip(&sizes).enumerate() {
            assert!(at.0 + size.0 <= 100 && at.1 + size.1 <= 100);
            for (&other_at, &other_size) in placed.iter().zip(&sizes).skip(i + 1) {
                assert!(!overlaps((at, size), (other_at, other_size)));
            }
        }
    }

    #[test]
    fn packing_stops_at_the_first_rectangle_that_does_not_fit() {
        // The third needs a new shelf that would end below the atlas; the fourth would fit but
        // must not jump the queue (drawing order).
        let sizes = [(8, 6), (8, 6), (8, 6), (2, 2)];
        assert_eq!(pack_prefix(&sizes, 16, 10), [(0, 0), (8, 0)]);
        assert!(pack_prefix(&[(17, 1)], 16, 10).is_empty());
        assert!(pack_prefix(&[(0, 4)], 16, 10).is_empty());
        assert!(pack_prefix(&[], 16, 10).is_empty());
    }

    #[test]
    fn atlas_grows_only_as_far_as_needed() {
        assert_eq!(atlas_size_for(&[(100, 40); 8], ATLAS_START), ATLAS_START);
        // A 4K-wide line does not fit 2048 across: only the width grows.
        assert_eq!(atlas_size_for(&[(3000, 200)], ATLAS_START), (4096, 1024));
        // Three full 1080p clips need 2048x4096.
        assert_eq!(
            atlas_size_for(&[(1920, 1080); 3], ATLAS_START),
            (2048, 4096)
        );
        // More than the cap holds: stays at the cap, the rest goes to another batch.
        let many = [(4096, 1024); 6];
        assert_eq!(atlas_size_for(&many, ATLAS_START), (ATLAS_MAX, ATLAS_MAX));
        assert_eq!(pack_prefix(&many, ATLAS_MAX, ATLAS_MAX).len(), 4);
    }
}

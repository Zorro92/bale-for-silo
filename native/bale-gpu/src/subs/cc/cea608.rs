//! CEA-608 (line 21) caption decoder for one channel, CC1 to CC4: the two 15×32 caption memories,
//! the cursor and the pen, driven by the channel's byte pairs in display order.
//!
//! The behaviour follows the decoder rules of the former 47 CFR 15.119 (public domain; CEA-608-E
//! carries the same rules): pop-on (Resume Caption Loading into non-displayed memory, End of
//! Caption flips the memories), roll-up (2, 3 or 4 rows on a base row, Carriage Return scrolls, a
//! Preamble Address Code moves the window), paint-on (Resume Direct Captioning writes into displayed
//! memory), Preamble Address Codes (row, indent, colour, italics, underline), mid-row codes, Tab
//! Offsets, Backspace, Delete to End of Row, the erase commands, Flash On, and the special and
//! extended character sets (an extended character replaces the standard one sent before it).
//!
//! Ported from androidx Media3's `Cea608Decoder.java` (Copyright (C) 2016 The Android Open Source
//! Project, Apache License 2.0; changed for Bale: see `native/bale-gpu/NOTICE`): the character
//! tables, odd-parity and `cc_valid` checks (a caption channel that turns invalid drops what is
//! being built), suppression of the repeated transmission of a control code, the caption, Text and
//! XDS service tracking, the data channel bit, and a mid-row code's space taking the old style.
//! Where Media3 simplifies (its cue builders), this keeps the 608 memories.
//!
//! Background attribute codes (10/18 20-2F, 17/1F 2D) and the black foreground codes (17/1F 2E-2F)
//! set the pen without taking a column, as Media3, FFmpeg and AetherEngine treat them; CEA-608-E
//! has them replace a space the caption provider sends first, which ends in the same place.

use super::extract::Triplet;

pub const ROWS: usize = 15;
pub const COLUMNS: usize = 32;

/// A caption colour. Foregrounds use the first seven (and black from 17 2E); backgrounds all eight.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum Color {
    #[default]
    White,
    Green,
    Blue,
    Cyan,
    Red,
    Yellow,
    Magenta,
    Black,
}

impl Color {
    /// The three-bit colour of a PAC, mid-row or background attribute code.
    fn from_index(index: u8) -> Color {
        match index & 7 {
            0 => Color::White,
            1 => Color::Green,
            2 => Color::Blue,
            3 => Color::Cyan,
            4 => Color::Red,
            5 => Color::Yellow,
            6 => Color::Magenta,
            _ => Color::Black,
        }
    }

    /// Red, green, blue (Media3's `STYLE_COLORS`, Android's `Color` constants).
    pub fn rgb(self) -> (u8, u8, u8) {
        match self {
            Color::White => (255, 255, 255),
            Color::Green => (0, 255, 0),
            Color::Blue => (0, 0, 255),
            Color::Cyan => (0, 255, 255),
            Color::Red => (255, 0, 0),
            Color::Yellow => (255, 255, 0),
            Color::Magenta => (255, 0, 255),
            Color::Black => (0, 0, 0),
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum Opacity {
    #[default]
    Opaque,
    SemiTransparent,
    Transparent,
}

/// A cell's background. 608's default is opaque black.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct Background {
    pub color: Color,
    pub opacity: Opacity,
}

impl Default for Background {
    fn default() -> Self {
        Background {
            color: Color::Black,
            opacity: Opacity::Opaque,
        }
    }
}

/// The attributes characters are written with.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct Pen {
    pub foreground: Color,
    pub italic: bool,
    pub underline: bool,
    pub flash: bool,
    pub background: Background,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Cell {
    pub ch: char,
    pub pen: Pen,
}

pub type Row = [Option<Cell>; COLUMNS];

/// One caption memory; row 1 is index 0. An empty cell is transparent.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Memory(pub [Row; ROWS]);

impl Memory {
    pub const EMPTY: Memory = Memory([[None; COLUMNS]; ROWS]);

    pub fn is_empty(&self) -> bool {
        self.0.iter().all(|row| row.iter().all(Option::is_none))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Mode {
    /// No caption command yet: data goes to non-displayed memory, as in pop-on.
    Unknown,
    PopOn,
    RollUp,
    PaintOn,
}

/// One of the four caption channels.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Channel(u8);

impl Channel {
    pub const CC1: Channel = Channel(1);
    pub const CC2: Channel = Channel(2);
    pub const CC3: Channel = Channel(3);
    pub const CC4: Channel = Channel(4);

    /// CC1..CC4 from 1..=4.
    pub fn new(number: u32) -> Option<Channel> {
        u8::try_from(number)
            .ok()
            .filter(|n| (1..=4).contains(n))
            .map(Channel)
    }

    pub fn number(self) -> u32 {
        u32::from(self.0)
    }

    /// The `cc_type` of its pairs: 0 = field 1 (CC1, CC2), 1 = field 2 (CC3, CC4).
    pub fn field(self) -> u8 {
        (self.0 - 1) / 2
    }

    /// The data channel bit of its control codes: 0 = CC1 and CC3, 1 = CC2 and CC4.
    pub fn data_channel(self) -> u8 {
        (self.0 - 1) % 2
    }
}

/// Media3 `BASIC_CHARACTER_SET`: ASCII with the 608 replacements.
fn basic_char(b: u8) -> char {
    match b {
        0x2A => 'á',
        0x5C => 'é',
        0x5E => 'í',
        0x5F => 'ó',
        0x60 => 'ú',
        0x7B => 'ç',
        0x7C => '÷',
        0x7D => 'Ñ',
        0x7E => 'ñ',
        0x7F => '\u{25A0}',
        b => char::from(b),
    }
}

/// The transparent space (11 39): a column of video, an empty cell.
const TRANSPARENT_SPACE: char = '\0';

/// Media3 `SPECIAL_CHARACTER_SET` (11/19 30-3F).
const SPECIAL: [char; 16] = [
    '®',
    '°',
    '½',
    '¿',
    '™',
    '¢',
    '£',
    '♪',
    'à',
    TRANSPARENT_SPACE,
    'è',
    'â',
    'ê',
    'î',
    'ô',
    'û',
];

/// Media3 `SPECIAL_ES_FR_CHARACTER_SET` (12/1A 20-3F): Spanish, miscellaneous and French.
const EXTENDED_ES_FR: [char; 32] = [
    'Á', 'É', 'Ó', 'Ú', 'Ü', 'ü', '\u{2018}', '¡', '*', '\'', '\u{2014}', '©', '\u{2120}',
    '\u{2022}', '\u{201C}', '\u{201D}', 'À', 'Â', 'Ç', 'È', 'Ê', 'Ë', 'ë', 'Î', 'Ï', 'ï', 'Ô', 'Ù',
    'ù', 'Û', '«', '»',
];

/// Media3 `SPECIAL_PT_DE_CHARACTER_SET` (13/1B 20-3F): Portuguese, German and Danish.
const EXTENDED_PT_DE: [char; 32] = [
    'Ã', 'ã', 'Í', 'Ì', 'ì', 'Ò', 'ò', 'Õ', 'õ', '{', '}', '\\', '^', '_', '|', '~', 'Ä', 'ä', 'Ö',
    'ö', 'ß', '¥', '¤', '\u{2502}', 'Å', 'å', 'Ø', 'ø', '\u{250C}', '\u{2510}', '\u{2514}',
    '\u{2518}',
];

/// PAC row (1-based) by the first byte's low three bits and the second byte's 0x20 bit; 0 is not
/// assigned (10 60-7F).
const PAC_ROWS: [[u8; 2]; 8] = [
    [11, 0],
    [1, 2],
    [3, 4],
    [12, 13],
    [14, 15],
    [5, 6],
    [7, 8],
    [9, 10],
];

pub(crate) fn odd_parity(b: u8) -> bool {
    b.count_ones() % 2 == 1
}

/// The decoder of one channel.
#[derive(Clone, Debug)]
pub struct Decoder {
    channel: Channel,
    /// The channel's last pair passed the `cc_valid` and parity checks.
    caption_valid: bool,
    /// The control code a redundant copy of which is expected next.
    repeat: Option<[u8; 2]>,
    /// The data channel the field's data belongs to (set by each control code).
    data_channel: u8,
    /// False while the channel carries Text (Text Restart, Resume Text Display).
    caption_service: bool,
    /// An XDS packet is running in field 2.
    xds: bool,
    mode: Mode,
    rollup_rows: usize,
    /// Roll-up base row, 0-based.
    base_row: usize,
    displayed: Memory,
    hidden: Memory,
    /// Cursor row, 0-based.
    row: usize,
    /// Cursor column, 0..=COLUMNS; COLUMNS (past the last) writes into the last column.
    col: usize,
    pen: Pen,
    /// Displayed memory may have changed since [`Decoder::take_touched`].
    touched: bool,
}

impl Decoder {
    pub fn new(channel: Channel) -> Self {
        Decoder {
            channel,
            caption_valid: false,
            repeat: None,
            data_channel: 0,
            caption_service: true,
            xds: false,
            mode: Mode::Unknown,
            rollup_rows: 4,
            base_row: ROWS - 1,
            displayed: Memory::EMPTY,
            hidden: Memory::EMPTY,
            row: ROWS - 1,
            col: 0,
            pen: Pen::default(),
            touched: false,
        }
    }

    pub fn channel(&self) -> Channel {
        self.channel
    }

    pub fn mode(&self) -> Mode {
        self.mode
    }

    pub fn displayed(&self) -> &Memory {
        &self.displayed
    }

    /// Whether displayed memory may have changed since the last call.
    pub fn take_touched(&mut self) -> bool {
        std::mem::take(&mut self.touched)
    }

    /// ANSI/CTA-608-E Annex C.9: erases a roll-up or paint-on caption that has had no data for the
    /// timeout. True when something was erased.
    pub fn expire(&mut self) -> bool {
        if matches!(self.mode, Mode::RollUp | Mode::PaintOn) && !self.displayed.is_empty() {
            self.displayed = Memory::EMPTY;
            self.touched = true;
            return true;
        }
        false
    }

    /// Back to the state of a new decoder (a seek, a flush, another stream). Displayed memory
    /// counts as touched when it held something.
    pub fn reset(&mut self) {
        let touched = !self.displayed.is_empty();
        *self = Decoder::new(self.channel);
        self.touched = touched;
    }

    /// Feeds one `cc_data` triplet; anything but this channel's field is ignored. True when the pair
    /// was caption data of this channel.
    pub fn feed(&mut self, triplet: Triplet) -> bool {
        let cc_type = triplet.cc_type();
        if cc_type & 0x02 != 0 || cc_type != self.channel.field() {
            return false;
        }
        let [raw1, raw2] = triplet.data();
        let (b1, b2) = (raw1 & 0x7F, raw2 & 0x7F);
        if b1 == 0 && b2 == 0 {
            return false;
        }
        let was_valid = self.caption_valid;
        self.caption_valid = triplet.valid() && odd_parity(raw1) && odd_parity(raw2);
        if self.repeated(b1, b2) {
            return false;
        }
        if !self.caption_valid {
            if was_valid {
                // The encoder flipped cc_valid off: captions are being turned off (Media3).
                self.lost();
            }
            return false;
        }
        if !self.route(b1, b2) {
            return false;
        }
        if (0x10..0x20).contains(&b1) {
            self.control(b1, b2);
        } else {
            self.chars(b1, b2);
        }
        true
    }

    /// Media3 `isRepeatedCommand`: a control code (first byte 10-1F) is sent twice in a row; the
    /// copy is dropped, a third one is a new command.
    fn repeated(&mut self, b1: u8, b2: u8) -> bool {
        if self.caption_valid && (0x10..=0x1F).contains(&b1) {
            if self.repeat == Some([b1, b2]) {
                self.repeat = None;
                return true;
            }
            self.repeat = Some([b1, b2]);
        } else {
            self.repeat = None;
        }
        false
    }

    /// Whether the pair belongs to this channel's caption service.
    fn route(&mut self, b1: u8, b2: u8) -> bool {
        if self.channel.field() == 1 {
            // XDS (field 2 only): 01-0E start or continue a packet, 0F ends it; a caption control
            // code interrupts it.
            if (0x01..=0x0F).contains(&b1) {
                self.xds = b1 != 0x0F;
                return false;
            }
            if self.xds {
                if !(0x10..=0x1F).contains(&b1) {
                    return false;
                }
                self.xds = false;
            }
        }
        if (0x10..=0x1F).contains(&b1) {
            self.data_channel = (b1 >> 3) & 1;
        }
        if self.data_channel != self.channel.data_channel() {
            return false;
        }
        if (b1 & 0xF6) == 0x14 {
            match b2 {
                0x2A | 0x2B => self.caption_service = false,
                0x20 | 0x25..=0x27 | 0x29 | 0x2F => self.caption_service = true,
                _ => {}
            }
        }
        self.caption_service
    }

    fn control(&mut self, b1: u8, b2: u8) {
        let code = b1 & 0xF7;
        match (code, b2) {
            (0x10..=0x17, 0x40..=0x7F) => self.preamble(code, b2),
            (0x11, 0x20..=0x2F) => self.mid_row(b2),
            (0x11, 0x30..=0x3F) => match SPECIAL[usize::from(b2 - 0x30)] {
                TRANSPARENT_SPACE => self.put(None),
                ch => self.write(ch),
            },
            (0x12 | 0x13, 0x20..=0x3F) => {
                let table = if code == 0x12 {
                    &EXTENDED_ES_FR
                } else {
                    &EXTENDED_PT_DE
                };
                self.backspace();
                self.write(table[usize::from(b2 - 0x20)]);
            }
            (0x10, 0x20..=0x2F) => {
                self.pen.background = Background {
                    color: Color::from_index(b2 >> 1),
                    opacity: if b2 & 1 != 0 {
                        Opacity::SemiTransparent
                    } else {
                        Opacity::Opaque
                    },
                };
            }
            (0x17, 0x21..=0x23) => self.col = (self.col + usize::from(b2 - 0x20)).min(COLUMNS - 1),
            (0x17, 0x2D) => self.pen.background.opacity = Opacity::Transparent,
            (0x17, 0x2E | 0x2F) => {
                self.pen.foreground = Color::Black;
                self.pen.italic = false;
                self.pen.flash = false;
                self.pen.underline = b2 & 1 != 0;
            }
            (0x14 | 0x15, 0x20..=0x2F) => self.misc(b2),
            _ => {}
        }
    }

    /// Preamble Address Code: row, then colour and italics or an indent (white), and underline.
    fn preamble(&mut self, code: u8, b2: u8) {
        let row = PAC_ROWS[usize::from(code & 7)][usize::from(b2 & 0x20 != 0)];
        if row == 0 {
            return;
        }
        let attributes = b2 & 0x1F;
        let underline = attributes & 1 != 0;
        let style = (attributes >> 1) & 7;
        let indent = attributes & 0x10 != 0;
        self.pen = Pen {
            foreground: if indent || style == 7 {
                Color::White
            } else {
                Color::from_index(style)
            },
            italic: !indent && style == 7,
            underline,
            ..Pen::default()
        };
        let row = usize::from(row) - 1;
        if self.mode == Mode::RollUp {
            self.move_window(row);
            self.row = self.base_row;
        } else {
            self.row = row;
        }
        self.col = if indent { usize::from(style) * 4 } else { 0 };
    }

    /// Mid-row code: a space in the old style, then colour (italics off) or italics, underline, and
    /// flash off.
    fn mid_row(&mut self, b2: u8) {
        self.write(' ');
        let style = (b2 >> 1) & 7;
        if style == 7 {
            self.pen.italic = true;
        } else {
            self.pen.foreground = Color::from_index(style);
            self.pen.italic = false;
        }
        self.pen.underline = b2 & 1 != 0;
        self.pen.flash = false;
    }

    fn misc(&mut self, b2: u8) {
        match b2 {
            // Resume Caption Loading: a roll-up display stays.
            0x20 => self.mode = Mode::PopOn,
            0x21 => self.backspace(),
            0x24 => {
                let (row, col) = (self.row, self.col);
                if col < COLUMNS {
                    self.memory()[row][col..].fill(None);
                }
            }
            0x25..=0x27 => self.roll_up(usize::from(b2 - 0x23)),
            // Flash On: a spacing attribute.
            0x28 => {
                self.write(' ');
                self.pen.flash = true;
            }
            // Resume Direct Captioning: what is displayed stays and is painted over.
            0x29 => self.mode = Mode::PaintOn,
            0x2C => self.erase_displayed(),
            0x2D => self.carriage_return(),
            0x2E => self.hidden = Memory::EMPTY,
            0x2F => {
                std::mem::swap(&mut self.displayed, &mut self.hidden);
                self.mode = Mode::PopOn;
                self.touched = true;
            }
            // 22/23 reserved; 2A/2B (Text) are handled by the service tracking.
            _ => {}
        }
    }

    /// RU2/RU3/RU4. From another style both memories are erased and the base row is row 15;
    /// in roll-up only the window size changes, rows above it are erased.
    fn roll_up(&mut self, rows: usize) {
        if self.mode != Mode::RollUp {
            self.erase_displayed();
            self.hidden = Memory::EMPTY;
            self.mode = Mode::RollUp;
            self.base_row = ROWS - 1;
            self.row = self.base_row;
            self.col = 0;
            self.pen = Pen::default();
        }
        self.rollup_rows = rows;
        self.move_window(self.base_row);
        self.row = self.base_row;
    }

    /// Moves the roll-up window to base row `row` (0-based) intact, as low as it has to be to fit,
    /// and erases every row outside it.
    fn move_window(&mut self, row: usize) {
        let rows = self.rollup_rows;
        let base = row.max(rows - 1);
        let mut moved = Memory::EMPTY;
        for i in 0..rows {
            if let (Some(from), Some(to)) = (
                (self.base_row + 1 + i).checked_sub(rows),
                (base + 1 + i).checked_sub(rows),
            ) {
                moved.0[to] = self.displayed.0[from];
            }
        }
        self.base_row = base;
        if moved != self.displayed {
            self.displayed = moved;
            self.touched = true;
        }
    }

    /// Roll-up: the top row of the window goes, the others move up, the base row is empty and the
    /// cursor at its start. Other styles ignore it.
    fn carriage_return(&mut self) {
        if self.mode != Mode::RollUp {
            return;
        }
        let base = self.base_row;
        let top = base + 1 - self.rollup_rows;
        for row in top..base {
            self.displayed.0[row] = self.displayed.0[row + 1];
        }
        self.displayed.0[base] = [None; COLUMNS];
        self.row = base;
        self.col = 0;
        self.pen = Pen::default();
        self.touched = true;
    }

    fn erase_displayed(&mut self) {
        if !self.displayed.is_empty() {
            self.displayed = Memory::EMPTY;
            self.touched = true;
        }
    }

    /// Media3 `resetCueBuilders` on a channel turning invalid: what is being built goes; roll-up and
    /// paint-on build on screen.
    fn lost(&mut self) {
        self.hidden = Memory::EMPTY;
        if matches!(self.mode, Mode::RollUp | Mode::PaintOn) {
            self.erase_displayed();
        }
    }

    fn chars(&mut self, b1: u8, b2: u8) {
        // A first byte 00-0F (field 1) is ignored alone; the second is still a character.
        if b1 >= 0x20 {
            self.write(basic_char(b1));
        }
        if b2 >= 0x20 {
            self.write(basic_char(b2));
        }
    }

    /// The memory characters go to: displayed in roll-up and paint-on, else non-displayed.
    fn memory(&mut self) -> &mut [Row; ROWS] {
        if matches!(self.mode, Mode::RollUp | Mode::PaintOn) {
            self.touched = true;
            &mut self.displayed.0
        } else {
            &mut self.hidden.0
        }
    }

    fn write(&mut self, ch: char) {
        let cell = Cell { ch, pen: self.pen };
        self.put(Some(cell));
    }

    /// Writes `cell` at the cursor and moves it right; past the last column it stays there.
    fn put(&mut self, cell: Option<Cell>) {
        let (row, col) = (self.row, self.col.min(COLUMNS - 1));
        self.memory()[row][col] = cell;
        self.col = col + 1;
    }

    /// One column left, erasing it; ignored in the first column.
    fn backspace(&mut self) {
        if self.col == 0 {
            return;
        }
        self.col -= 1;
        let (row, col) = (self.row, self.col);
        self.memory()[row][col] = None;
    }

    #[cfg(test)]
    pub(crate) fn hidden(&self) -> &Memory {
        &self.hidden
    }

    #[cfg(test)]
    pub(crate) fn cursor(&self) -> (usize, usize) {
        (self.row + 1, self.col)
    }
}

#[cfg(test)]
#[path = "cea608_tests.rs"]
mod tests;

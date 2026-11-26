use super::*;

/// `b` with its odd-parity bit.
fn parity(b: u8) -> u8 {
    let b = b & 0x7F;
    if b.count_ones().is_multiple_of(2) {
        b | 0x80
    } else {
        b
    }
}

/// A valid pair of `field` (0 or 1).
fn pair(field: u8, b1: u8, b2: u8) -> Triplet {
    Triplet([0xFC | field, parity(b1), parity(b2)])
}

const RCL: [u8; 2] = [0x14, 0x20];
const BS: [u8; 2] = [0x14, 0x21];
const DER: [u8; 2] = [0x14, 0x24];
const RU2: [u8; 2] = [0x14, 0x25];
const RU3: [u8; 2] = [0x14, 0x26];
const RU4: [u8; 2] = [0x14, 0x27];
const FON: [u8; 2] = [0x14, 0x28];
const RDC: [u8; 2] = [0x14, 0x29];
const TR: [u8; 2] = [0x14, 0x2A];
const EDM: [u8; 2] = [0x14, 0x2C];
const CR: [u8; 2] = [0x14, 0x2D];
const ENM: [u8; 2] = [0x14, 0x2E];
const EOC: [u8; 2] = [0x14, 0x2F];
const ROW15: [u8; 2] = [0x14, 0x60];
const ROW14: [u8; 2] = [0x14, 0x40];
const ROW1: [u8; 2] = [0x11, 0x40];

/// Feeds field-1 data: each `[a, b]` once; `text` as character pairs. Control codes are sent
/// twice, as encoders do.
struct Feed<'a>(&'a mut Decoder);

impl Feed<'_> {
    fn code(&mut self, code: [u8; 2]) -> &mut Self {
        let field = self.0.channel.field();
        self.0.feed(pair(field, code[0], code[1]));
        self.0.feed(pair(field, code[0], code[1]));
        self
    }

    fn once(&mut self, code: [u8; 2]) -> &mut Self {
        let field = self.0.channel.field();
        self.0.feed(pair(field, code[0], code[1]));
        self
    }

    fn text(&mut self, text: &str) -> &mut Self {
        let field = self.0.channel.field();
        let bytes: Vec<u8> = text.bytes().collect();
        for chunk in bytes.chunks(2) {
            self.0
                .feed(pair(field, chunk[0], chunk.get(1).copied().unwrap_or(0)));
        }
        self
    }
}

fn feed(decoder: &mut Decoder) -> Feed<'_> {
    Feed(decoder)
}

/// Row `row` (1-based) of `memory` as text; empty cells are `.`.
fn row_text(memory: &Memory, row: usize) -> String {
    memory.0[row - 1]
        .iter()
        .map(|cell| cell.map_or('.', |cell| cell.ch))
        .collect::<String>()
        .trim_end_matches('.')
        .to_owned()
}

/// Every non-empty row of `memory`, with its 1-based number.
fn rows(memory: &Memory) -> Vec<(usize, String)> {
    (1..=ROWS)
        .map(|row| (row, row_text(memory, row)))
        .filter(|(_, text)| !text.is_empty())
        .collect()
}

fn cell(memory: &Memory, row: usize, col: usize) -> Cell {
    memory.0[row - 1][col].expect("a character")
}

#[test]
fn channels_map_to_fields_and_data_channels() {
    assert_eq!(Channel::new(0), None);
    assert_eq!(Channel::new(5), None);
    let mapped: Vec<(u8, u8)> = (1..=4)
        .map(|n| {
            let channel = Channel::new(n).unwrap();
            assert_eq!(channel.number(), n);
            (channel.field(), channel.data_channel())
        })
        .collect();
    assert_eq!(mapped, [(0, 0), (0, 1), (1, 0), (1, 1)]);
}

#[test]
fn pop_on_loads_hidden_memory_and_flips_on_end_of_caption() {
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RCL)
        .code(ENM)
        .code(ROW15)
        .text("HELLO");
    assert!(decoder.displayed().is_empty());
    assert!(!decoder.take_touched());
    assert_eq!(rows(decoder.hidden()), [(15, "HELLO".to_owned())]);
    feed(&mut decoder).code([0x14, 0x2F]);
    assert!(decoder.take_touched());
    assert_eq!(rows(decoder.displayed()), [(15, "HELLO".to_owned())]);
    assert!(decoder.hidden().is_empty());
    assert_eq!(decoder.mode(), Mode::PopOn);
    // The next caption builds while the first stays up; EOC flips again without erasing.
    feed(&mut decoder).code(RCL).code(ROW1).text("NEXT");
    assert_eq!(rows(decoder.displayed()), [(15, "HELLO".to_owned())]);
    feed(&mut decoder).code(EOC);
    assert_eq!(rows(decoder.displayed()), [(1, "NEXT".to_owned())]);
    assert_eq!(rows(decoder.hidden()), [(15, "HELLO".to_owned())]);
    // Erase Non-displayed Memory and Erase Displayed Memory.
    feed(&mut decoder).code(ENM);
    assert!(decoder.hidden().is_empty());
    decoder.take_touched();
    feed(&mut decoder).code(EDM);
    assert!(decoder.take_touched());
    assert!(decoder.displayed().is_empty());
    // EOC without RCL forces pop-on.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(ROW15).text("X").code(EOC);
    assert_eq!(rows(decoder.displayed()), [(15, "X".to_owned())]);
}

#[test]
fn a_repeated_control_code_acts_once_and_a_third_copy_acts_again() {
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RDC).code(ROW15).text("ABCDE");
    // BS BS (one backspace), BS BS (another).
    feed(&mut decoder).code(BS).code(BS);
    assert_eq!(row_text(decoder.displayed(), 15), "ABC");
    // Three copies: the first acts, the second is its repeat, the third acts again.
    feed(&mut decoder).once(BS).once(BS).once(BS);
    assert_eq!(row_text(decoder.displayed(), 15), "A");
    // Null padding between the two copies does not break the pair.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RDC).code(ROW15).text("ABC");
    decoder.feed(pair(0, BS[0], BS[1]));
    decoder.feed(Triplet([0xFC, 0x80, 0x80]));
    decoder.feed(pair(0, BS[0], BS[1]));
    assert_eq!(row_text(decoder.displayed(), 15), "AB");
}

#[test]
fn roll_up_scrolls_two_three_and_four_rows_on_the_base_row() {
    for (command, rows_shown) in [(RU2, 2), (RU3, 3), (RU4, 4)] {
        let mut decoder = Decoder::new(Channel::CC1);
        feed(&mut decoder).code(command).code(CR).code(ROW15);
        for line in 1..=5 {
            if line > 1 {
                feed(&mut decoder).code(CR).code(ROW15);
            }
            feed(&mut decoder).text(&format!("LINE {line}"));
            // Characters show as they arrive.
            assert!(decoder.take_touched());
        }
        let expected: Vec<(usize, String)> = (0..rows_shown)
            .map(|i| (15 - i, format!("LINE {}", 5 - i)))
            .rev()
            .collect();
        assert_eq!(rows(decoder.displayed()), expected, "{rows_shown} rows");
        assert_eq!(decoder.cursor(), (15, 6));
    }
}

#[test]
fn a_roll_up_window_moves_intact_and_resizes() {
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RU3).code(ROW15).text("ONE");
    feed(&mut decoder).code(CR).text("TWO");
    feed(&mut decoder).code(CR).text("THREE");
    assert_eq!(
        rows(decoder.displayed()),
        [
            (13, "ONE".to_owned()),
            (14, "TWO".to_owned()),
            (15, "THREE".to_owned())
        ]
    );
    // A PAC on row 5 moves the window up without erasing; the cursor goes to the base row.
    feed(&mut decoder).code([0x15, 0x40]);
    assert_eq!(
        rows(decoder.displayed()),
        [
            (3, "ONE".to_owned()),
            (4, "TWO".to_owned()),
            (5, "THREE".to_owned())
        ]
    );
    assert_eq!(decoder.cursor(), (5, 0));
    // A base row above the window's height: the window stays as low as it must.
    feed(&mut decoder).code(ROW1);
    assert_eq!(rows(decoder.displayed())[0].0, 1);
    assert_eq!(decoder.cursor(), (3, 0));
    // RU2 turns the top row off and erases it.
    feed(&mut decoder).code(RU2);
    assert_eq!(
        rows(decoder.displayed()),
        [(2, "TWO".to_owned()), (3, "THREE".to_owned())]
    );
    // A carriage return with nothing to scroll leaves the base row empty and the cursor at its start.
    feed(&mut decoder).code(EDM).code(CR);
    assert!(decoder.displayed().is_empty());
    assert_eq!(decoder.cursor(), (3, 0));
}

#[test]
fn styles_switch_as_the_rules_say() {
    // A roll-up command erases a pop-on caption from both memories.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(ROW15).text("SHOWN").code(EOC);
    feed(&mut decoder).code(RCL).code(ROW14).text("HIDDEN");
    feed(&mut decoder).code(RU2);
    assert!(decoder.displayed().is_empty());
    assert!(decoder.hidden().is_empty());
    assert_eq!(decoder.cursor(), (15, 0));
    // RCL and RDC leave a roll-up caption displayed.
    feed(&mut decoder).text("ROLL");
    feed(&mut decoder).code(RCL).code(ROW1).text("LOAD");
    assert_eq!(rows(decoder.displayed()), [(15, "ROLL".to_owned())]);
    feed(&mut decoder).code(RDC).code(ROW1).text("PAINT");
    assert_eq!(
        rows(decoder.displayed()),
        [(1, "PAINT".to_owned()), (15, "ROLL".to_owned())]
    );
    // Paint-on writes straight to the display; EOC keeps a paint-on caption in non-displayed memory.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RDC)
        .code([0x16, 0x72])
        .text("PAINT");
    assert!(decoder.take_touched());
    assert_eq!(rows(decoder.displayed()), [(8, "....PAINT".to_owned())]);
    feed(&mut decoder).code(EOC);
    assert!(decoder.displayed().is_empty());
    assert_eq!(rows(decoder.hidden()), [(8, "....PAINT".to_owned())]);
    // Carriage returns do nothing in pop-on and paint-on.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RCL)
        .code(ROW14)
        .text("AB")
        .code(CR)
        .text("CD");
    assert_eq!(rows(decoder.hidden()), [(14, "ABCD".to_owned())]);
}

#[test]
fn preamble_address_codes_place_rows_indents_and_attributes() {
    // Every row: the PAC table of 15.119 (i).
    let rows_by_code: [(u8, u8, usize); 15] = [
        (0x11, 0x40, 1),
        (0x11, 0x60, 2),
        (0x12, 0x40, 3),
        (0x12, 0x60, 4),
        (0x15, 0x40, 5),
        (0x15, 0x60, 6),
        (0x16, 0x40, 7),
        (0x16, 0x60, 8),
        (0x17, 0x40, 9),
        (0x17, 0x60, 10),
        (0x10, 0x40, 11),
        (0x13, 0x40, 12),
        (0x13, 0x60, 13),
        (0x14, 0x40, 14),
        (0x14, 0x60, 15),
    ];
    for (b1, b2, row) in rows_by_code {
        let mut decoder = Decoder::new(Channel::CC1);
        feed(&mut decoder).code(RDC).code([b1, b2]).text("X");
        assert_eq!(rows(decoder.displayed()), [(row, "X".to_owned())]);
        // Data channel 2's codes (first byte + 8) address the same row for CC2.
        let mut decoder = Decoder::new(Channel::CC2);
        feed(&mut decoder)
            .code([0x1C, 0x29])
            .code([b1 + 8, b2])
            .text("Y");
        assert_eq!(rows(decoder.displayed()), [(row, "Y".to_owned())]);
    }
    // 10 60-7F is not assigned: the cursor stays.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RDC)
        .code(ROW1)
        .code([0x10, 0x60])
        .text("Z");
    assert_eq!(rows(decoder.displayed()), [(1, "Z".to_owned())]);
    // Indents 0..28, white and with the underline bit.
    for indent in 0..8u8 {
        for underline in [false, true] {
            let mut decoder = Decoder::new(Channel::CC1);
            feed(&mut decoder)
                .code(RDC)
                .code([0x14, 0x50 | (indent << 1) | u8::from(underline)])
                .text("I");
            let at = cell(decoder.displayed(), 14, usize::from(indent) * 4);
            assert_eq!(at.ch, 'I');
            assert_eq!(
                at.pen,
                Pen {
                    underline,
                    ..Pen::default()
                }
            );
        }
    }
    // Colours, italics and underline from the PAC; a PAC resets what a mid-row code set.
    let colours = [
        Color::White,
        Color::Green,
        Color::Blue,
        Color::Cyan,
        Color::Red,
        Color::Yellow,
        Color::Magenta,
    ];
    for (style, colour) in colours.iter().enumerate() {
        let mut decoder = Decoder::new(Channel::CC1);
        feed(&mut decoder)
            .code(RDC)
            .code([0x11, 0x2E]) // italics, to be reset
            .code([0x14, 0x61 | ((style as u8) << 1)])
            .text("C");
        let pen = cell(decoder.displayed(), 15, 0).pen;
        assert_eq!(pen.foreground, *colour);
        assert!(pen.underline && !pen.italic);
    }
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RDC).code([0x14, 0x6E]).text("W");
    let pen = cell(decoder.displayed(), 15, 0).pen;
    assert!(pen.italic && !pen.underline);
    assert_eq!(pen.foreground, Color::White);
}

#[test]
fn mid_row_codes_change_colour_italics_and_underline_after_a_space() {
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RDC)
        .code([0x14, 0x62]) // green
        .text("G")
        .code([0x11, 0x2E]) // italics: colour stays
        .text("I")
        .code([0x11, 0x29]) // red underline: italics off
        .text("R")
        .code([0x11, 0x2F]) // italics underline
        .text("U");
    let memory = *decoder.displayed();
    assert_eq!(row_text(&memory, 15), "G I R U");
    let pen = |col| cell(&memory, 15, col).pen;
    assert_eq!((pen(0).foreground, pen(0).italic), (Color::Green, false));
    // The space a mid-row code takes keeps the style before it (Media3).
    assert_eq!(pen(1), pen(0));
    assert_eq!((pen(2).foreground, pen(2).italic), (Color::Green, true));
    assert_eq!(
        (pen(4).foreground, pen(4).italic, pen(4).underline),
        (Color::Red, false, true)
    );
    assert_eq!(
        (pen(6).foreground, pen(6).italic, pen(6).underline),
        (Color::Red, true, true)
    );
    // Flash On is a spacing attribute too, and a mid-row code turns it off.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RDC)
        .code(ROW15)
        .code(FON)
        .text("F")
        .code([0x11, 0x20])
        .text("N");
    let memory = *decoder.displayed();
    assert_eq!(row_text(&memory, 15), " F N");
    assert!(cell(&memory, 15, 1).pen.flash);
    assert!(!cell(&memory, 15, 3).pen.flash);
}

#[test]
fn tab_offsets_move_without_erasing() {
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RDC)
        .code(ROW15)
        .text("ABCDEFGH")
        .code(ROW15)
        .code([0x17, 0x21])
        .text("x")
        .code([0x17, 0x22])
        .text("y")
        .code([0x17, 0x23]);
    assert_eq!(row_text(decoder.displayed(), 15), "AxCDyFGH");
    assert_eq!(decoder.cursor(), (15, 8));
    // A PAC indent plus a tab on an empty row, as the clips use: column 10.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RCL)
        .code([0x13, 0x54])
        .code([0x17, 0x22])
        .text("T");
    assert_eq!(rows(decoder.hidden()), [(12, "..........T".to_owned())]);
    // Tabs stop at the last column.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RCL).code([0x14, 0x5E]);
    for _ in 0..3 {
        feed(&mut decoder).code([0x17, 0x23]);
    }
    feed(&mut decoder).text("E");
    assert_eq!(cell(decoder.hidden(), 14, 31).ch, 'E');
}

#[test]
fn background_attributes_and_black_text_take_no_column() {
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RDC)
        .code(ROW15)
        .code([0x10, 0x24]) // blue opaque
        .text("B")
        .code([0x10, 0x29]) // red semi-transparent
        .text("R")
        .code([0x17, 0x2D]) // transparent
        .text("T")
        .code([0x10, 0x20]) // white opaque
        .code([0x17, 0x2F]) // black underline
        .text("K");
    let memory = *decoder.displayed();
    assert_eq!(row_text(&memory, 15), "BRTK");
    let pen = |col| cell(&memory, 15, col).pen;
    assert_eq!(
        pen(0).background,
        Background {
            color: Color::Blue,
            opacity: Opacity::Opaque
        }
    );
    assert_eq!(
        pen(1).background,
        Background {
            color: Color::Red,
            opacity: Opacity::SemiTransparent
        }
    );
    assert_eq!(pen(2).background.opacity, Opacity::Transparent);
    assert_eq!(
        (pen(3).background.color, pen(3).foreground, pen(3).underline),
        (Color::White, Color::Black, true)
    );
    // Data channel 2's background code (18 2E): black opaque.
    let mut decoder = Decoder::new(Channel::CC2);
    feed(&mut decoder)
        .code([0x1C, 0x29])
        .code([0x1C, 0x60])
        .code([0x18, 0x2E])
        .text("Z");
    assert_eq!(
        cell(decoder.displayed(), 15, 0).pen.background,
        Background::default()
    );
    // A PAC puts the background back to the default.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RDC)
        .code(ROW15)
        .code([0x10, 0x24])
        .code(ROW14)
        .text("D");
    assert_eq!(
        cell(decoder.displayed(), 14, 0).pen.background,
        Background::default()
    );
}

#[test]
fn special_extended_and_basic_characters() {
    let mut decoder = Decoder::new(Channel::CC1);
    let mut f = feed(&mut decoder);
    f.code(RCL).code(ROW15);
    for b2 in 0x30..=0x3F {
        f.code([0x11, b2]);
    }
    let row = row_text(decoder.hidden(), 15);
    // 11 39 is the transparent space: an empty cell.
    assert_eq!(row, "®°½¿™¢£♪à.èâêîôû");
    // Extended characters replace the standard character sent before them.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RCL)
        .code(ROW15)
        .text("!")
        .code([0x12, 0x27])
        .text("E")
        .code([0x12, 0x21])
        .text("s")
        .code([0x13, 0x34])
        .text("A")
        .code([0x13, 0x30])
        .text("<")
        .code([0x12, 0x3E])
        .text("|")
        .code([0x13, 0x37])
        .text("~}*\\^_`{|");
    assert_eq!(row_text(decoder.hidden(), 15), "¡ÉßÄ«│ñÑáéíóúç÷");
    // Every extended slot, both sets: at the first column the backspace has nothing to erase.
    for (b1, table) in [(0x12u8, &EXTENDED_ES_FR), (0x13, &EXTENDED_PT_DE)] {
        for (i, expected) in table.iter().enumerate() {
            let mut decoder = Decoder::new(Channel::CC1);
            feed(&mut decoder)
                .code(RCL)
                .code(ROW15)
                .code([b1, 0x20 + i as u8]);
            assert_eq!(cell(decoder.hidden(), 15, 0).ch, *expected);
        }
    }
    // The basic set's other bytes are ASCII; 7F is a solid block.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RCL).code(ROW15).text("Az09 '\x7f");
    assert_eq!(row_text(decoder.hidden(), 15), "Az09 '\u{25A0}");
}

#[test]
fn backspace_delete_to_end_of_row_and_the_last_column() {
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RCL)
        .code(ROW15)
        .code(BS)
        .text("ABCDEF");
    assert_eq!(row_text(decoder.hidden(), 15), "ABCDEF");
    feed(&mut decoder).code(ROW15).code([0x17, 0x22]).code(DER);
    assert_eq!(row_text(decoder.hidden(), 15), "AB");
    // Past the 32nd column, characters replace the last one; a backspace then erases it.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RCL)
        .code(ROW15)
        .text("0123456789012345678901234567890123");
    let row = row_text(decoder.hidden(), 15);
    assert_eq!(row.chars().count(), COLUMNS);
    // Columns 1-31 hold characters 0-30; the 32nd took 31, 32 and finally 33.
    assert_eq!(row, "01234567890123456789012345678903");
    assert_eq!(decoder.cursor(), (15, COLUMNS));
    feed(&mut decoder).code(BS);
    assert_eq!(row_text(decoder.hidden(), 15).chars().count(), COLUMNS - 1);
    feed(&mut decoder).code(DER);
    assert_eq!(row_text(decoder.hidden(), 15).chars().count(), COLUMNS - 1);
}

#[test]
fn field_two_and_data_channel_two_reach_only_their_channel() {
    let mut cc1 = Decoder::new(Channel::CC1);
    let mut cc2 = Decoder::new(Channel::CC2);
    let mut cc3 = Decoder::new(Channel::CC3);
    let mut cc4 = Decoder::new(Channel::CC4);
    let stream = [
        pair(0, 0x14, 0x29),
        pair(0, 0x14, 0x60),
        pair(0, b'O', b'N'),
        pair(0, b'E', 0),
        pair(0, 0x1C, 0x29),
        pair(0, 0x1C, 0x60),
        pair(0, b'T', b'W'),
        pair(0, b'O', 0),
        pair(1, 0x15, 0x29), // field 2 uses 15 for its misc codes
        pair(1, 0x15, 0x40),
        pair(1, b'T', b'H'),
        pair(1, 0x1D, 0x29),
        pair(1, 0x1D, 0x60),
        pair(1, b'F', b'O'),
    ];
    for decoder in [&mut cc1, &mut cc2, &mut cc3, &mut cc4] {
        for triplet in stream {
            decoder.feed(triplet);
        }
    }
    assert_eq!(rows(cc1.displayed()), [(15, "ONE".to_owned())]);
    assert_eq!(rows(cc2.displayed()), [(15, "TWO".to_owned())]);
    assert_eq!(rows(cc3.displayed()), [(5, "TH".to_owned())]);
    assert_eq!(rows(cc4.displayed()), [(6, "FO".to_owned())]);
    // DTVCC triplets never reach a 608 decoder.
    let mut decoder = Decoder::new(Channel::CC1);
    assert!(!decoder.feed(Triplet([0xFF, 0x14, 0x29])));
    assert!(!decoder.feed(Triplet([0xFE, 0x41, 0x41])));
}

#[test]
fn text_service_and_xds_are_not_captions() {
    let mut decoder = Decoder::new(Channel::CC3);
    let mut f = feed(&mut decoder);
    f.code([0x15, 0x29]).code(ROW15).text("AB");
    // An XDS packet (field 2): start, data, end and checksum.
    f.once([0x01, 0x03]).text("XDSDATA").once([0x0F, 0x1D]);
    f.text("CD");
    // Text Restart: Text data on the channel is not caption data, until a caption command.
    f.code([0x15, 0x2A]).text("TEXT");
    f.code([0x15, 0x29]).text("EF");
    assert_eq!(row_text(decoder.displayed(), 15), "ABCDEF");
    // A caption control code interrupts an XDS packet.
    let mut decoder = Decoder::new(Channel::CC3);
    feed(&mut decoder)
        .code([0x15, 0x29])
        .code(ROW15)
        .once([0x05, 0x01])
        .text("ZZ")
        .code(ROW15)
        .text("OK");
    assert_eq!(row_text(decoder.displayed(), 15), "OK");
    // CC1 Text (T1) after Text Restart or Resume Text Display, until Resume Caption Loading.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(RCL)
        .code(ROW15)
        .text("AB")
        .code(TR)
        .text("TEXT")
        .code([0x14, 0x2B])
        .text("MORE")
        .code(RCL)
        .text("CD");
    assert_eq!(row_text(decoder.hidden(), 15), "ABCD");
    // In field 1, a first byte 00-0F is ignored and the second byte is still text.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RDC).code(ROW15).once([0x05, b'Q']);
    assert_eq!(row_text(decoder.displayed(), 15), "Q");
}

#[test]
fn parity_errors_and_invalid_data_drop_what_is_built() {
    // A byte failing odd parity makes the pair invalid: it is not written and, like cc_valid
    // turning off after valid data, it drops a paint-on caption (Media3).
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RDC).code(ROW15).text("OK");
    decoder.take_touched();
    assert!(!decoder.feed(Triplet([0xFC, b'A', parity(b'B')])));
    assert!(decoder.take_touched());
    assert!(decoder.displayed().is_empty());
    // The next valid data builds again.
    feed(&mut decoder).code(ROW15).text("AGAIN");
    assert_eq!(rows(decoder.displayed()), [(15, "AGAIN".to_owned())]);
    // cc_valid off with data: a displayed pop-on caption stays, the one being loaded goes.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder)
        .code(ROW15)
        .text("UP")
        .code(EOC)
        .code(RCL)
        .text("NEXT");
    assert!(!decoder.feed(Triplet([0xF8, parity(b'X'), parity(b'Y')])));
    assert_eq!(rows(decoder.displayed()), [(15, "UP".to_owned())]);
    assert!(decoder.hidden().is_empty());
    // Invalid null padding is padding, not a turn-off.
    let mut decoder = Decoder::new(Channel::CC1);
    feed(&mut decoder).code(RDC).code(ROW15).text("OK");
    assert!(!decoder.feed(Triplet([0xF8, 0x80, 0x80])));
    assert_eq!(row_text(decoder.displayed(), 15), "OK");
}

#[test]
fn reset_returns_to_a_new_decoder() {
    let mut decoder = Decoder::new(Channel::CC2);
    feed(&mut decoder)
        .code([0x1C, 0x29])
        .code([0x1C, 0x60])
        .text("GONE");
    decoder.take_touched();
    decoder.reset();
    assert!(decoder.take_touched());
    assert!(decoder.displayed().is_empty());
    assert_eq!(decoder.mode(), Mode::Unknown);
    assert_eq!(decoder.channel(), Channel::CC2);
    decoder.reset();
    assert!(!decoder.take_touched());
}

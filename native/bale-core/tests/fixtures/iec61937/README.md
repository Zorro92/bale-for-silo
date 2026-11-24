# IEC 61937 golden fixtures

Expected output for `tests/iec61937_golden.rs`: what FFmpeg's `spdif` muxer writes for each input,
which Bale's packers (`src/iec61937.rs`) must reproduce byte for byte.

Each case has three kinds of file:

- `<case>.<ext>`: the elementary stream FFmpeg read (`.ac3`, `.eac3`, `.dts`, `.thd`, `.mlp`).
- `<case>.sizes`: the access units FFmpeg's demuxer and parser cut it into, one size per line
  (`ffprobe -f <demuxer> -i <case>.<ext> -show_entries packet=size -of csv=p=0`). The test feeds the
  packers exactly these units, as the muxer received them.
- `<case>[.<variant>].spdif`: `ffmpeg -f <demuxer> -i <case>.<ext> -c copy -f spdif [options] <out>`.

## How they were made

`python3 -I generate.py` (stdlib only) rebuilds all of them with the `ffmpeg` and `ffprobe` on PATH.
These were made on 2026-11-24 with **FFmpeg 9.0.2** (Fedora package, `ffmpeg version 9.0.2`). Another
FFmpeg may encode the real inputs differently, but each input and its expected output come from the same
run, so a regenerated set stays consistent.

No third-party media is in here. Real streams are FFmpeg's own encoders run on generated sine tones
(440/660 Hz); synthetic streams are built by `generate.py` from real headers and filler bytes. The local
test-media library has no TrueHD or DTS-HD clip (its MANIFEST lists lossless audio as a gap), and FFmpeg
has no DTS-HD encoder, so DTS-HD is synthetic: real DTS core frames from FFmpeg's encoder, each followed
by an extension substream with a valid sync word and size header (`nuExtSSFsize`) and filler. FFmpeg's
muxer and Bale's packer both carry the extension opaquely, so this checks the IEC 61937-5 type IV
framing exactly, but not against a real Master Audio stream.

| Case | Source | What it covers |
|---|---|---|
| `ac3-48k-5.1` | `ac3` encoder, 448 kbit/s, `-audio_service_type hi` | AC-3 bursts; bsmod 3 in Pc |
| `ac3-44k-stereo` | `ac3` encoder, 44.1 kHz, 192 kbit/s | 44.1 kHz frame sizes (they alternate) |
| `eac3-48k-5.1` | `eac3` encoder, 256 kbit/s | E-AC-3, 6-block frames: one per burst |
| `eac3-1block-7.1-synthetic` | synthetic, valid headers and CRCs | 1-block 5.1 frames each with a 7.1 dependent frame: 12 frames a burst |
| `eac3-3block-synthetic` | synthetic, valid headers and CRCs | 3-block frames: 2 a burst; a last unit FFmpeg drops (`flush` test) |
| `dts-48k-5.1` | `dca` encoder (`-strict -2`) | DTS type I (512 samples) |
| `dts-44k-stereo` | `dca` encoder, 44.1 kHz | 2048-byte frames that fill their period: no preamble |
| `dts-1024-synthetic`, `dts-2048-synthetic` | real core header, NBLKS/FSIZE changed, filler | Types II and III; an odd frame length; a frame that fills type III exactly with its preamble |
| `dts-le16`, `dts-be14`, `dts-le14` | `dca` encoder at 768 kbit/s, repacked | 16-bit little-endian and 14-bit packings |
| `dtshd-synthetic` | real cores + synthetic extension substreams | `.core`: core only (extension dropped); `.hbr`: `-dtshd_rate 768000` (HBR, 8 ch at 192 kHz); `.stereo`: `-dtshd_rate 192000`, where the sixth frame overflows and the rest go core-only |
| `truehd-48k-5.1` | `truehd` encoder (`-strict -2`) | MAT frames: codes, padding by input timing |
| `truehd-96k-stereo` | `truehd` encoder, 96 kHz | 80-sample access units |
| `mlp-48k-stereo` | `mlp` encoder (`-strict -2`) | MLP major sync (rate one byte later) |
| `truehd-timing-synthetic` | `truehd` encoder, `input_timing` rewritten (parity nibble kept valid) | A 10-unit gap (padded), a 40-unit gap and a step back (discontinuities: no padding) |

FFmpeg 9.0.2 prints decoder errors while probing the synthetic streams (their payloads are filler);
they don't affect the packets or the muxer output.

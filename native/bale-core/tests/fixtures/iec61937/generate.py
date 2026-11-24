#!/usr/bin/env python3
"""Regenerates the IEC 61937 golden fixtures with the ffmpeg and ffprobe on PATH (README.md).

Real streams come from FFmpeg's own encoders. Synthetic ones are built here where FFmpeg has no encoder
or never produces the variant: DTS-HD extension substreams, 1-block E-AC-3 with 7.1 dependent frames,
3-block E-AC-3, 1024/2048-sample and 14-bit/little-endian DTS, TrueHD timing jumps. Synthetic payloads
are filler; only the headers the parsers and packers read are real. Each case stores its input, the
access-unit sizes ffprobe reports for it (FFmpeg's demuxer and parser, as the muxer receives them) and
the spdif muxer's output for it.

Run with: python3 -I generate.py (stdlib only; writes next to this file).
"""

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def run(*args):
    subprocess.run(args, check=True, cwd=HERE)


def output(*args):
    return subprocess.run(args, check=True, cwd=HERE, capture_output=True, text=True).stdout


class Filler:
    """Deterministic filler bytes (xorshift32) that never start a sync word of any format here."""

    AVOID = {0x0B, 0x77, 0x7F, 0xFE, 0x1F, 0xFF, 0x64, 0xF8, 0x72}

    def __init__(self, seed):
        self.state = seed or 1

    def byte(self):
        while True:
            x = self.state
            x ^= (x << 13) & 0xFFFFFFFF
            x ^= x >> 17
            x ^= (x << 5) & 0xFFFFFFFF
            self.state = x
            if x & 0xFF not in self.AVOID:
                return x & 0xFF

    def bytes(self, count):
        return bytes(self.byte() for _ in range(count))


class Bits:
    def __init__(self):
        self.value = 0
        self.count = 0

    def put(self, width, value):
        assert 0 <= value < (1 << width)
        self.value = (self.value << width) | value
        self.count += width

    def to_bytes(self):
        pad = -self.count % 8
        return (self.value << pad).to_bytes((self.count + pad) // 8, "big")


def crc16_8005(data):
    """CRC-16, polynomial 0x8005, MSB first, initial 0: AC-3's crc (FFmpeg's AV_CRC_16_ANSI)."""
    crc = 0
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x8005) if crc & 0x8000 else crc << 1
            crc &= 0xFFFF
    return crc


# ---------------------------------------------------------------------------------------------------
# Real encodes


def sine_graph(rate, seconds, layout):
    src = f"sine=frequency=440:sample_rate={rate}:duration={seconds}[a];" \
          f"sine=frequency=660:sample_rate={rate}:duration={seconds}[b];[a][b]amerge=inputs=2"
    if layout == "stereo":
        return src + "[out]"
    return src + ",pan=5.1(side)|FL=c0|FR=c1|FC=0.5*c0+0.5*c1|LFE=0.1*c0|SL=0.5*c0|SR=0.5*c1[out]"


def encode(name, rate, seconds, layout, *codec_args):
    run("ffmpeg", "-v", "error", "-y", "-filter_complex", sine_graph(rate, seconds, layout),
        "-map", "[out]", *codec_args, name)


# ---------------------------------------------------------------------------------------------------
# Synthetic E-AC-3 (ATSC A/52 E.1.2): independent and dependent sync frames with valid headers and CRC


def eac3_frame(filler, size, strmtyp, numblkscod, acmod, lfeon, chanmap=None):
    assert size % 2 == 0
    bits = Bits()
    bits.put(16, 0x0B77)
    bits.put(2, strmtyp)
    bits.put(3, 0)                 # substreamid
    bits.put(11, size // 2 - 1)    # frmsiz
    bits.put(2, 0)                 # fscod: 48 kHz
    bits.put(2, numblkscod)
    bits.put(3, acmod)
    bits.put(1, lfeon)
    bits.put(5, 16)                # bsid
    bits.put(5, 27)                # dialnorm
    bits.put(1, 0)                 # compre
    if strmtyp == 1:
        bits.put(1, 1)             # chanmape
        bits.put(16, chanmap)
    bits.put(1, 0)                 # mixmdate
    bits.put(1, 0)                 # infomdate
    if strmtyp == 0 and numblkscod != 3:
        bits.put(1, 1)             # convsync
    bits.put(1, 0)                 # addbsie
    head = bits.to_bytes()
    body = head + filler.bytes(size - len(head) - 2)
    return body + crc16_8005(body[2:]).to_bytes(2, "big")


def eac3_synthetic(name, numblkscod, units, dependent):
    filler = Filler(0xEAC3 + numblkscod)
    stream = bytearray()
    for i in range(units):
        stream += eac3_frame(filler, 160 + 24 * (i % 5), 0, numblkscod, acmod=7, lfeon=1)
        if dependent:
            # Lrs/Rrs pair (chanmap bit 6 counting from the MSB): 5.1 + 2 = 7.1.
            stream += eac3_frame(filler, 96 + 16 * (i % 3), 1, numblkscod, acmod=2, lfeon=0,
                                 chanmap=1 << (15 - 6))
    write(name, stream)


# ---------------------------------------------------------------------------------------------------
# Synthetic DTS (ETSI TS 102 114)


def dts_frames(data):
    """Splits a 16-bit big-endian core-only stream by FSIZE."""
    frames, at = [], 0
    while at < len(data):
        assert data[at:at + 4] == b"\x7f\xfe\x80\x01"
        size = (((data[at + 5] & 0x03) << 12) | (data[at + 6] << 4) | (data[at + 7] >> 4)) + 1
        frames.append(data[at:at + size])
        at += size
    return frames


def dts_with(header_source, nblks, size, filler):
    """A core frame with header_source's header but NBLKS and FSIZE replaced, filler after the header."""
    h = bytearray(header_source[:16])
    h[4] = (h[4] & 0xFE) | (nblks >> 6)
    h[5] = ((nblks & 0x3F) << 2) | (((size - 1) >> 12) & 0x03)
    h[6] = ((size - 1) >> 4) & 0xFF
    h[7] = (((size - 1) & 0x0F) << 4) | (h[7] & 0x0F)
    return bytes(h) + filler.bytes(size - 16)


def dts_exss(size, filler):
    """A DTS-HD extension substream: real sync word and size header (nuExtSSFsize), filler after."""
    bits = Bits()
    bits.put(32, 0x64582025)       # SYNCEXTSSH
    bits.put(8, 0)                 # UserDefinedBits
    bits.put(2, 0)                 # nExtSSIndex
    bits.put(1, 0)                 # bHeaderSizeType: 8/16-bit sizes
    bits.put(8, 16 - 1)            # nuExtSSHeaderSize
    bits.put(16, size - 1)         # nuExtSSFsize
    bits.put(1, 0)                 # bStaticFieldsPresent
    head = bits.to_bytes()
    return head + filler.bytes(size - len(head))


def to_14bit(frame, little_endian):
    """Repacks a 16-bit core frame as 14-bit words with the sign of bit 13 in the top two bits."""
    value = int.from_bytes(frame, "big")
    total = len(frame) * 8
    words = -(-total // 14)
    value <<= words * 14 - total
    out = bytearray()
    for i in range(words):
        word = (value >> ((words - 1 - i) * 14)) & 0x3FFF
        if word & 0x2000:
            word |= 0xC000
        out += word.to_bytes(2, "little" if little_endian else "big")
    return bytes(out)


def swap16(data):
    data = data + b"\x00" * (len(data) % 2)
    return bytes(b for i in range(0, len(data), 2) for b in (data[i + 1], data[i]))


# ---------------------------------------------------------------------------------------------------
# Synthetic TrueHD timing: input_timing jumps, parity nibble kept valid


def truehd_units(data):
    units, at = [], 0
    while at < len(data):
        size = (((data[at] & 0x0F) << 8) | data[at + 1]) * 2
        units.append(bytearray(data[at:at + size]))
        at += size
    return units


def retime(unit, timing):
    def fold(x):
        return ((x >> 4) ^ x) & 0x0F

    change = fold(unit[2] ^ (timing >> 8)) ^ fold(unit[3] ^ (timing & 0xFF))
    unit[2], unit[3] = timing >> 8, timing & 0xFF
    unit[0] ^= change << 4


def truehd_timing(name, source):
    units = truehd_units(read(source))
    shift = 0
    for i, unit in enumerate(units):
        if i == 30:
            shift += 400       # 10 units late: padded (25600 bytes due, under half a frame)
        elif i == 45:
            shift += 1600      # 40 units late: over half a frame, a discontinuity (no padding)
        elif i == 60:
            shift -= 80        # backwards: also a discontinuity
        timing = (((unit[2] << 8) | unit[3]) + shift) & 0xFFFF
        retime(unit, timing)
    write(name, b"".join(units))


# ---------------------------------------------------------------------------------------------------


def read(name):
    with open(os.path.join(HERE, name), "rb") as f:
        return f.read()


def write(name, data):
    with open(os.path.join(HERE, name), "wb") as f:
        f.write(data)


def sizes(name, demuxer):
    found = output("ffprobe", "-v", "error", "-f", demuxer, "-i", name, "-select_streams", "a:0",
                   "-show_entries", "packet=size", "-of", "csv=p=0").split()
    data = read(name)
    assert sum(map(int, found)) == len(data), f"{name}: packets don't cover the file"
    return found


def spdif(name, demuxer, expected, *options):
    run("ffmpeg", "-v", "error", "-y", "-f", demuxer, "-i", name, "-c", "copy", "-f", "spdif",
        *options, expected)


def case(name, demuxer, outputs):
    stem = name.rsplit(".", 1)[0]
    with open(os.path.join(HERE, stem + ".sizes"), "w") as f:
        f.write("\n".join(sizes(name, demuxer)) + "\n")
    for suffix, options in outputs:
        spdif(name, demuxer, f"{stem}{suffix}.spdif", *options)


def main():
    version = output("ffmpeg", "-version").splitlines()[0]
    print(version, file=sys.stderr)

    # Real encodes.
    encode("ac3-48k-5.1.ac3", 48000, 0.1, "5.1", "-c:a", "ac3", "-b:a", "448k",
           "-audio_service_type", "hi", "-f", "ac3")
    encode("ac3-44k-stereo.ac3", 44100, 0.1, "stereo", "-c:a", "ac3", "-b:a", "192k", "-f", "ac3")
    encode("eac3-48k-5.1.eac3", 48000, 0.1, "5.1", "-c:a", "eac3", "-b:a", "256k", "-f", "eac3")
    encode("dts-48k-5.1.dts", 48000, 0.08, "5.1", "-c:a", "dca", "-strict", "-2", "-f", "dts")
    # 1411.2 kbit/s at 44.1 kHz: 2048-byte frames that fill their period, so no preamble (DTS CD).
    encode("dts-44k-stereo.dts", 44100, 0.05, "stereo", "-c:a", "dca", "-strict", "-2", "-f", "dts")
    encode("truehd-48k-5.1.thd", 48000, 0.06, "5.1", "-c:a", "truehd", "-strict", "-2", "-f", "truehd")
    encode("truehd-96k-stereo.thd", 96000, 0.05, "stereo", "-c:a", "truehd", "-strict", "-2",
           "-f", "truehd")
    encode("mlp-48k-stereo.mlp", 48000, 0.05, "stereo", "-c:a", "mlp", "-strict", "-2", "-f", "mlp")

    # Intermediates for the synthetic streams (not kept).
    encode("dts-768k.tmp", 48000, 0.05, "stereo", "-c:a", "dca", "-strict", "-2", "-b:a", "768k",
           "-f", "dts")
    encode("truehd-long.tmp", 48000, 0.1, "5.1", "-c:a", "truehd", "-strict", "-2", "-f", "truehd")

    # Synthetic E-AC-3: 1-block 5.1 + 7.1 dependent frames (12 frames a burst), 3-block (2 a burst).
    eac3_synthetic("eac3-1block-7.1-synthetic.eac3", 0, 18, True)
    eac3_synthetic("eac3-3block-synthetic.eac3", 2, 7, False)

    # Synthetic DTS: 1024- and 2048-sample cores (types II and III), one with an odd size.
    real = dts_frames(read("dts-48k-5.1.dts"))
    filler = Filler(0xD7)
    write("dts-1024-synthetic.dts",
          b"".join(dts_with(real[0], 31, size, filler) for size in (2400, 3001, 2800, 4000)))
    write("dts-2048-synthetic.dts",
          b"".join(dts_with(real[0], 63, size, filler) for size in (6000, 8184, 5200)))

    # Synthetic DTS-HD: real cores, each followed by an extension substream. The 6400-byte one doesn't
    # fit the stereo link (core-only fallback from there on); everything fits HBR. 1201 makes it odd.
    filler = Filler(0x4D)
    exss = (400, 1201, 2500, 4000, 6000, 6400, 800)
    write("dtshd-synthetic.dts",
          b"".join(core + dts_exss(size, filler) for core, size in zip(real, exss)))

    # DTS packings: 768 kbit/s cores as 16-bit little-endian and 14-bit big/little-endian.
    cores = read("dts-768k.tmp")
    write("dts-le16.dts", swap16(cores))
    frames = dts_frames(cores)
    write("dts-be14.dts", b"".join(to_14bit(frame, False) for frame in frames))
    write("dts-le14.dts", b"".join(to_14bit(frame, True) for frame in frames))

    truehd_timing("truehd-timing-synthetic.thd", "truehd-long.tmp")
    for tmp in ("dts-768k.tmp", "truehd-long.tmp"):
        os.remove(os.path.join(HERE, tmp))

    plain = [("", [])]
    case("ac3-48k-5.1.ac3", "ac3", plain)
    case("ac3-44k-stereo.ac3", "ac3", plain)
    case("eac3-48k-5.1.eac3", "eac3", plain)
    case("eac3-1block-7.1-synthetic.eac3", "eac3", plain)
    case("eac3-3block-synthetic.eac3", "eac3", plain)
    case("dts-48k-5.1.dts", "dts", plain)
    case("dts-44k-stereo.dts", "dts", plain)
    case("dts-1024-synthetic.dts", "dts", plain)
    case("dts-2048-synthetic.dts", "dts", plain)
    case("dts-le16.dts", "dts", plain)
    case("dts-be14.dts", "dts", plain)
    case("dts-le14.dts", "dts", plain)
    case("dtshd-synthetic.dts", "dts", [
        (".core", []),
        (".hbr", ["-dtshd_rate", "768000"]),
        (".stereo", ["-dtshd_rate", "192000"]),
    ])
    case("truehd-48k-5.1.thd", "truehd", plain)
    case("truehd-96k-stereo.thd", "truehd", plain)
    case("truehd-timing-synthetic.thd", "truehd", plain)
    case("mlp-48k-stereo.mlp", "mlp", plain)


if __name__ == "__main__":
    main()

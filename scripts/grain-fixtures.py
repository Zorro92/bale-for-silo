#!/usr/bin/env python3
"""Synthetic dav1d 1.5.4 oracles; invoke through grain-fixtures.sh (Python -I).

No source clips or machine paths are committed. The manifest and ffprobe JSON
retain provenance; params.bin is the same data in the 184-byte little-endian ABI.
"""

import argparse
import ctypes
import gzip
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess
import tempfile


FFMPEG = "ffmpeg"
FFPROBE = "ffprobe"


def run(*args):
    return subprocess.run(args, check=True, capture_output=True)


def numbers(value):
    return [int(v) for v in value.split()] if value else []


def points(component, prefix):
    xs = numbers(component.get(prefix + "_points_value", ""))
    ys = numbers(component.get(prefix + "_points_scaling", ""))
    assert len(xs) == len(ys)
    assert all(a < b for a, b in zip(xs, xs[1:]))
    return list(zip(xs, ys))


def pack_params(frame, grain, depth):
    components = grain["components"] + [{}] * (3 - len(grain["components"]))
    yp = points(components[0], "y")
    uvp = [points(c, "uv") for c in components[1:]]
    assert len(uvp) == 2 and len(yp) <= 14 and all(len(p) <= 10 for p in uvp)
    result = struct.pack("<4I", 184, int(grain["seed"]), frame["width"], frame["height"])
    result += bytes([
        depth, int(frame.get("color_space") in ("gbr", "rgb")), len(yp),
        grain["chroma_scaling_from_luma"], len(uvp[0]), len(uvp[1]),
        grain["scaling_shift"], grain["ar_coeff_lag"], grain["ar_coeff_shift"],
        grain["grain_scale_shift"], grain["overlap_flag"], grain["limit_output_range"],
    ])
    for values, count in [(yp, 14), (uvp[0], 10), (uvp[1], 10)]:
        result += bytes(v for p in values for v in p) + bytes(2 * (count - len(values)))
    n = 2 * grain["ar_coeff_lag"] * (grain["ar_coeff_lag"] + 1)
    for plane, c in enumerate(components):
        count = (n if yp else 0) if plane == 0 else (n + bool(yp) if uvp[plane - 1] or grain["chroma_scaling_from_luma"] else 0)
        coeffs = numbers(c.get("ar_coeffs_y" if plane == 0 else "ar_coeffs_uv", ""))
        # ffprobe prints lag-dependent slots even for inactive planes. Canonicalize
        # only those unused slots; all live coefficients come directly from side data.
        assert len(coeffs) >= count
        coeffs = coeffs[:count]
        result += struct.pack("<" + "b" * count, *coeffs) + bytes((24 if plane == 0 else 25) - count)
    result += bytes(2)
    for key in ["uv_mult", "uv_mult_luma", "uv_offset"]:
        result += struct.pack("<2h", *(c.get(key, 0) for c in components[1:]))
    assert len(result) == 184
    return result


# Hand-picked tables reach syntax/math branches not exercised by libaom's presets.
# Coefficients and scaling points are deliberately asymmetric and include negatives.
TABLES = {
    "lag0": dict(lag=0, ar_shift=6, grain_shift=3, scaling_shift=8, csfl=0, overlap=0,
                 y=[(7, 180), (79, 21), (151, 213), (243, 23)],
                 uv=[[(9, 171), (117, 33), (245, 211)], [(3, 219), (129, 19), (251, 143)]],
                 cy=[], cb=[51], cr=[-83], mult=[64, 192, 32, 96, 160, 511]),
    "lag3": dict(lag=3, ar_shift=9, grain_shift=0, scaling_shift=11, csfl=0, overlap=1,
                 y=[(3, 210), (17, 37), (31, 184), (49, 58), (67, 162), (83, 79), (101, 141),
                    (119, 96), (137, 127), (157, 117), (179, 103), (201, 138), (227, 82), (251, 159)],
                 uv=[[(5, 199), (29, 39), (53, 181), (77, 61), (101, 163), (125, 83), (151, 145), (179, 105), (211, 127), (249, 127)],
                     [(1, 29), (23, 209), (47, 51), (73, 187), (99, 73), (127, 165), (153, 95), (181, 143), (213, 117), (253, 121)]],
                 cy=[-17, 12, -7, 3, -5, 11, -13, 19, -23, 29, -31, 37, -41, 43, -47, 53, -59, 61, -67, 71, -73, 79, -83, 89],
                 cb=[11, -9, 7, -5, 3, -1, 13, -15, 17, -19, 21, -23, 25, -27, 29, -31, 33, -35, 37, -39, 41, -43, 45, -47, 83],
                 cr=[-3, 5, -7, 9, -11, 13, -15, 17, -19, 21, -23, 25, -27, 29, -31, 33, -35, 37, -39, 41, -43, 45, -47, 49, -97],
                 mult=[255, 0, 511, 0, 255, 0]),
    "csfl": dict(lag=1, ar_shift=7, grain_shift=2, scaling_shift=9, csfl=1, overlap=1,
                 y=[(13, 213), (71, 31), (127, 191), (201, 53), (241, 173)], uv=[[], []],
                 cy=[-23, 17, -11, 41], cb=[13, -19, 23, 31, -97], cr=[-29, 31, -37, 43, 83],
                 mult=[128, 128, 256, 128, 128, 256]),
    "no-points": dict(lag=3, ar_shift=8, grain_shift=1, scaling_shift=10, csfl=0, overlap=1,
                      y=[], uv=[[], []], cy=[0] * 24, cb=[0] * 25, cr=[0] * 25,
                      mult=[128, 128, 256, 128, 128, 256]),
    "restricted": dict(lag=2, ar_shift=6, grain_shift=1, scaling_shift=8, csfl=0, overlap=0,
                       y=[(1, 255), (97, 159), (239, 255)],
                       uv=[[(5, 255), (131, 151), (247, 255)], [(11, 251), (113, 173), (253, 255)]],
                       cy=[-11, 7, -3, 1, -5, 9, -13, 17, -19, 23, -29, 31],
                       cb=[-5, 7, -11, 13, -17, 19, -23, 29, -31, 37, -41, 43, 61],
                       cr=[3, -7, 11, -13, 17, -19, 23, -29, 31, -37, 41, -43, -71],
                       mult=[0, 255, 0, 255, 0, 511]),
}


def table_text(p):
    # filmgrn1 has no clip_to_restricted_range field (including libaom 3.15.2).
    # The p line is lag, AR shift, grain shift, scaling shift, csfl, overlap,
    # then Cb/Cr mult, luma mult, offset in their unsigned bitstream domains.
    lines = ["filmgrn1"]
    for start, end, seed in [(0, 400000, 45231), (400000, 10000000, 51993)]:
        lines.append(f"E {start} {end} 1 {seed} 1")
        values = [p["lag"], p["ar_shift"], p["grain_shift"], p["scaling_shift"], p["csfl"], p["overlap"], *p["mult"]]
        lines.append("\tp " + " ".join(map(str, values)))
        for label, values in [("sY", p["y"]), ("sCb", p["uv"][0]), ("sCr", p["uv"][1])]:
            lines.append(f"\t{label} {len(values)} " + " ".join(str(v) for pair in values for v in pair))
        for label, values in [("cY", p["cy"]), ("cCb", p["cb"]), ("cCr", p["cr"])]:
            lines.append(f"\t{label} " + " ".join(map(str, values)))
    return "\n".join(line.rstrip() for line in lines) + "\n"


def packet_obus(data):
    """Byte offsets of every low-overhead OBU, grouped by IVF packet."""
    assert data[:4] == b"DKIF"
    pos = struct.unpack_from("<H", data, 6)[0]
    packets = []
    while pos < len(data):
        size = struct.unpack_from("<I", data, pos)[0]
        pos += 12
        end = pos + size
        obus = []
        while pos < end:
            start = pos
            header = data[pos]
            assert header & 2, "expected OBU size field"
            pos += 1 + bool(header & 4)
            size = 0
            shift = 0
            while True:
                value = data[pos]
                pos += 1
                size |= (value & 127) << shift
                if value < 128:
                    break
                shift += 7
                assert shift <= 56
            obus.append(start)
            pos += size
        assert pos == end
        packets.append(obus)
    return packets


def patch_header_flags(clip, identity):
    """Lossless metadata-only bit edits for flags filmgrn1 cannot express.

    trace_headers reports bit positions relative to the start of each OBU,
    including its header. All-intra encoding guarantees two explicit grain
    records. Re-probing below verifies that dav1d actually receives these flags.
    """
    trace = run(FFMPEG, "-hide_banner", "-loglevel", "info", "-i", str(clip),
                "-map", "0:v", "-c:v", "copy", "-bsf:v", "trace_headers", "-f", "null", "-").stderr.decode()
    data = bytearray(clip.read_bytes())
    packets = packet_obus(data)
    packet = -1
    obu = -1
    counts = {"clip_to_restricted_range": 0, "matrix_coefficients": 0}
    for line in trace.splitlines():
        if "Packet:" in line:
            packet += 1
            obu = -1
        elif "OBU header" in line and packet >= 0:
            obu += 1
        elif packet >= 0:
            match = re.search(r"\]\s+(\d+)\s+(clip_to_restricted_range|matrix_coefficients)\s+([01]+)\s+=", line)
            if not match:
                continue
            bit, field, old = match.groups()
            if field == "matrix_coefficients" and not identity:
                continue
            value = 1 if field == "clip_to_restricted_range" else 0
            assert len(old) == (1 if field == "clip_to_restricted_range" else 8)
            start = packets[packet][obu]
            for n in range(len(old)):
                position = int(bit) + n
                index = start + position // 8
                mask = 128 >> (position % 8)
                assert bool(data[index] & mask) == (old[n] == "1")
                data[index] = (data[index] & ~mask) | (mask if (value >> (len(old) - 1 - n)) & 1 else 0)
            counts[field] += 1
    assert packet + 1 == len(packets) == 2
    assert counts["clip_to_restricted_range"] == 2
    if identity:
        assert counts["matrix_coefficients"] == 2
    clip.write_bytes(data)


def tags_for(frame, grain, depth):
    c = grain["components"] + [{}] * (3 - len(grain["components"]))
    yp = points(c[0], "y")
    uvp = [points(component, "uv") for component in c[1:]]
    tags = [f"bd{depth}", f"lag{grain['ar_coeff_lag']}", f"scaling{grain['scaling_shift']}",
            f"ar-shift{grain['ar_coeff_shift']}", f"grain-shift{grain['grain_scale_shift']}",
            "overlap-on" if grain["overlap_flag"] else "overlap-off",
            "restricted" if grain["limit_output_range"] else "full-clip"]
    if grain["chroma_scaling_from_luma"]:
        tags.append("csfl")
    if not yp:
        tags.append("no-y-points")
    if not any(uvp) and not grain["chroma_scaling_from_luma"]:
        tags.append("chroma-pass-through")
    if len(yp) == 14 and all(len(p) == 10 for p in uvp):
        tags.append("max-points")
    if any(component.get("uv_offset", 0) < 0 for component in c[1:]):
        tags.append("negative-uv-offset")
    if any(component.get(k, 0) < 0 for component in c[1:] for k in ["uv_mult", "uv_mult_luma"]):
        tags.append("negative-uv-mult")
    if any(any(a[1] != b[1] for a, b in zip(p, p[1:])) for p in [yp, *uvp]):
        tags.append("nonconstant-scaling")
        if depth == 10:
            tags.append("10bit-substeps")
    if frame.get("color_space") in ("gbr", "rgb"):
        tags.append("matrix-identity")
    w, h = frame["width"], frame["height"]
    if w % 32 or h % 32:
        tags.append("partial-blocks")
    if w % 2 and h % 2:
        tags.append("odd-width-height")
    if w % 32 == 1 and h % 32 == 1:
        tags.append("one-pixel-edge-blocks")
    return tags


def generate_case(output, work, name, depth, width, height, grain_option, restricted=False, identity=False):
    pix_fmt = "yuv420p" if depth == 8 else "yuv420p10le"
    clip = work / (name + ".ivf")
    # testsrc2 rounds odd sizes, so crop with exact=1 from a larger even canvas.
    source_w, source_h = (width + 1) & ~1, (height + 1) & ~1
    run(FFMPEG, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
        f"testsrc2=size={source_w}x{source_h}:rate=24", "-vf", f"format={pix_fmt},crop={width}:{height}:0:0:exact=1",
        "-frames:v", "2", "-c:v", "libaom-av1", "-cpu-used", "8", "-threads", "1", "-row-mt", "0",
        "-lag-in-frames", "0", "-g", "1", "-crf", "30", "-b:v", "0", "-pix_fmt", pix_fmt,
        "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt709",
        "-aom-params", grain_option, "-f", "ivf", str(clip))
    if restricted:
        patch_header_flags(clip, identity)
    probe = json.loads(run(FFPROBE, "-v", "error", "-export_side_data", "film_grain",
                           "-show_frames", "-of", "json", str(clip)).stdout)
    frames = probe["frames"]
    assert len(frames) == 2
    records = []
    tags = set()
    params = bytearray()
    for frame in frames:
        assert (frame["width"], frame["height"], frame["pix_fmt"]) == (width, height, pix_fmt), "decoded size/format changed"
        grain, = [s for s in frame.get("side_data_list", []) if s.get("type") == "av1"]
        assert grain["subsampling_x"] == grain["subsampling_y"] == 1
        if restricted:
            assert grain["limit_output_range"] == 1
        if identity:
            assert frame.get("color_space") in ("gbr", "rgb")
        params.extend(pack_params(frame, grain, depth))
        tags.update(tags_for(frame, grain, depth))
        records.append({k: frame[k] for k in ["width", "height", "pix_fmt", "color_space"]})
        records[-1]["grain"] = grain
    assert records[0]["grain"]["seed"] != records[1]["grain"]["seed"], "seeds must differ"
    case = output / name
    case.mkdir(exist_ok=True)
    (case / "params.bin").write_bytes(params)
    # Do not retain ffprobe's packet positions, addresses, or input pathname.
    (case / "params.json").write_text(json.dumps(records, indent=2) + "\n")
    frame_bytes = (width * height + 2 * ((width + 1) // 2) * ((height + 1) // 2)) * (1 if depth == 8 else 2)
    hashes = []
    for plain in [True, False]:
        options = ["-export_side_data", "film_grain"] if plain else []
        raw = run(FFMPEG, "-nostdin", "-hide_banner", "-loglevel", "error", "-c:v", "libdav1d", *options,
                  "-i", str(clip), "-map", "0:v:0", "-frames:v", "2", "-pix_fmt", pix_fmt, "-f", "rawvideo", "-").stdout
        assert len(raw) == frame_bytes * 2, "decoded frame size/count mismatch"
        filename = "plain.yuv.gz" if plain else "grained.yuv.gz"
        (case / filename).write_bytes(gzip.compress(raw, compresslevel=9, mtime=0))
        hashes.append(hashlib.sha256(raw).hexdigest())
    if "no-y-points" not in tags:
        assert hashes[0] != hashes[1], "grain was not applied"
    print(f"{name}: {width}x{height}, {', '.join(sorted(tags))}", flush=True)
    return "\t".join([name, str(width), str(height), str(depth), "2", ",".join(sorted(tags)), *hashes])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    version = run(FFMPEG, "-version").stdout.decode().splitlines()[0]
    assert version.startswith("ffmpeg version 8.1.3 "), "fixtures are pinned to ffmpeg 8.1.3"
    # This is a version query only, not execution of downloaded source.
    dav1d = ctypes.CDLL("libdav1d.so.7")
    dav1d.dav1d_version.restype = ctypes.c_char_p
    assert dav1d.dav1d_version().decode() == "1.5.4"
    aom = ctypes.CDLL("libaom.so.3")
    aom.aom_codec_version_str.restype = ctypes.c_char_p
    aom_version = aom.aom_codec_version_str().decode()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    tables = output / "tables"
    tables.mkdir(exist_ok=True)
    for name, params in TABLES.items():
        (tables / (name + ".tbl")).write_text(table_text(params))
    manifest = ["# Synthetic lavfi testsrc2 only; ffmpeg 8.1.3; dav1d 1.5.4 (54706fc6bc0cdecab7e9593974a4039cc038fca7); libaom " + aom_version,
                "# params.bin: two consecutive 184-byte little-endian BaleGrainParams; params.json: ffprobe side data",
                "# restricted cases use lossless trace_headers-guided clip-bit edits (filmgrn1 cannot store that flag)",
                "# restricted-identity additionally edits matrix_coefficients: a defensive, nonconforming 4:2:0 case accepted by dav1d's default non-strict mode",
                "# name\twidth\theight\tbit_depth\tframes\tbranches\tplain_sha256\tgrained_sha256"]
    with tempfile.TemporaryDirectory(prefix="bale-grain-fixtures-") as temporary:
        work = Path(temporary)
        for depth in [8, 10]:
            for n in range(1, 17):
                width, height = (130, 98) if n == 15 else ((65, 33) if n == 16 else (128, 96))
                name = f"preset{n:02}-{depth}"
                manifest.append(generate_case(output, work, name, depth, width, height, f"film-grain-test={n}"))
            for table in TABLES:
                width, height = {"lag0": (65, 33), "lag3": (333, 197), "csfl": (129, 97), "no-points": (65, 33), "restricted": (130, 98)}[table]
                name = f"{table}-{depth}"
                manifest.append(generate_case(output, work, name, depth, width, height, "film-grain-table=" + str(tables / (table + ".tbl")), restricted=table == "restricted"))
            manifest.append(generate_case(output, work, f"restricted-identity-{depth}", depth, 65, 33,
                                          "film-grain-table=" + str(tables / "restricted.tbl"), restricted=True, identity=True))
    (output / "manifest.tsv").write_text("\n".join(manifest) + "\n")
    total = sum(p.stat().st_size for p in output.rglob("*") if p.is_file())
    assert total <= 4_000_000, f"fixture budget exceeded: {total} bytes"
    print(f"44 cases, 88 frames, {total:,} bytes committed fixtures", flush=True)


if __name__ == "__main__":
    main()

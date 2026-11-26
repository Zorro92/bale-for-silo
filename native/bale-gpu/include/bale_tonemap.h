#ifndef BALE_TONEMAP_H
#define BALE_TONEMAP_H

#include <stdint.h>

#ifndef _HRESULT_DEFINED
#define _HRESULT_DEFINED
typedef int32_t HRESULT;
#endif

typedef struct ID3D11Device ID3D11Device;
typedef struct ID3D11DeviceContext ID3D11DeviceContext;
typedef struct ID3D11Texture2D ID3D11Texture2D;

/* FACILITY_ITF: the device rendered a result outside the CPU reference tolerance. */
#define BALE_TONEMAP_E_SELFTEST ((HRESULT)0x8004BA01u)

#ifdef __cplusplus
extern "C" {
#endif

/* ABI 1. Every HRESULT-returning call returns S_OK, or a failure HRESULT with no side effects. */
uint32_t bale_tonemap_abi_version(void); /* returns 1 */
/* Additive capability (Bale 2026-11-17); resolve with GetProcAddress, absent = NV12 only.
   Bits of the output texture formats bale_tonemap_apply accepts. P010 carries the same
   BT.709 limited-range SDR result as 10-bit codes (64-940 / 64-960, code << 6); apply picks
   the output depth from the output texture's format. */
#define BALE_TONEMAP_OUTPUT_NV12 1u
#define BALE_TONEMAP_OUTPUT_P010 2u
/* DOWNSCALE: config.reserved[0] / reserved[1] may give a larger visible source size (even, at most
   4x the output per axis; both zero = same as width/height). apply then area-downscales: each
   output pixel averages the tone-mapped display-linear BT.709 light of the source pixels it covers,
   weighted by overlap, and encodes once. The input must be at least that source size. */
#define BALE_TONEMAP_OUTPUT_DOWNSCALE 4u
/* INPUT_NV12: the input texture may be NV12 (8-bit codes, read as code << 2 = the 10-bit code);
   the output format is still the caller's choice (P010 per Bale's 10-bit rule). */
#define BALE_TONEMAP_INPUT_NV12 8u
/* SDR709: frames with transfer 1/6/14/15 and primaries 1, matrix 1 (BT.709 SDR) are accepted:
   BT.1886 decode to linear light, area downscale, re-encode; no tone curve, no gamut change. */
#define BALE_TONEMAP_SDR709 16u
/* NV12_DITHERED (Bale 2026-11-19): NV12 output is ordered-dithered before the 8-bit quantisation, in
   luma and chroma, with a static 8x8 Bayer threshold at the output pixel (no per-frame change, so still
   pictures stay still; Bale's debug switch r2dither takes rav1g's display-pass R2 threshold instead,
   2026-11-26). P010 output is unchanged. Absent = NV12 is plainly rounded. */
#define BALE_TONEMAP_OUTPUT_NV12_DITHERED 32u
/* Bit 64 is retired (Bale 2026-11-24) and stays unused: wrappers 2.1.0-bale.64 to bale.75 read it as
   permission for shader-readable decoder pools. apply always copies a decoder array slice first. */
uint32_t bale_tonemap_output_formats(void); /* returns NV12 | P010 | DOWNSCALE | INPUT_NV12 | SDR709 | NV12_DITHERED */
typedef struct BaleToneMapConfig {
    uint32_t width, height;     /* output size (input may be larger: decoder alignment, or a downscale) */
    uint32_t curve;             /* 1 = BT.2390 EETF, 2 = Hable */
    float    source_peak_nits;  /* 1000 = server default (tonemapx peak=100); 0 = none (1000) since 2026-11-24, older
                                   DLLs: E_INVALIDARG; < 0: E_INVALIDARG. PQ frames with MaxCLL / mastering metadata
                                   use that peak instead (BaleToneMapPeakInfo below) */
    float    target_white_nits; /* 100 = server default */
    float    desat;             /* 0 = server default */
    uint32_t reserved[4];       /* [0],[1]: visible source w/h for DOWNSCALE (0,0 = output size); [2],[3] zero */
} BaleToneMapConfig;
typedef struct BaleToneMapFrameInfo { /* H.273 code points, per frame */
    uint8_t  transfer;          /* 16 = PQ, 18 = HLG; SDR 1/6/14/15 with BT.2020 (gamut) or, with SDR709, BT.709 */
    uint8_t  primaries;         /* 9 = BT.2020; 1 = BT.709 (PQ, or SDR709) */
    uint8_t  matrix;            /* 9 = BT.2020 NCL; 1 = BT.709 */
    uint8_t  full_range;        /* 0 = limited */
    uint8_t  chroma_location;   /* AVChromaLocation of the input: 2 center, 3 topleft, 4 top, 5 bottomleft,
                                   6 bottom are honoured; 0 (unspecified), 1 and unknown values use left */
    uint8_t  has_mastering, has_content_light, reserved0;
    float    mastering_primaries[3][2], mastering_white[2];
    float    mastering_min_nits, mastering_max_nits; /* max: PQ source peak when MaxCLL is absent or larger */
    uint32_t max_cll, max_fall;                      /* max_cll: PQ source peak (400-10000 nits) */
    uint32_t reserved[4];
} BaleToneMapFrameInfo;
HRESULT bale_tonemap_create(ID3D11Device* device, const BaleToneMapConfig* config, void** handle);
/* input: P010, or NV12 with INPUT_NV12 (decoder array slice or SRV texture). output: NV12, or P010 when
   bale_tonemap_output_formats() has BALE_TONEMAP_OUTPUT_P010; RENDER_TARGET|SHADER_RESOURCE, 1 slice. */
HRESULT bale_tonemap_apply(void* handle, ID3D11DeviceContext* context, ID3D11Texture2D* input, uint32_t input_subresource, ID3D11Texture2D* output, const BaleToneMapFrameInfo* frame);
void    bale_tonemap_destroy(void* handle);

/* Additive (Bale 2026-11-24), every build; absent in older DLLs, which always use the config peak.
   The source peak the most recently created stage applies. PQ: the frame's MaxCLL, else its mastering
   display maximum (a MaxCLL above it gives the maximum), clamped to 400-10000 nits, else the config
   peak; HLG: the config peak; SDR frames don't change the report. Read does no GPU work and never waits:
   S_FALSE = apply holds the record, output untouched. */
typedef struct BaleToneMapPeakInfo {
    uint64_t stage;              /* that stage's create number (1, 2, ...); 0 = none created yet */
    uint32_t changes;            /* how often its peak or peak source changed; 0 = no PQ/HLG frame yet */
    uint32_t source;             /* 1 = config, 2 = MaxCLL, 3 = mastering display maximum */
    float    peak_nits;          /* the peak the tone curve uses */
    float    config_peak_nits;   /* the stage's config.source_peak_nits (0 = none: 1000) */
    uint32_t max_cll;            /* that frame's MaxCLL; 0 = none */
    float    mastering_max_nits; /* that frame's mastering display maximum; 0 = none */
    uint32_t transfer;           /* that frame's H.273 transfer: 16 PQ, 18 HLG */
    uint32_t flags;              /* bit 0: debug fixedpeak (config peak despite the metadata) */
} BaleToneMapPeakInfo;
/* Debug-package export (Cargo feature debug-probes), absent in production: resolve dynamically. */
HRESULT bale_tonemap_peak_read(BaleToneMapPeakInfo* info);

/* Optional debug-package exports (Cargo feature stage-timing), additive to ABI 1.
   Resolve dynamically; absent in production. Enable is captured by the next stage create,
   never changes an active handle. Read does no GPU work: S_FALSE = busy, output untouched.
   All values in one report belong to the latest measured stage and one completed window.
   Since 2026-11-24 the stage measures with two D3D11 event queries whose completion a poll thread
   sees on the CPU (timestamp queries crashed the Xbox driver); a measured apply then requests two
   submissions. Layout unchanged. */
typedef struct BaleToneMapTimingStats {
    uint64_t session, window;
    uint32_t state; /* 0 = no measured stage, 1 = collecting, 2 = timing disabled, 3 = destroyed */
    uint32_t frames, valid, disjoint, unavailable, pending; /* disjoint: always 0 with events */
    int32_t error; /* HRESULT of timing failure; playback continues */
    uint32_t reserved; /* failure reason: 0 = unknown (older DLL), 1 = create, 2 = data size,
                         3 = GetData, 4 = zero QPC frequency, 5 = sequence, 6 = contained panic,
                         7 = non-immediate context, 8 = device failure, 9 = no poll thread
                         (no immediate context or multithread protection, or no thread),
                         10 = an end event still pending after ~1 s of polling */
    double avg_ms[4], max_ms[4]; /* [0] total, an upper bound of the stage's elapsed GPU time;
                                    [1] end resolution, how long before the poll that saw the end
                                    the stage could have finished; [2], [3] 0 (were total, input
                                    copy, luma, chroma with timestamps); averages use valid only */
} BaleToneMapTimingStats;
HRESULT bale_tonemap_debug_timing_enable(uint32_t enabled); /* 0 or 1; default 0 */
HRESULT bale_tonemap_debug_timing_read(BaleToneMapTimingStats* stats);
/* Stage bench (Bale 2026-11-26, same feature): GPU timestamps per apply on a private hardware device (no
   video work), a Bayer and an R2-dithered stage in turn, NV12 and P010 output, at Bale's display routes.
   frames: timed applies per arm, 0 = 60, at most 240. Blocks for seconds; never during playback; refuses
   while timing is enabled. Writes a UTF-8 report without NUL (truncated at a character boundary) and
   returns its length. */
uint32_t bale_tonemap_debug_bench(uint32_t frames, uint8_t* report, uint32_t capacity);

#ifdef __cplusplus
}
#endif
#endif /* BALE_TONEMAP_H */

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
   pictures stay still). P010 output is unchanged. Absent = NV12 is plainly rounded. */
#define BALE_TONEMAP_OUTPUT_NV12_DITHERED 32u
uint32_t bale_tonemap_output_formats(void); /* returns NV12 | P010 | DOWNSCALE | INPUT_NV12 | SDR709 | NV12_DITHERED */
typedef struct BaleToneMapConfig {
    uint32_t width, height;     /* output size (input may be larger: decoder alignment, or a downscale) */
    uint32_t curve;             /* 1 = BT.2390 EETF, 2 = Hable */
    float    source_peak_nits;  /* 1000 = server default (tonemapx peak=100); <= 0: metadata later; v1 E_INVALIDARG */
    float    target_white_nits; /* 100 = server default */
    float    desat;             /* 0 = server default */
    uint32_t reserved[4];       /* [0],[1]: visible source w/h for DOWNSCALE (0,0 = output size); [2],[3] zero */
} BaleToneMapConfig;
typedef struct BaleToneMapFrameInfo { /* H.273 code points, per frame */
    uint8_t  transfer;          /* 16 = PQ, 18 = HLG; SDR 1/6/14/15 with BT.2020 (gamut) or, with SDR709, BT.709 */
    uint8_t  primaries;         /* 9 = BT.2020; 1 = BT.709 (PQ, or SDR709) */
    uint8_t  matrix;            /* 9 = BT.2020 NCL; 1 = BT.709 */
    uint8_t  full_range;        /* 0 = limited */
    uint8_t  chroma_location;   /* AVChromaLocation value; 0/1 and all other values use left */
    uint8_t  has_mastering, has_content_light, reserved0;
    float    mastering_primaries[3][2], mastering_white[2];
    float    mastering_min_nits, mastering_max_nits;
    uint32_t max_cll, max_fall;
    uint32_t reserved[4];
} BaleToneMapFrameInfo;
HRESULT bale_tonemap_create(ID3D11Device* device, const BaleToneMapConfig* config, void** handle);
/* input: P010, or NV12 with INPUT_NV12 (decoder array slice or SRV texture). output: NV12, or P010 when
   bale_tonemap_output_formats() has BALE_TONEMAP_OUTPUT_P010; RENDER_TARGET|SHADER_RESOURCE, 1 slice. */
HRESULT bale_tonemap_apply(void* handle, ID3D11DeviceContext* context, ID3D11Texture2D* input, uint32_t input_subresource, ID3D11Texture2D* output, const BaleToneMapFrameInfo* frame);
void    bale_tonemap_destroy(void* handle);

#ifdef __cplusplus
}
#endif
#endif /* BALE_TONEMAP_H */

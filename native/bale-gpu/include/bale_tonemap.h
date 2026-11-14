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
typedef struct BaleToneMapConfig {
    uint32_t width, height;     /* coded output size (input may be larger: decoder alignment) */
    uint32_t curve;             /* 1 = BT.2390 EETF, 2 = Hable */
    float    source_peak_nits;  /* 1000 = server default (tonemapx peak=100); <= 0: metadata later; v1 E_INVALIDARG */
    float    target_white_nits; /* 100 = server default */
    float    desat;             /* 0 = server default */
    uint32_t reserved[4];       /* zero */
} BaleToneMapConfig;
typedef struct BaleToneMapFrameInfo { /* H.273 code points, per frame */
    uint8_t  transfer;          /* 16 = PQ (only one handled in v1), 18 = HLG (later) */
    uint8_t  primaries;         /* 9 = BT.2020 */
    uint8_t  matrix;            /* 9 = BT.2020 NCL */
    uint8_t  full_range;        /* 0 = limited */
    uint8_t  chroma_location;   /* AVChromaLocation value; 0/1 and all other values use left */
    uint8_t  has_mastering, has_content_light, reserved0;
    float    mastering_primaries[3][2], mastering_white[2];
    float    mastering_min_nits, mastering_max_nits;
    uint32_t max_cll, max_fall;
    uint32_t reserved[4];
} BaleToneMapFrameInfo;
HRESULT bale_tonemap_create(ID3D11Device* device, const BaleToneMapConfig* config, void** handle);
HRESULT bale_tonemap_apply(void* handle, ID3D11DeviceContext* context, ID3D11Texture2D* input, uint32_t input_subresource, ID3D11Texture2D* output, const BaleToneMapFrameInfo* frame);
void    bale_tonemap_destroy(void* handle);

#ifdef __cplusplus
}
#endif
#endif /* BALE_TONEMAP_H */

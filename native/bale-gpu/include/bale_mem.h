#ifndef BALE_MEM_H
#define BALE_MEM_H
#include <stdint.h>

/* Independent memory ledger ABI 1, cdecl; resolve exports from bale_gpu.dll.
 * IDs: 0 unassigned; 1 tone map; 2 film grain; 3 subtitle overlay;
 * 4 engine target/staging; 5 zero-copy resources; 6-15 reserved for bale_gpu;
 * 16 d3d11va decoder texture array (slices); 17 stage output (tone map/downscale);
 * 18 dav1d upload pool + staging ring; 19 rav1g output images;
 * 20 rav1g process device present (count=1, bytes=0);
 * 21 d3d11va copy pool / rav1g direct-copy textures;
 * 22 rav1g decoder buffers (device_size per stream, count=streams);
 * 23-31 reserved for FFmpegInteropX; 32 player swap chain (estimate); 33-63 reserved.
 * Logical bytes only, excluding Xbox tiling/alignment padding. Count resources
 * (16 counts slices, 22 counts streams), including residents of unknown size (bytes=0, count=1). Account shared memory
 * once at its allocator, never again when opening a shared resource or a view.
 */
#ifdef __cplusplus
extern "C" {
#endif

typedef struct BaleMemEntry {
    uint32_t category;
    int32_t count;
    int64_t bytes;
    int64_t peak_bytes;
} BaleMemEntry; /* 24 bytes; offsets 0, 4, 8, 16 (default C packing). */

uint32_t bale_mem_abi_version(void); /* 1 */
/* Lock-free atomics only: no allocation/panic, locks or D3D calls.
 * category >= 64 ignored. Counters saturate at MAX on overflow and clamp
 * independently to 0 on underflow. Pair each allocation (+bytes,+count) with its final release (-bytes,-count).
 */
void bale_mem_account(uint32_t category, int64_t delta_bytes, int32_t delta_count);
/* The readers below are debug-package exports (Cargo feature debug-probes), absent in production: resolve them
 * dynamically. Accounting is in every build.
 *
 * Returns TOTAL needed; writes min(capacity,total) entries in ascending ID order.
 * NULL out queries size without writing. Entries remain after first use, even at
 * zero bytes/count/peak. Uses only atomic reads, no locks or D3D calls. Counters
 * are sampled independently during concurrent updates; peak is at least sampled bytes.
 * Use capacity=64 to avoid racing a size query with a newly used category.
 */
uint32_t bale_mem_snapshot(BaleMemEntry* out, uint32_t capacity);
void bale_mem_reset_peaks(void); /* peak=current; preserves history and underflows */
/* Lifetime underflowing account calls, once per call even if both counters
 * underflow; saturates at UINT64_MAX. Invalid category returns 0.
 */
uint64_t bale_mem_underflows(uint32_t category);

#ifdef __cplusplus
}
#endif
#endif

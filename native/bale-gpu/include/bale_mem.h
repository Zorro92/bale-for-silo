#ifndef BALE_MEM_H
#define BALE_MEM_H
#include <stdint.h>

/* Independent memory ledger ABI 1, cdecl; resolve exports from bale_gpu.dll.
 * IDs: 0 unassigned; 1 tone map; 2 film grain; 3 subtitle overlay;
 * 4 engine target/staging; 5 zero-copy resources; 6-15 reserved for bale_gpu;
 * 16 decoder frames; 17 tone-map output pool; 18 dav1d upload pool;
 * 19 rav1g output image pool; 20 rav1g prewarm resident; 21 other fork textures;
 * 22-31 reserved for FFmpegInteropX; 32 player swap chain (estimate); 33-63 reserved.
 * Logical bytes only, excluding Xbox tiling/alignment padding. Count resources,
 * including residents of unknown size (bytes=0, count=1). Account shared memory
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
/* Thread safe, no allocation/panic. category >= 64 ignored. Counters saturate
 * at MAX on overflow and clamp independently to 0 on underflow. Pair each
 * allocation (+bytes,+count) with its final release (-bytes,-count).
 */
void bale_mem_account(uint32_t category, int64_t delta_bytes, int32_t delta_count);
/* Returns TOTAL needed; writes min(capacity,total) entries in ascending ID order.
 * NULL out queries size without writing. Entries remain after first use, even at
 * zero bytes/count/peak. Each row is coherent, but the whole snapshot is not atomic.
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

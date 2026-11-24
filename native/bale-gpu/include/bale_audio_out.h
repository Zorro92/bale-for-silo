#ifndef BALE_AUDIO_OUT_H
#define BALE_AUDIO_OUT_H

#include <stdint.h>

#ifndef _HRESULT_DEFINED
#define _HRESULT_DEFINED
typedef int32_t HRESULT;
#endif

/* Exclusive-mode WASAPI audio output (Bale docs/audio-passthrough.md, "Exclusive output"). ABI 1, cdecl,
   additive (Bale 2026-11-24): resolve the exports from bale_gpu.dll with GetProcAddress; older DLLs lack them.
   Debug plumbing so far: no release path creates an output.

   An output is one exclusive-mode IAudioClient on the default render endpoint, activated the UWP way
   (MediaDevice default render id, ActivateAudioInterfaceAsync through the caller's mmdevapi.dll, which
   bale_gpu never imports), AudioCategory_Movie, event-driven with a buffer of one device period. Its render
   thread makes every COM call and fills the device each period from a ring of queued frames, playing
   silence when the ring runs dry (counted as an underrun).

   Ids: 1-65535, never 0, so an id fits the FFmpegOptions int the FFmpegInteropX fork receives. An id is not
   handed out again until the counter has gone round all others. Every call is thread-safe and may come from
   any thread; only create and destroy wait (never call them on the UI thread). Calls on a destroyed or
   unknown id return E_HANDLE (write: BALE_AUDIO_OUT_WRITE_CLOSED).

   Typical use: the app creates the output and hands its id to the fork; the fork's feeder writes interleaved
   frames with their pts; the app starts and pauses it with the video and calls bale_audio_out_sync with the
   video clock now and then; after a seek or stream switch the fork calls flush and writes from the new pts;
   the app destroys it. When the device is lost (status DEVICE_LOST, e.g. a headset took over) or the stream
   fails, writes return CLOSED and control calls the error: the caller falls back to its decoded path and
   destroys the output. */

#ifdef __cplusplus
extern "C" {
#endif

#define BALE_AUDIO_OUT_ABI_VERSION 1u

/* BaleAudioFormat.kind */
#define BALE_AUDIO_KIND_PCM      0u /* integer PCM */
#define BALE_AUDIO_KIND_IEC61937 1u /* IEC 61937 bitstream in 16-bit IEC 60958 words */

/* BaleAudioFormat.iec_subtype: the KSDATAFORMAT_SUBTYPE_IEC61937_* (ksmedia.h) the link declares */
#define BALE_AUDIO_IEC_NONE   0u
#define BALE_AUDIO_IEC_AC3    1u /* IEC61937_DOLBY_DIGITAL */
#define BALE_AUDIO_IEC_EAC3   2u /* IEC61937_DOLBY_DIGITAL_PLUS */
#define BALE_AUDIO_IEC_DTS    3u /* IEC61937_DTS */
#define BALE_AUDIO_IEC_DTS_HD 4u /* IEC61937_DTS_HD */
#define BALE_AUDIO_IEC_MLP    5u /* IEC61937_DOLBY_MLP: TrueHD / MLP in MAT frames */

/* What the endpoint is opened with. Checked by create (E_INVALIDARG otherwise) and turned into a
   WAVEFORMATEXTENSIBLE (cbSize 22), or a WAVEFORMATEXTENSIBLE_IEC61937 (cbSize 34) when encoded_rate is set.
   The console's TV path accepted exactly one exclusive format (probe, 2026-11-24): PCM, 8 channels,
   48000 Hz, 32-bit container, 24 valid bits, mask 0x63F. */
typedef struct BaleAudioFormat {
    uint32_t kind;             /* BALE_AUDIO_KIND_* */
    uint32_t channels;         /* PCM 1-8; IEC 2 (one line) or 8 (high bit rate) */
    uint32_t sample_rate;      /* PCM 8000-384000 Hz; IEC: the carrier rate, 32000/44100/48000 or
                                  128000/176400/192000 */
    uint32_t container_bits;   /* PCM 16, 24 or 32; IEC 16 (0 = 16) */
    uint32_t valid_bits;       /* 8..container_bits, 0 = container_bits (IEC 16); 24 valid bits in a 32-bit
                                  container sit in the top 3 bytes */
    uint32_t channel_mask;     /* ksmedia.h speaker mask with exactly one bit per speaker; 0 = the default for
                                  1, 2 or 8 speakers (0x4, 0x3, 0x63F). Interleaving follows the mask's bit
                                  order (7.1: FL FR FC LFE BL BR SL SR) */
    uint32_t iec_subtype;      /* BALE_AUDIO_IEC_*: 0 for PCM, 1-5 for IEC */
    uint32_t encoded_rate;     /* IEC only: the decoded content's rate (8000-768000); nonzero opens
                                  WAVEFORMATEXTENSIBLE_IEC61937, whose mask then names the decoded speakers
                                  (Microsoft's E-AC-3 example: 2-ch 192 kHz link, 48 kHz content, mask 0x3F,
                                  6 encoded channels). PCM: 0 */
    uint32_t encoded_channels; /* with encoded_rate: the decoded channel count, 0 = channels; otherwise 0 */
    uint32_t reserved[3];      /* zero */
} BaleAudioFormat; /* 48 bytes */

/* BaleAudioOutOpenInfo.step: where create stopped */
#define BALE_AUDIO_OUT_STEP_DONE       0u
#define BALE_AUDIO_OUT_STEP_ARGUMENTS  1u /* format, ring_ms or a pointer: E_INVALIDARG / E_POINTER */
#define BALE_AUDIO_OUT_STEP_MODULE     2u /* mmdevapi.dll null or without ActivateAudioInterfaceAsync */
#define BALE_AUDIO_OUT_STEP_THREAD     3u /* render thread, COM apartment, event, or the open did not finish
                                             within ~6 s (HRESULT_FROM_WIN32(ERROR_TIMEOUT)) */
#define BALE_AUDIO_OUT_STEP_DEVICE_ID  4u /* MediaDevice.GetDefaultAudioRenderId failed or is empty */
#define BALE_AUDIO_OUT_STEP_ACTIVATE   5u /* ActivateAudioInterfaceAsync, including its 2 s wait */
#define BALE_AUDIO_OUT_STEP_FORMAT     6u /* IsFormatSupported(EXCLUSIVE): AUDCLNT_E_UNSUPPORTED_FORMAT,
                                             AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED (headset, or Allow
                                             passthrough off), AUDCLNT_E_DEVICE_IN_USE ... */
#define BALE_AUDIO_OUT_STEP_INITIALIZE 7u /* GetDevicePeriod / Initialize, including the re-aligned retry */
#define BALE_AUDIO_OUT_STEP_SERVICES   8u /* SetEventHandle, GetBufferSize, IAudioRenderClient, IAudioClock */
#define BALE_AUDIO_OUT_STEP_TABLE      9u /* 8 outputs already open: E_OUTOFMEMORY */

/* BaleAudioOutOpenInfo.mmcss */
#define BALE_AUDIO_OUT_MMCSS_NONE      0u /* avrt.dll not loaded in the process: normal priority */
#define BALE_AUDIO_OUT_MMCSS_PRO_AUDIO 1u /* the render thread runs in MMCSS "Pro Audio" */
#define BALE_AUDIO_OUT_MMCSS_FAILED    2u /* AvSetMmThreadCharacteristicsW refused: normal priority */

/* What create did, also when it failed; fields of steps after `step` stay zero. */
typedef struct BaleAudioOutOpenInfo {
    uint32_t step;                 /* BALE_AUDIO_OUT_STEP_*: DONE on success */
    HRESULT  hr;                   /* create's result */
    HRESULT  activate_hr;          /* ActivateAudioInterfaceAsync's result */
    HRESULT  properties_hr;        /* IAudioClient2::SetClientProperties(AudioCategory_Movie); not fatal */
    HRESULT  format_hr;            /* IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE) */
    HRESULT  initialize_hr;        /* the first Initialize */
    HRESULT  realign_hr;           /* Initialize of the new client after AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED */
    uint32_t activate_ms;          /* how long the first activation took */
    int64_t  default_period_100ns; /* GetDevicePeriod */
    int64_t  minimum_period_100ns;
    int64_t  period_100ns;         /* buffer duration = periodicity Initialize took (the default period, or
                                      the aligned one) */
    uint32_t buffer_frames;        /* GetBufferSize: frames the device takes each period */
    uint32_t ring_frames;          /* ring capacity */
    uint32_t mmcss;                /* BALE_AUDIO_OUT_MMCSS_* */
    uint32_t realigned;            /* 1 = Microsoft's re-align recipe ran (GetBufferSize, new client,
                                      Initialize with the aligned duration) */
    uint32_t reserved[2];
} BaleAudioOutOpenInfo; /* 80 bytes */

/* BaleAudioOutStats.status */
#define BALE_AUDIO_OUT_STATUS_STOPPED     0u /* created, not started yet */
#define BALE_AUDIO_OUT_STATUS_RUNNING     1u
#define BALE_AUDIO_OUT_STATUS_PAUSED      2u
#define BALE_AUDIO_OUT_STATUS_DEVICE_LOST 3u /* AUDCLNT_E_DEVICE_INVALIDATED / _RESOURCES_INVALIDATED: fall back */
#define BALE_AUDIO_OUT_STATUS_FAILED      4u /* another device error, or no buffer event for 2 s while running
                                                (HRESULT_FROM_WIN32(ERROR_TIMEOUT)): fall back */

/* pts_100ns of a write that continues the previous one; also "unknown" in device_pts_100ns */
#define BALE_AUDIO_PTS_NONE (-9223372036854775807LL - 1)
/* bale_audio_out_write: unknown id, or an output that can no longer play (lost, failed, being destroyed) */
#define BALE_AUDIO_OUT_WRITE_CLOSED 0xFFFFFFFFu

/* A snapshot, taken without waiting for the render thread. Positions count from create or the last flush. */
typedef struct BaleAudioOutStats {
    uint32_t status;                /* BALE_AUDIO_OUT_STATUS_* */
    HRESULT  error;                 /* what ended the stream (DEVICE_LOST / FAILED), else S_OK */
    uint32_t buffer_frames;         /* frames the device takes each period */
    uint32_t ring_frames;           /* ring capacity */
    uint32_t queued_frames;         /* in the ring now */
    uint32_t queued_ms;             /* the same in ms, rounded down */
    uint32_t underruns;             /* device periods not filled entirely from the ring while playing */
    uint32_t flushes;               /* bale_audio_out_flush calls */
    uint64_t frames_written;        /* frames bale_audio_out_write accepted since create */
    uint64_t frames_rendered;       /* frames handed to the device: queued data, padding and silence */
    uint64_t silence_frames;        /* underrun silence */
    uint64_t events;                /* device periods filled (including the one loaded before each start) */
    int64_t  device_position_100ns; /* IAudioClock position; 0 before the first reading */
    int64_t  device_qpc_100ns;      /* QueryPerformanceCounter time of that reading in 100 ns (0 = none) */
    int64_t  device_pts_100ns;      /* pts of the frame at that position; BALE_AUDIO_PTS_NONE = unknown */
    int64_t  sync_error_100ns;      /* at the last measured sync: pts at the device position (extrapolated to
                                       the reference's QPC time) minus the video position; + = audio ahead */
    uint32_t syncs;                 /* sync references measured */
    uint32_t syncs_unmeasured;      /* references while not playing or before any timed frame played */
    uint32_t drop_corrections;      /* corrections that dropped queued frames (audio behind) */
    uint32_t pad_corrections;       /* corrections that inserted silence (audio ahead) */
    uint64_t dropped_frames;
    uint64_t padded_frames;
    uint32_t reserved[6];
} BaleAudioOutStats; /* 152 bytes */

uint32_t bale_audio_out_abi_version(void); /* BALE_AUDIO_OUT_ABI_VERSION */

/* Opens the default render endpoint in exclusive mode with `format` and starts its render thread, stopped.
   mmdevapi: the caller's loaded mmdevapi.dll (HMODULE; bale_gpu keeps its own reference while the output
   lives). ring_ms: ring capacity, 0 = 500, else 50-1000 (at least two device buffers either way): the most
   the feeder can queue ahead. Waits for the activation and the format calls (normally milliseconds, at most
   ~6 s); never call on the UI thread. On success writes the id; on failure *id = 0 and the HRESULT says why
   (AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED, AUDCLNT_E_UNSUPPORTED_FORMAT, AUDCLNT_E_DEVICE_IN_USE, ...), so the
   caller can fall back. info may be NULL. */
HRESULT bale_audio_out_create(void* mmdevapi, const BaleAudioFormat* format, uint32_t ring_ms, uint32_t* id,
                              BaleAudioOutOpenInfo* info);

/* Queues interleaved frames in the format's layout and returns the bytes taken: whole frames only (resend a
   remainder with the next data), as many as fit. Never waits for the device. 0 = the ring is full: wait a few
   ms (the feeder must not read the file far ahead of the video) and retry. BALE_AUDIO_OUT_WRITE_CLOSED = stop
   feeding (see above). pts_100ns times the first frame (100 ns, the video's timeline);
   BALE_AUDIO_PTS_NONE continues from the previous frames by counting. A pts within 2 ms of the counted one
   continues the timeline; a larger jump starts a new one there. */
uint32_t bale_audio_out_write(uint32_t id, const void* data, uint32_t bytes, int64_t pts_100ns);

/* Start or resume playing the queued frames / pause (queued frames stay). Idempotent. */
HRESULT bale_audio_out_start(uint32_t id);
HRESULT bale_audio_out_pause(uint32_t id);

/* A seek or stream switch: drops everything queued and resets the device stream (its clock restarts at 0).
   Frames written after the call start a new timeline at their pts and play on if the output was started.
   Idempotent. */
HRESULT bale_audio_out_flush(uint32_t id);

/* A sync reference from the clock master: the video position (100 ns) at the QueryPerformanceCounter value
   qpc_ticks (raw counter, > 0) when it was read. Call now and then while both play (e.g. every 100-250 ms),
   not during a seek. The render thread measures the error at its next period: PCM is corrected with whole
   frames when the error passes 20 ms (silence when the audio is ahead, dropped queued frames when behind; at
   most 1 s per reference; errors beyond 10 s are taken for a stale reference and ignored). IEC 61937 is only
   measured for now: corrections must keep bursts whole. */
HRESULT bale_audio_out_sync(uint32_t id, int64_t video_position_100ns, int64_t qpc_ticks);

HRESULT bale_audio_out_stats(uint32_t id, BaleAudioOutStats* stats);

/* The format the output was created with, defaults filled in: what a feeder handed only the id converts to. */
HRESULT bale_audio_out_format(uint32_t id, BaleAudioFormat* format);

/* Stops the stream, ends the render thread and releases the client; safe in any state, also after errors.
   Waits for the thread (at most 3 s; HRESULT_FROM_WIN32(ERROR_TIMEOUT) leaves a stuck thread to finish on its
   own). The id names nothing afterwards. */
HRESULT bale_audio_out_destroy(uint32_t id);

#ifdef __cplusplus
}
#endif
#endif /* BALE_AUDIO_OUT_H */

# Apple TV protocol parity

Owner's rule (AGENTS.md, 2026-11-17): playback planning and routing follow the Apple TV app. This is the
audit of what the tvOS client declares or sends on protocol-v3 start and replan
(`silo-apple` `ApplePlaybackV3Capabilities.swift`, `PlaybackProtocolV3Models.swift`, `StreamRequest.swift`,
`PlaybackSessionBridge.swift`) against what Bale sends (`PlaybackStartBuilder`, `PlaybackRequests`,
`PlaybackReplan`, `PlaybackClient`, `evidence.rs`). Each field's effect was checked in `silo-server`
(`internal/playback/protocol_v3.go`, `capabilities_v3.go`, `plan_v3.go`, `internal/api/handlers/playback_v3.go`,
`playback_connection.go`). Snapshot: silo-apple and silo-server as of 2026-11-16.

Rule for the decisions: a claim Bale cannot actually honour is worse than no claim. "Skip" means
Bale cannot honour it today, or the server does nothing with it; "owner" means Bale could, but it needs a
player or product decision first.

## Client features (`client_features`)

| Feature | Apple | Bale before | Decision | Why |
|---|---|---|---|---|
| `playback_plan_v3`, `client_video_transformations_v1`, `device_quirks_v1`, `seek_reanchor_v1`, `direct_stream_resume_v1` | Yes | Yes | — | Same. |
| `software_video_decode_v1` | Yes (video) | Yes, while a software decode entry exists | — | Same intent; Bale drops it with its software entries. |
| `embedded_subtitles_v1` | Yes | Yes, with a `native_embedded` claim | — | Same. |
| `header_authenticated_media_v1` | Yes (and requires the server to offer it) | No | **Implemented** | Bale already fetches every plan URL (stream, HLS children, `.sup` windows, sidecars, fonts) through the authenticated client with bearer refresh (`PlaybackMedia`, `NativeHeaderRefresh`), and `PlaybackMediaUrl` accepts only API-relative media routes. Declaring it makes the server keep media API-local with no signed credential in URLs. Without it a server with proxy nodes may hand out signed absolute proxy URLs, which Bale refuses. Attempt-sticky on the server. |
| `authorized_media_origins_v1` | Negotiated when the server offers it | No | Not now (owner, 2026-11-18) | Only useful on servers with proxy nodes. Restores proxy-node egress. Bale would have to accept absolute `/stream/v3/{session}[/master.m3u8\|/segment/…]` URLs on a server-named origin (https, or http only for an http API), send the bearer there, and let the Rust HLS reader follow children on that origin. A security boundary change in `PlaybackMediaUrl` and `hls.rs`; only worth it on servers with proxy nodes. |
| `output_change_v1` | Yes | No | Later (owner, 2026-11-18) | The feature only enables the `output_change` replan. Bale would need to notice an HDMI mode or audio sink change while playing and replan (player page work). Goes with `output_context_id` below. |
| `playback_route_diagnostics` | Yes | No | Skip | The server reads it from nobody's request (only advertises it). Bale posts only `plan_summary` route events, which need `playback_plan_summary_v1` from the server, not this. |
| `hls_subtitle_renditions_v1`, `hls_remux_subtitle_renditions_v1` | Yes | No | Skip | The server would put the subtitle into the HLS stream as a WebVTT `SUBTITLES` rendition instead of a sidecar. Bale's overlay draws fetched sidecars and its Rust HLS reader reads one media playlist; without the feature the server keeps sidecars, which Bale plays. |
| `hls_subtitle_renditions_all_v1` | Negotiated | No | Skip | Requires the above. |
| `buffering_quality_v1` on starts | Yes, when offered | Yes, when offered and unmetered | — | Same, with the highest current reading as a `probe` estimate. |
| `buffering_quality_v1` on replans | Yes, when offered | **No**: replans sent the builder's list | **Implemented** | A non-seek replan replaces the server's stored features with the replan's list, and the connection reading and learned limit apply only when it names the feature. A track change or failure recovery was planned without the Auto budget its start had. `PlaybackClient.ReplanAsync` now repeats what the attempt's start negotiated (also `subrip_sidecar_v1`, which the server pins anyway). Replans still send no estimate; the server fills in its reading. |
| `subrip_sidecar_v1` | No | Negotiated | — | Bale-only; the overlay parses SubRip itself. |
| `paused_timeout_v1` | Yes (video starts and their replans; Apple `80ae9437`) | No | **Implemented** (2026-11-24) | Server `dde5ed2` ends a session left paused past the administrator's limit (15 min by default) and answers progress, replans and the seek reanchor 410 `playback_session_paused_timeout` to an attempt that advertised it, else `playback_session_ended`. Every start sends it and replans repeat the start's list (attempt-sticky on the server). Either 410 leaves the player with the official notice over the page it returns to and starts nothing (`PlaybackServerEnds`, `PlayerPage.ServerEnd.cs`); progress reads every error as a problem so the type survives (the contract lists no 410 there). |
| `layout_aware_passthrough` | No (no sink layouts) | Only with passthrough entries (none today) | — | Same outcome. |

## Request fields

| Field | Apple | Bale before | Decision | Why |
|---|---|---|---|---|
| `learned_limit` (`ignore` / `clear`) | Sent per buffering mode and Restore quality | No | **Implemented** | Starts and replans send `ignore` while `playback.buffering_quality_mode` is Never and the request names `buffering_quality_v1` (`PlaybackProtocol.LearnedLimit`); Settings has the mode and the learned-limit Reset row (docs/player-plan.md, "Connection speed test"). Restore quality on the player's undo pill sends `clear` (docs/player-plan.md, "Buffering quality"). |
| `bandwidth_estimate_source: observed_rebuffer`, `observed_throughput_kbps` | Sent on a rebuffer step-down | No | **Implemented** | `BufferingQualityController` (a port of Apple's, replaying the server's conformance vectors) lowers Auto with the step-down estimate, `observed_rebuffer` and the plan's measured throughput (omitted on HLS, as Apple omits it on server HLS). Later replans of the attempt repeat the kept estimate without a source. Both fields are dropped unless the attempt negotiated `buffering_quality_v1`. |
| `plan_summary` route events (`playback_plan_summary_v1`) | Sent when a plan stops playing | No | **Implemented** | Posted when a plan is replaced, ends, fails or the player leaves, only when the server advertises the feature. Not kept on the device across an app kill (Apple keeps a journal); best effort, never retried. |
| Failure classification `subtitle_embedded_failed` | Sent when an embedded subtitle cannot be mounted | No | **Implemented** (owner-approved 2026-11-17) | `PlaybackEmbeddedSubtitleFailure` decides; the player page sends it. Only a plan whose subtitle is native (`subtitle.embedded`, mode `render`) can fail; sidecars, packaged artifacts, burn-in and Off never do. Confirmed failures are deterministic mount checks: the plan itself (not `original_http`, or no stream index the wrapper accepts: Apple's `invalidEmbeddedSubtitle`), a mount that is not on the FFmpeg demuxer, no overlay, a refused `BindBitmaps` (demuxer DVD/DVB and the setup of direct-MKV PGS `.sup` windows), and after open a bound stream missing from FFmpeg's `SubtitleStreams` (Apple's `validateEmbeddedSubtitleSelection`). A `.sup` window fetch or decode failure is retried and never replans. The recovery is sent from MediaOpened before Play (or at once for a subtitle answer adopted in place) as one `failure_recovery` with the failed plan's key, the source position and the same tracks, resending features, capabilities and context unchanged: no codec, delivery, DV7 or range withdrawal, and it skips the SDR-downscale remount. The server disables native selection for the rest of the attempt. It spends the page's single recovery replan like `audio_track_selection_failed`; once that is spent, a subtitle failure leaves the video playing without subtitles instead of an error, so it cannot loop. |
| `progress_persistence` | Omitted (server default) | `"server"` | — | Same meaning. |
| `allow_alternate_versions` | Watch-party only | `false` only under a version-checked range claim | — | Bale-specific guard. |

## Capabilities and context

| Field | Apple | Bale before | Decision | Why |
|---|---|---|---|---|
| `client_playback_context.device.manufacturer` / `model` | `Apple` / machine id | Platform only | **Implemented** | The server records the model on its route events (admin diagnostics). On Xbox the device name is the product name ("Xbox Series X"); elsewhere it is a PC's own name, so non-Xbox sends neither. |
| `device.os_version`, `platform_details` | Yes | No | Skip | Diagnostics only (quirks read `platform_details` for Android). The OS version needs a `Bale.Xbox` change for no planning effect. |
| `output.sink_type`, `output.output_context_id` | Yes (hashed route identity) | No | Later, with `output_change_v1` (owner, 2026-11-18) | The server compares the id for output-change intent and rejects a seek replan whose id changed. Without output-change replans a changing id only adds risk. |
| `client_capabilities.max_resolution` | `2160p` | Not sent | Skip | It caps Auto's transcode height; silo-server already fits transcodes to Bale's platform-attested H.264 decode entries (`h264BoundedQualityV3`), which carry the same limit per codec. |
| `audio_evidence: declared` | Yes | `platform_attested` | Skip | The server treats only `exact` differently (passthrough). |
| Dolby Vision `dolby_vision_profiles` `[5, 8]` and `client_dv7_to_dv81` | Yes on DV panels | No | Skip | Owner's Dolby Vision rule: no DV output claim until passthrough is implemented and verified on a DV display. |
| `client_dv7_to_hdr10` | Yes | Yes | — | Same (FFmpeg base layer). |
| `client_managed_dynamic_range_v1` | Yes | Per selected version | — | Bale's is narrower on purpose (AGENTS.md). |
| `client_selected_audio_track_v1` | Yes | Yes | — | Same. |
| Validated claims `authenticated_stream_headers`, `client_subtitle_overlay` | Yes | No | Skip | The server never reads either. |
| `native_embedded` text codecs (SubRip/ASS/WebVTT in MKV, `mov_text` in MP4) and `embedded_text` | Yes | Bitmap codecs in MKV only | Skip | Bale's overlay takes text from sidecars, not from the demuxer. The server extracts the sidecar instead. |
| `native_embedded` PGS in `ts` / `m2ts` | Yes (Aether reassembles split PES display sets) | No | Only after a console test with a real file (owner, 2026-11-18) | The FFmpeg bitmap path might work, but it is unverified on the console. |
| `embedded_bitmap` on packaged deliveries | No | Yes (PGS `.sup` windows) | — | Bale-only, verified. |
| HLS delivery containers | `hls`, `mpegts`, `fmp4`, `mp4` | `m3u8`, `hls` | — | The server gates on the stream container `hls`, which both list. |

## Server features Bale reads

| Feature | Apple | Bale | Note |
|---|---|---|---|
| `sequenced_progress_v1` | Required | Required | Same. |
| `neutral_playback_v3_contract_v1`, `header_authenticated_media_v1` | Required, else "update the server" | Not required | Bale keeps playing on an older server; it accepts its signed `st=` API-local URLs. |
| `plan_source_duration_v1` | Decides whether to fall back to the catalog duration | Not read | Bale only uses the plan's duration, which is right on servers that offer the feature. |
| `plan_active_quality_v1` | Quality menu | Logged; the buffering controller's lower rung | Bale has no quality menu yet; a lowering steps from the named rung when the server reports one. |
| `playback_plan_summary_v1` | Read | Read | Gates `plan_summary` route events. |
| `seek_reanchor_v1`, `buffering_quality_v1`, `connection_speed_v1`, `subrip_sidecar_v1` | Read | Read | Same. |

## Console check

The `header_authenticated_media_v1` claim changes every plan URL's shape (no `st=` token; transcodes
answer only an authenticated owner). On the console, check direct play (MP4 and MKV), a progressive remux
with a seek, an HLS transcode with a seek, a text sidecar with fonts (ASS), and PGS on HLS. Check them
again after the access token has been refreshed during playback.

//! Home follows watch state from other devices: while Home is on screen, Bale holds the server's v2 events socket on
//! the `user_state` channel and re-reads Home when the active profile's watch state changes, so a title stopped,
//! finished or marked on another device moves on Home without waiting for Bale's own return refresh.
//!
//! The server publishes `user_state.changed` after every stop that writes progress, history or watched state
//! (silo-server `165ab2b`, `playback_stopped: true`) and for watched, favorite, watchlist and Home dismissal changes.
//! Delivery is best effort, so a client also re-reads on every (re)subscribe (docs/realtime-api.md, "Playback stop
//! events"). The clients: silo-apple `0414ff74` (`HomeWatchStateRealtime.swift`), silo-roku `c1841ae`
//! (`HomeWatchState.bs`, `HomeEventsTask.bs`) and Android TV's `HomeRealtimeCoordinator.kt` / `EventsSocketV2Api.kt`.
//!
//! One attempt at a time, each under its own generation:
//! 1. Mint a ticket, `POST /api/v2/events/ws-ticket` as the profile. It is usable when its protocol is
//!    `silo.events.v2`, it expires in 1–30 s, the connection may last 1–300 s and the ticket is a header token
//!    (Android `validTicket`, Apple `Ticket.endpoint`).
//! 2. Open `GET /api/v2/events/ws?channels=user_state` offering `silo.events.v2` then `silo.ticket.<ticket>`, and
//!    require the server to pick `silo.events.v2`.
//! 3. A `hello` that still asks for a subscription gets one (Apple); a `subscribed` frame naming `user_state` makes
//!    the subscription live. Every subscription and every `user_state.changed` for the profile re-reads Home after
//!    Android's 2 s trailing debounce (`HomeRealtimeCoordinator.DEBOUNCE_MS`), which turns a burst (a batch of
//!    watched marks, a progress sync) into one read.
//!
//! How a connection ends (silo-server `f31842a`, docs/realtime-api.md "Close codes"): 4001 `access_changed` (or its
//! text frame) re-reads Home and reconnects at once with a new ticket; 4002 `credential_invalid` is final, with one
//! last re-read so the REST path meets the revoked session or the deleted profile; 4003 `lifetime_ended` reconnects at
//! once; 4004 `authority_unavailable` and every other end back off. A mint the server could not check answers 503
//! with Retry-After: back off, never sooner than Retry-After, capped at 60 s (the cap the server documents for the
//! control socket's mint). A mint refused outright (403) or not served (404, 405, 410, 501) is final for this
//! showing of Home.
//!
//! Backoff: 1 s doubling to 30 s, as all three clients. They reset it on every subscription; here it resets once a
//! subscription has held for 10 s, and only such a connection reconnects at once, so a server that closes each
//! connection as soon as it subscribes cannot hold Bale in a tight loop of mints and Home reads.
//!
//! While no subscription is live (connecting, backing off or stopped), Home re-reads every 120 s (Roku's
//! `HOME_WATCH_POLL_SECONDS`). Timeouts: the ticket 30 s, the open 15 s, the subscription 15 s after the open (Roku's
//! `HomeEventsTask`), and the connection its `max_connection_seconds` plus 5 s for the server's own 4003.
//!
//! Sized for the console (docs/resource-budget.md): one idle TCP connection with the server's 20 s pings, and at
//! most one Home read per debounce window. Home is hidden under Detail and the player, which ends the socket, so
//! nothing here runs beside playback.
//!
//! Bookkeeping only, in milliseconds of the host's monotonic clock: the host performs the actions in order and calls
//! again with a tick at `next_deadline`. Notes are fixed diagnostics with codes and durations, never ids or tickets.

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// The events socket's message protocol, offered first and the only one the server may pick.
pub const PROTOCOL: &str = "silo.events.v2";
/// The second offered protocol carries the ticket; the server never selects it.
const TICKET_PROTOCOL_PREFIX: &str = "silo.ticket.";
const CHANNEL: &str = "user_state";
const CHANGED: &str = "user_state.changed";
/// The socket below the server's base URL, with the channel declared up front (the server's `channels` query), so
/// a hello normally needs no subscribe frame.
pub const PATH: &str = "api/v2/events/ws?channels=user_state";
/// The subscribe frame for a hello whose `required_action` is not `none` (HomeWatchStateRealtime.swift).
pub const SUBSCRIBE: &str =
    r#"{"type":"subscribe","request_id":"bale-home","channels":["user_state"]}"#;
/// The server's tickets are 43 characters (32 random bytes, base64url); anything this long is not a ticket.
const MAX_TICKET_LEN: usize = 512;

/// The timings; [`Policy::DEFAULT`] in the app, tests shrink them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
pub struct Policy {
    pub backoff_base_ms: i64,
    pub backoff_max_ms: i64,
    pub retry_after_max_ms: i64,
    pub poll_ms: i64,
    pub debounce_ms: i64,
    pub mint_timeout_ms: i64,
    pub connect_timeout_ms: i64,
    pub subscribe_timeout_ms: i64,
    pub stable_ms: i64,
    pub lifetime_grace_ms: i64,
}

impl Policy {
    pub const DEFAULT: Policy = Policy {
        // Apple `retrySeconds`, Roku `m.watchBackoff`, Android `INITIAL_BACKOFF_MS` / `MAX_BACKOFF_MS`.
        backoff_base_ms: 1_000,
        backoff_max_ms: 30_000,
        // docs/realtime-api.md, the playback-control mint: "never sooner than the refusal's Retry-After (capped at
        // 60 seconds)".
        retry_after_max_ms: 60_000,
        // HomeWatchState.bs HOME_WATCH_POLL_SECONDS.
        poll_ms: 120_000,
        // HomeRealtimeCoordinator.kt DEBOUNCE_MS.
        debounce_ms: 2_000,
        // The ticket expires within 30 s anyway.
        mint_timeout_ms: 30_000,
        connect_timeout_ms: 15_000,
        // HomeEventsTask.bs: `if not subscribed and elapsed >= 15000 then exit while`.
        subscribe_timeout_ms: 15_000,
        stable_ms: 10_000,
        lifetime_grace_ms: 5_000,
    };
}

/// What the host does, in order.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(tag = "action", rename_all = "snake_case")]
pub enum Action {
    /// Start attempt `generation` (ending every older one): mint a ticket for the profile.
    Mint { generation: u32 },
    /// Open attempt `generation`'s socket at `path` below the server's base URL (`ws`/`wss` for `http`/`https`),
    /// offering `protocols` in this order, with no other credential.
    Connect {
        generation: u32,
        path: &'static str,
        protocols: Vec<String>,
    },
    /// Send this text frame on attempt `generation`'s socket.
    Send { generation: u32, text: &'static str },
    /// End attempt `generation`: cancel its request, close its socket.
    Close { generation: u32 },
    /// Re-read Home.
    Refresh,
}

/// A mint's answer: the fields of the server's `EventsSocketTicket`, any of which may be missing.
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
pub struct Ticket {
    pub ticket: Option<String>,
    pub expires_in: Option<i64>,
    pub max_connection_seconds: Option<i64>,
    pub protocol: Option<String>,
}

#[derive(Debug, Clone, PartialEq)]
enum Phase {
    /// Not started, or final: no further attempt. The poll still re-reads once started.
    Stopped,
    /// The next attempt mints at `until`.
    Waiting {
        until: i64,
    },
    /// The current attempt's ticket is being minted.
    Minting {
        since: i64,
    },
    /// Its socket is opening; `lifetime_ms` is the ticket's connection lifetime.
    Connecting {
        since: i64,
        lifetime_ms: i64,
    },
    Open(Connection),
}

#[derive(Debug, Clone, PartialEq)]
struct Connection {
    opened_at: i64,
    lifetime_ms: i64,
    subscribe_sent: bool,
    subscribed_at: Option<i64>,
    access_changed: bool,
}

/// One showing of Home's subscription.
#[derive(Debug)]
pub struct HomeEvents {
    policy: Policy,
    /// The profile Home shows; set once by [`HomeEvents::start`].
    profile_id: Option<String>,
    generation: u32,
    phase: Phase,
    /// The wait the next failure uses.
    backoff_ms: i64,
    poll_due: Option<i64>,
    refresh_due: Option<i64>,
    actions: Vec<Action>,
    notes: Vec<String>,
}

impl Default for HomeEvents {
    fn default() -> Self {
        Self::new()
    }
}

/// The frame fields this reads; everything else is ignored.
#[derive(Deserialize)]
struct Frame {
    #[serde(rename = "type")]
    kind: Option<String>,
    required_action: Option<String>,
    channels: Option<Vec<String>>,
    rejected: Option<Vec<Rejected>>,
    channel: Option<String>,
    event: Option<String>,
    data: Option<Value>,
}

#[derive(Deserialize)]
struct Rejected {
    channel: Option<String>,
    code: Option<String>,
}

/// What a frame asks for, decided before the state changes.
enum Reaction {
    None,
    Subscribe,
    Subscribed,
    Refused(String),
    Changed(String),
    AccessChanged,
}

impl HomeEvents {
    pub fn new() -> Self {
        HomeEvents {
            policy: Policy::DEFAULT,
            profile_id: None,
            generation: 0,
            phase: Phase::Stopped,
            backoff_ms: Policy::DEFAULT.backoff_base_ms,
            poll_due: None,
            refresh_due: None,
            actions: Vec::new(),
            notes: Vec::new(),
        }
    }

    /// Home is on screen for `profile_id`: the first ticket is minted now and the poll's clock starts. Once only.
    pub fn start(&mut self, now: i64, profile_id: &str, policy: Policy) {
        if self.profile_id.is_some() {
            return;
        }
        self.policy = policy;
        self.backoff_ms = policy.backoff_base_ms;
        self.profile_id = Some(profile_id.to_owned());
        self.poll_due = Some(now.saturating_add(policy.poll_ms));
        if profile_id.is_empty() {
            // user_state reaches only a profile-bound socket (docs/realtime-api.md, "Personal event recipients").
            self.note("stopped: no profile");
            return;
        }
        self.mint(now);
    }

    pub fn tick(&mut self, now: i64) {
        self.due(now);
    }

    pub fn mint_succeeded(&mut self, now: i64, generation: u32, ticket: &Ticket) {
        if self.current(generation) && matches!(self.phase, Phase::Minting { .. }) {
            match usable(ticket) {
                Some((value, lifetime_ms)) => {
                    self.phase = Phase::Connecting {
                        since: now,
                        lifetime_ms,
                    };
                    self.actions.push(Action::Connect {
                        generation,
                        path: PATH,
                        protocols: vec![
                            PROTOCOL.to_owned(),
                            format!("{TICKET_PROTOCOL_PREFIX}{value}"),
                        ],
                    });
                }
                None => {
                    self.note("ticket unusable");
                    self.retry_later(now, 0);
                }
            }
        }
        self.due(now);
    }

    /// The mint failed with HTTP `status` (None: no answer) and the answer's Retry-After.
    pub fn mint_failed(
        &mut self,
        now: i64,
        generation: u32,
        status: Option<u16>,
        retry_after_ms: Option<i64>,
    ) {
        if self.current(generation) && matches!(self.phase, Phase::Minting { .. }) {
            match status {
                // permission_denied or profile_verification_required: this credential cannot delegate a socket.
                Some(403) => self.stop("ticket refused (403)"),
                // A server without the route (older than the v2 events socket).
                Some(code @ (404 | 405 | 410 | 501)) => {
                    self.stop(&format!("no events socket ({code})"))
                }
                // 503 dependency_unavailable (the credential could not be checked), 429, 5xx, a 401 the session
                // refresh did not cure, or no answer at all.
                _ => {
                    self.note(&match status {
                        Some(code) => format!("ticket failed ({code})"),
                        None => "ticket failed (no answer)".to_owned(),
                    });
                    self.retry_later(now, retry_after_ms.unwrap_or(0));
                }
            }
        }
        self.due(now);
    }

    /// The socket opened and the server picked `protocol`.
    pub fn opened(&mut self, now: i64, generation: u32, protocol: Option<&str>) {
        if self.current(generation)
            && let Phase::Connecting { lifetime_ms, .. } = self.phase
        {
            if protocol == Some(PROTOCOL) {
                self.phase = Phase::Open(Connection {
                    opened_at: now,
                    lifetime_ms,
                    subscribe_sent: false,
                    subscribed_at: None,
                    access_changed: false,
                });
            } else {
                // HomeWatchStateRealtime.swift SocketDelegate: any other protocol is cancelled.
                self.note("socket picked another protocol");
                self.actions.push(Action::Close { generation });
                self.retry_later(now, 0);
            }
        }
        self.due(now);
    }

    /// The socket did not open: the upgrade was refused with HTTP `status`, or failed without an answer.
    pub fn connect_failed(&mut self, now: i64, generation: u32, status: Option<u16>) {
        if self.current(generation) && matches!(self.phase, Phase::Connecting { .. }) {
            self.note(&match status {
                Some(code) => format!("socket refused ({code})"),
                None => "socket failed".to_owned(),
            });
            self.retry_later(now, 0);
        }
        self.due(now);
    }

    /// A text frame arrived on attempt `generation`'s socket.
    pub fn frame(&mut self, now: i64, generation: u32, text: &str) {
        if self.current(generation) {
            let reaction = self.react(text);
            self.apply(now, generation, reaction);
        }
        self.due(now);
    }

    /// Attempt `generation`'s socket ended with close `code` (None: no close frame, or a failure).
    pub fn closed(&mut self, now: i64, generation: u32, code: Option<u16>) {
        if self.current(generation) {
            match &self.phase {
                Phase::Open(connection) => {
                    let connection = connection.clone();
                    self.ended(now, &connection, code);
                }
                Phase::Connecting { .. } => {
                    self.note(&format!(
                        "socket closed before opening ({})",
                        code_text(code)
                    ));
                    self.retry_later(now, 0);
                }
                _ => {}
            }
        }
        self.due(now);
    }

    /// A subscription is live: the poll waits.
    pub fn live(&self) -> bool {
        matches!(
            self.phase,
            Phase::Open(Connection {
                subscribed_at: Some(_),
                ..
            })
        )
    }

    /// No further attempt will be made while Home stays on screen.
    pub fn stopped(&self) -> bool {
        self.profile_id.is_some() && self.phase == Phase::Stopped
    }

    /// When to call again with a tick, if anything is waiting.
    pub fn next_deadline(&self) -> Option<i64> {
        let policy = &self.policy;
        let phase = match &self.phase {
            Phase::Stopped => None,
            Phase::Waiting { until } => Some(*until),
            Phase::Minting { since } => Some(since.saturating_add(policy.mint_timeout_ms)),
            Phase::Connecting { since, .. } => {
                Some(since.saturating_add(policy.connect_timeout_ms))
            }
            Phase::Open(connection) => {
                let lifetime = lifetime_end(connection, policy);
                Some(match connection.subscribed_at {
                    Some(_) => lifetime,
                    None => lifetime.min(
                        connection
                            .opened_at
                            .saturating_add(policy.subscribe_timeout_ms),
                    ),
                })
            }
        };
        [phase, self.poll_due, self.refresh_due]
            .into_iter()
            .flatten()
            .min()
    }

    pub fn actions(&self) -> &[Action] {
        &self.actions
    }

    pub fn notes(&self) -> &[String] {
        &self.notes
    }

    /// Clears what the last call left; the host has taken it.
    pub fn clear(&mut self) {
        self.actions.clear();
        self.notes.clear();
    }

    fn current(&self, generation: u32) -> bool {
        self.profile_id.is_some() && generation == self.generation
    }

    fn react(&self, text: &str) -> Reaction {
        let Phase::Open(connection) = &self.phase else {
            return Reaction::None;
        };
        // A frame that isn't JSON, or not an object, changes nothing (decodeHomeRealtimeFrame "never throws").
        let Ok(frame) = serde_json::from_str::<Frame>(text) else {
            return Reaction::None;
        };
        match frame.kind.as_deref() {
            Some("hello")
                if frame.required_action.as_deref() != Some("none")
                    && !connection.subscribe_sent =>
            {
                Reaction::Subscribe
            }
            Some("subscribed") => {
                if frame.channels.iter().flatten().any(|name| name == CHANNEL) {
                    if connection.subscribed_at.is_none() {
                        Reaction::Subscribed
                    } else {
                        Reaction::None
                    }
                } else if let Some(refusal) = frame
                    .rejected
                    .iter()
                    .flatten()
                    .find(|refusal| refusal.channel.as_deref() == Some(CHANNEL))
                {
                    // Every refusal code (unknown_channel, forbidden, profile_required) lasts as long as this
                    // credential: waiting for it is pointless.
                    Reaction::Refused(known(
                        refusal.code.as_deref(),
                        &["unknown_channel", "forbidden", "profile_required"],
                    ))
                } else {
                    // The answer to a declaration that is still owed a subscribe frame, or to another selection.
                    Reaction::None
                }
            }
            // HomeEventsTask.bs, HomeWatchStateRealtime.swift: only a subscribed socket's changes, for this profile.
            Some("event")
                if connection.subscribed_at.is_some()
                    && frame.channel.as_deref() == Some(CHANNEL)
                    && frame.event.as_deref() == Some(CHANGED) =>
            {
                let data = frame.data.as_ref();
                let profile = data
                    .and_then(|data| data.get("profile_id"))
                    .and_then(Value::as_str);
                if profile.is_none() || profile != self.profile_id.as_deref() {
                    return Reaction::None;
                }
                let change = known(
                    data.and_then(|data| data.get("change"))
                        .and_then(Value::as_str),
                    &[
                        "progress",
                        "watched",
                        "favorite",
                        "watchlist",
                        "history",
                        "home_dismissal",
                        "not_interested",
                        "download",
                    ],
                );
                let stopped = data
                    .and_then(|data| data.get("playback_stopped"))
                    .and_then(Value::as_bool)
                    == Some(true);
                Reaction::Changed(if stopped {
                    format!("{change}, playback stopped")
                } else {
                    change
                })
            }
            // docs/realtime-api.md "Access changes": the frame comes first, then the 4001 close.
            Some("access_changed") if !connection.access_changed => Reaction::AccessChanged,
            _ => Reaction::None,
        }
    }

    fn apply(&mut self, now: i64, generation: u32, reaction: Reaction) {
        let Phase::Open(connection) = &mut self.phase else {
            return;
        };
        match reaction {
            Reaction::None => {}
            Reaction::Subscribe => {
                connection.subscribe_sent = true;
                self.actions.push(Action::Send {
                    generation,
                    text: SUBSCRIBE,
                });
            }
            Reaction::Subscribed => {
                connection.subscribed_at = Some(now);
                self.note("subscribed");
                // The re-read covers whatever changed while no subscription was live.
                self.refresh(now);
            }
            Reaction::Refused(code) => {
                self.note(&format!("user_state refused ({code})"));
                self.actions.push(Action::Close { generation });
                self.stop("channel refused");
            }
            Reaction::Changed(change) => {
                self.note(&format!("watch state changed ({change})"));
                self.refresh(now);
            }
            Reaction::AccessChanged => {
                connection.access_changed = true;
                self.note("access changed");
                // "a client refetches the data the viewer's access decides (... home sections ...)".
                self.refresh(now);
            }
        }
    }

    /// The connection held its subscription for `stable_ms`: it may reconnect at once, and resets the backoff.
    fn stable(&self, connection: &Connection, now: i64) -> bool {
        connection
            .subscribed_at
            .is_some_and(|at| now.saturating_sub(at) >= self.policy.stable_ms)
    }

    fn ended(&mut self, now: i64, connection: &Connection, code: Option<u16>) {
        let stable = self.stable(connection, now);
        if stable {
            self.backoff_ms = self.policy.backoff_base_ms;
        }
        let text = code_text(code);
        match code {
            Some(4002) => {
                // "A failed lookup never closes with 4002, so a client can treat 4002 as final." One re-read lets
                // Home's REST read meet it: a revoked session ends at sign-in, a deleted profile at Who's watching.
                self.note(&format!("closed ({text})"));
                self.refresh(now);
                self.stop("credential invalid");
            }
            // "On either the frame or the close code, a client refetches ... and reconnects at once".
            _ if code == Some(4001) || connection.access_changed => {
                self.note(&format!("closed ({text}) after an access change"));
                self.refresh(now);
                self.reconnect(now, stable);
            }
            Some(4003) => {
                self.note(&format!("closed ({text})"));
                self.reconnect(now, stable);
            }
            _ => {
                self.note(&format!("closed ({text})"));
                self.retry_later(now, 0);
            }
        }
    }

    /// A new ticket at once after a connection that held its subscription, else after the backoff.
    fn reconnect(&mut self, now: i64, stable: bool) {
        if stable {
            self.mint(now);
        } else {
            self.retry_later(now, 0);
        }
    }

    fn due(&mut self, now: i64) {
        let policy = self.policy;
        match &self.phase {
            Phase::Waiting { until } if now >= *until => self.mint(now),
            Phase::Minting { since } if now >= since.saturating_add(policy.mint_timeout_ms) => {
                self.note("ticket timed out");
                self.actions.push(Action::Close {
                    generation: self.generation,
                });
                self.retry_later(now, 0);
            }
            Phase::Connecting { since, .. }
                if now >= since.saturating_add(policy.connect_timeout_ms) =>
            {
                self.note("socket open timed out");
                self.actions.push(Action::Close {
                    generation: self.generation,
                });
                self.retry_later(now, 0);
            }
            Phase::Open(connection) => {
                let connection = connection.clone();
                if connection.subscribed_at.is_none()
                    && now
                        >= connection
                            .opened_at
                            .saturating_add(policy.subscribe_timeout_ms)
                {
                    self.note("no subscription in time");
                    self.actions.push(Action::Close {
                        generation: self.generation,
                    });
                    self.retry_later(now, 0);
                } else if now >= lifetime_end(&connection, &policy) {
                    // The server's 4003 never came: end the connection as that close would have.
                    self.note("connection lifetime over");
                    self.actions.push(Action::Close {
                        generation: self.generation,
                    });
                    let stable = self.stable(&connection, now);
                    if stable {
                        self.backoff_ms = policy.backoff_base_ms;
                    }
                    self.reconnect(now, stable);
                }
            }
            _ => {}
        }
        if let Some(poll_due) = self.poll_due
            && now >= poll_due
        {
            if !self.live() {
                // HomeWatchState.bs pollHomeWatchState: only while the socket isn't live.
                self.note("poll");
                self.refresh(now);
            }
            let periods = now.saturating_sub(poll_due) / policy.poll_ms.max(1) + 1;
            self.poll_due = Some(poll_due.saturating_add(periods.saturating_mul(policy.poll_ms)));
        }
        if self.refresh_due.is_some_and(|due| now >= due) {
            self.refresh_due = None;
            self.actions.push(Action::Refresh);
        }
    }

    fn mint(&mut self, now: i64) {
        self.generation = self.generation.wrapping_add(1).max(1);
        self.phase = Phase::Minting { since: now };
        self.actions.push(Action::Mint {
            generation: self.generation,
        });
    }

    /// Waits out the backoff (at least `at_least_ms`, the server's Retry-After, within its cap), then doubles it.
    fn retry_later(&mut self, now: i64, at_least_ms: i64) {
        let wait = self
            .backoff_ms
            .max(at_least_ms.clamp(0, self.policy.retry_after_max_ms));
        self.backoff_ms = self
            .backoff_ms
            .saturating_mul(2)
            .min(self.policy.backoff_max_ms);
        self.phase = Phase::Waiting {
            until: now.saturating_add(wait),
        };
        self.note(&format!("retrying in {wait} ms"));
    }

    fn stop(&mut self, why: &str) {
        self.phase = Phase::Stopped;
        self.note(&format!("stopped: {why}"));
    }

    /// HomeRealtimeCoordinator.kt's trailing debounce: the read follows the last signal by `debounce_ms`.
    fn refresh(&mut self, now: i64) {
        self.refresh_due = Some(now.saturating_add(self.policy.debounce_ms));
    }

    fn note(&mut self, note: &str) {
        self.notes.push(note.to_owned());
    }
}

fn lifetime_end(connection: &Connection, policy: &Policy) -> i64 {
    connection
        .opened_at
        .saturating_add(connection.lifetime_ms)
        .saturating_add(policy.lifetime_grace_ms)
}

/// The ticket and its connection lifetime in milliseconds, when the server's answer can be used.
fn usable(ticket: &Ticket) -> Option<(&str, i64)> {
    let value = ticket.ticket.as_deref()?;
    let lifetime = ticket.max_connection_seconds?;
    let usable = ticket.protocol.as_deref() == Some(PROTOCOL)
        && (1..=30).contains(&ticket.expires_in?)
        && (1..=300).contains(&lifetime)
        && !value.is_empty()
        && value.len() <= MAX_TICKET_LEN
        && value.bytes().all(is_token_byte);
    usable.then_some((value, lifetime * 1_000))
}

/// RFC 9110 `tchar`: what a Sec-WebSocket-Protocol entry may hold (EventsSocketV2Api.kt validTicket).
fn is_token_byte(byte: u8) -> bool {
    byte.is_ascii_alphanumeric() || b"!#$%&'*+-.^_`|~".contains(&byte)
}

/// A server-chosen name for a note: only the ones this knows, never arbitrary text from the wire.
fn known(value: Option<&str>, names: &[&str]) -> String {
    match value {
        Some(value) if names.contains(&value) => value.to_owned(),
        Some(_) => "other".to_owned(),
        None => "unknown".to_owned(),
    }
}

fn code_text(code: Option<u16>) -> String {
    match code {
        Some(4001) => "4001 access_changed".to_owned(),
        Some(4002) => "4002 credential_invalid".to_owned(),
        Some(4003) => "4003 lifetime_ended".to_owned(),
        Some(4004) => "4004 authority_unavailable".to_owned(),
        Some(code) => code.to_string(),
        None => "no close frame".to_owned(),
    }
}

#[cfg(test)]
mod tests;

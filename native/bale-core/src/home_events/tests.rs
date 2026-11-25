use super::*;

const PROFILE: &str = "profile-1";
const TICKET: &str = "q8C1-xY_Zt0aBcDeFgHiJkLmNoPqRsTuVwXyZ012345";
const HELLO_DECLARED: &str = r#"{"type":"hello","schema_version":1,"connection_id":"01JC","available_channels":["catalog","history_import","user_state","user_settings","notifications"],"required_action":"none"}"#;
const HELLO_OWED: &str = r#"{"type":"hello","schema_version":1,"connection_id":"01JC","available_channels":["user_state"],"required_action":"subscribe"}"#;
const SUBSCRIBED: &str = r#"{"type":"subscribed","channels":["user_state"]}"#;
const SNAPSHOT: &str =
    r#"{"type":"snapshot","channel":"user_state","timestamp":"2026-11-19T23:42:54Z","data":null}"#;

fn changed(profile: &str) -> String {
    format!(
        r#"{{"type":"event","channel":"user_state","event":"user_state.changed","event_id":"01J","timestamp":"2026-11-19T23:42:54Z","data":{{"profile_id":"{profile}","content_id":"episode-1","series_id":"series-1","change":"progress","playback_stopped":true}}}}"#
    )
}

fn ticket() -> Ticket {
    Ticket {
        ticket: Some(TICKET.to_owned()),
        expires_in: Some(30),
        max_connection_seconds: Some(300),
        protocol: Some(PROTOCOL.to_owned()),
    }
}

fn take(machine: &mut HomeEvents) -> Vec<Action> {
    let actions = machine.actions().to_vec();
    machine.clear();
    actions
}

/// Started at `now`, its first mint taken.
fn started(now: i64) -> HomeEvents {
    let mut machine = HomeEvents::new();
    machine.start(now, PROFILE, Policy::DEFAULT);
    assert_eq!(take(&mut machine), [mint(1)]);
    machine
}

/// Attempt 1 subscribed at 0 through the declared channel, its re-read taken at 2 s.
fn live() -> HomeEvents {
    let mut machine = started(0);
    machine.mint_succeeded(0, 1, &ticket());
    machine.opened(0, 1, Some(PROTOCOL));
    machine.frame(0, 1, HELLO_DECLARED);
    machine.frame(0, 1, SUBSCRIBED);
    assert!(machine.live());
    machine.clear();
    machine.tick(2_000);
    assert_eq!(take(&mut machine), [Action::Refresh]);
    machine
}

/// The host: ticks at every deadline up to `to`, as its timer would, and returns what each tick asked for.
fn advance(machine: &mut HomeEvents, to: i64) -> Vec<(i64, Action)> {
    let mut done = Vec::new();
    for _ in 0..10_000 {
        match machine.next_deadline() {
            Some(due) if due <= to => {
                machine.tick(due);
                done.extend(take(machine).into_iter().map(|action| (due, action)));
            }
            _ => return done,
        }
    }
    panic!("the deadlines never moved past {to}");
}

fn mint(generation: u32) -> Action {
    Action::Mint { generation }
}

fn connect(generation: u32) -> Action {
    Action::Connect {
        generation,
        path: PATH,
        protocols: vec![PROTOCOL.to_owned(), format!("silo.ticket.{TICKET}")],
    }
}

/// The host for a server that answers every mint with 500 as soon as it is sent, from attempt 1 at 0 until `to`:
/// the waits between the attempts.
fn waits_between_failed_mints(machine: &mut HomeEvents, to: i64) -> Vec<i64> {
    let mut starts = vec![0];
    machine.mint_failed(0, 1, Some(500), None);
    machine.clear();
    while let Some(next) = machine.next_deadline().filter(|&next| next <= to) {
        machine.tick(next);
        for action in take(machine) {
            if let Action::Mint { generation } = action {
                starts.push(next);
                machine.mint_failed(next, generation, Some(500), None);
                machine.clear();
            }
        }
    }
    starts.windows(2).map(|pair| pair[1] - pair[0]).collect()
}

#[test]
fn start_mints_at_once_and_a_ticket_opens_the_socket_with_both_protocols_in_order() {
    let mut machine = HomeEvents::new();
    machine.start(5_000, PROFILE, Policy::DEFAULT);
    assert_eq!(take(&mut machine), [mint(1)]);
    assert!(!machine.live() && !machine.stopped());
    // The ticket's own timeout comes before the first poll.
    assert_eq!(machine.next_deadline(), Some(35_000));
    machine.mint_succeeded(5_100, 1, &ticket());
    assert_eq!(take(&mut machine), [connect(1)]);
    assert_eq!(PATH, "api/v2/events/ws?channels=user_state");
    // Starting again changes nothing.
    machine.start(6_000, "profile-2", Policy::DEFAULT);
    assert!(take(&mut machine).is_empty());
}

#[test]
fn a_declared_subscription_rereads_home_after_the_debounce() {
    let mut machine = started(0);
    machine.mint_succeeded(0, 1, &ticket());
    machine.opened(100, 1, Some(PROTOCOL));
    machine.frame(110, 1, HELLO_DECLARED);
    // The URL declared the channel: no subscribe frame.
    machine.clear();
    machine.frame(120, 1, SUBSCRIBED);
    machine.frame(130, 1, SNAPSHOT);
    assert!(machine.live());
    assert!(take(&mut machine).is_empty());
    assert_eq!(machine.next_deadline(), Some(2_120));
    assert!(advance(&mut machine, 2_119).is_empty());
    assert_eq!(advance(&mut machine, 2_120), [(2_120, Action::Refresh)]);
    // A second subscribed frame for the same connection is no new subscription.
    machine.frame(3_000, 1, SUBSCRIBED);
    assert!(advance(&mut machine, 60_000).is_empty());
}

#[test]
fn a_hello_that_asks_for_a_subscription_gets_one_frame() {
    let mut machine = started(0);
    machine.mint_succeeded(0, 1, &ticket());
    machine.opened(0, 1, Some(PROTOCOL));
    machine.clear();
    machine.frame(10, 1, HELLO_OWED);
    assert_eq!(
        take(&mut machine),
        [Action::Send {
            generation: 1,
            text: SUBSCRIBE
        }]
    );
    machine.frame(20, 1, HELLO_OWED);
    assert!(take(&mut machine).is_empty());
    // The declaration's own answer, empty, is not the one owed: wait for it.
    machine.frame(30, 1, r#"{"type":"subscribed","channels":[]}"#);
    assert!(!machine.live() && !machine.stopped());
    machine.frame(
        40,
        1,
        r#"{"type":"subscribed","request_id":"bale-home","channels":["user_state"]}"#,
    );
    assert!(machine.live());
    let frame: serde_json::Value = serde_json::from_str(SUBSCRIBE).unwrap();
    assert_eq!(frame["type"], "subscribe");
    assert_eq!(frame["channels"], serde_json::json!(["user_state"]));
}

#[test]
fn changes_for_the_profile_reread_home_once_per_burst() {
    let mut machine = live();
    machine.frame(10_000, 1, &changed(PROFILE));
    assert!(take(&mut machine).is_empty());
    // Android's trailing debounce: each change pushes the read back.
    machine.frame(11_500, 1, &changed(PROFILE));
    machine.frame(11_600, 1, &changed(PROFILE));
    assert!(advance(&mut machine, 13_599).is_empty());
    assert_eq!(advance(&mut machine, 20_000), [(13_600, Action::Refresh)]);
    let mut minimal = live();
    minimal.frame(
        10_000,
        1,
        r#"{"type":"event","channel":"user_state","event":"user_state.changed","data":{"profile_id":"profile-1","change":"watched"}}"#,
    );
    assert_eq!(advance(&mut minimal, 12_000), [(12_000, Action::Refresh)]);
}

#[test]
fn changes_for_another_profile_other_events_and_garbage_change_nothing() {
    let mut machine = live();
    for frame in [
        changed("profile-2"),
        changed(""),
        r#"{"type":"event","channel":"user_state","event":"user_state.changed","data":null}"#.to_owned(),
        r#"{"type":"event","channel":"user_state","event":"user_state.changed"}"#.to_owned(),
        r#"{"type":"event","channel":"user_state","event":"user_state.changed","data":{"profile_id":1}}"#.to_owned(),
        r#"{"type":"event","channel":"catalog","event":"catalog.item.changed","data":{"profile_id":"profile-1"}}"#.to_owned(),
        r#"{"type":"event","channel":"user_state","event":"download","data":{"profile_id":"profile-1"}}"#.to_owned(),
        r#"{"type":"error","code":"internal_error","message":"Failed to load snapshot"}"#.to_owned(),
        SNAPSHOT.to_owned(),
        "not json".to_owned(),
        "[]".to_owned(),
        "null".to_owned(),
        r#"{"type":7}"#.to_owned(),
        String::new(),
    ] {
        machine.frame(10_000, 1, &frame);
    }
    assert!(take(&mut machine).is_empty());
    assert!(machine.live());
    assert!(advance(&mut machine, 100_000).is_empty());
}

#[test]
fn a_change_before_the_subscription_is_not_read() {
    let mut machine = started(0);
    machine.mint_succeeded(0, 1, &ticket());
    machine.opened(0, 1, Some(PROTOCOL));
    machine.frame(10, 1, &changed(PROFILE));
    machine.clear();
    assert!(!machine.live());
    // Only the subscription timeout is waiting.
    assert_eq!(machine.next_deadline(), Some(15_000));
}

#[test]
fn failures_back_off_from_one_second_doubling_to_thirty() {
    let mut machine = started(0);
    assert_eq!(
        waits_between_failed_mints(&mut machine, 200_000)[..8],
        [1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000, 30_000]
    );
}

#[test]
fn retry_after_is_a_floor_capped_at_a_minute() {
    let mut machine = started(0);
    machine.mint_failed(0, 1, Some(503), Some(5_000));
    assert_eq!(machine.next_deadline(), Some(5_000));
    assert_eq!(advance(&mut machine, 5_000), [(5_000, mint(2))]);
    // The backoff went on doubling underneath: 2 s, which a shorter Retry-After doesn't shorten.
    machine.mint_failed(5_000, 2, Some(429), Some(500));
    assert_eq!(machine.next_deadline(), Some(7_000));
    assert_eq!(advance(&mut machine, 7_000), [(7_000, mint(3))]);
    machine.mint_failed(7_000, 3, Some(503), Some(3_600_000));
    assert_eq!(machine.next_deadline(), Some(67_000));
    machine.clear();
    // A server without the events service answers 503 with Retry-After 30 every time.
    let mut unconfigured = started(0);
    unconfigured.mint_failed(0, 1, Some(503), Some(30_000));
    assert_eq!(unconfigured.next_deadline(), Some(30_000));
}

#[test]
fn other_mint_failures_back_off() {
    for status in [
        None,
        Some(401),
        Some(400),
        Some(408),
        Some(422),
        Some(500),
        Some(502),
    ] {
        let mut machine = started(0);
        machine.mint_failed(0, 1, status, None);
        assert!(!machine.stopped(), "{status:?}");
        assert_eq!(
            advance(&mut machine, 1_000),
            [(1_000, mint(2))],
            "{status:?}"
        );
    }
}

#[test]
fn a_refused_or_missing_ticket_route_is_final_and_the_poll_takes_over() {
    for status in [403, 404, 405, 410, 501] {
        let mut machine = started(0);
        machine.mint_failed(0, 1, Some(status), Some(1_000));
        assert!(machine.stopped(), "{status}");
        assert!(!machine.live());
        machine.clear();
        let done = advance(&mut machine, 500_000);
        assert!(
            !done
                .iter()
                .any(|(_, action)| matches!(action, Action::Mint { .. }))
        );
        // HomeWatchState.bs pollHomeWatchState: every 120 s, read 2 s later by the debounce.
        assert_eq!(
            done,
            [
                (122_000, Action::Refresh),
                (242_000, Action::Refresh),
                (362_000, Action::Refresh),
                (482_000, Action::Refresh)
            ],
            "{status}"
        );
    }
}

#[test]
fn the_poll_waits_while_a_subscription_is_live() {
    let mut machine = live();
    assert!(advance(&mut machine, 299_000).is_empty());
    // A server that never answers a mint: no subscription, so the first poll reads.
    let mut down = started(0);
    let done = advance(&mut down, 122_000);
    assert!(done.contains(&(122_000, Action::Refresh)));
    assert_eq!(
        done.iter()
            .filter(|(_, action)| *action == Action::Refresh)
            .count(),
        1
    );
}

#[test]
fn close_4001_rereads_home_and_reconnects_at_once() {
    let mut machine = live();
    assert!(advance(&mut machine, 60_000).is_empty());
    machine.closed(60_000, 1, Some(4001));
    assert_eq!(take(&mut machine), [mint(2)]);
    assert!(!machine.live());
    assert_eq!(advance(&mut machine, 62_000), [(62_000, Action::Refresh)]);
    machine.mint_succeeded(62_100, 2, &ticket());
    assert_eq!(take(&mut machine), [connect(2)]);
}

#[test]
fn the_access_changed_frame_counts_when_its_close_code_is_lost() {
    let mut machine = live();
    machine.frame(30_000, 1, r#"{"type":"access_changed"}"#);
    assert!(take(&mut machine).is_empty());
    machine.closed(30_100, 1, None);
    assert_eq!(take(&mut machine), [mint(2)]);
    // The frame's re-read and the close's merge into one.
    assert_eq!(advance(&mut machine, 40_000), [(32_100, Action::Refresh)]);
}

#[test]
fn close_4002_is_final_with_one_last_reread() {
    let mut machine = live();
    machine.closed(40_000, 1, Some(4002));
    assert!(take(&mut machine).is_empty());
    assert!(machine.stopped());
    let done = advance(&mut machine, 400_000);
    assert!(
        !done
            .iter()
            .any(|(_, action)| matches!(action, Action::Mint { .. }))
    );
    assert_eq!(done[0], (42_000, Action::Refresh));
    // Then only the poll.
    assert_eq!(
        &done[1..],
        [
            (122_000, Action::Refresh),
            (242_000, Action::Refresh),
            (362_000, Action::Refresh)
        ]
    );
}

#[test]
fn close_4003_reconnects_at_once_and_the_new_subscription_rereads() {
    let mut machine = live();
    assert!(advance(&mut machine, 300_000).is_empty());
    machine.closed(300_000, 1, Some(4003));
    assert_eq!(take(&mut machine), [mint(2)]);
    machine.mint_succeeded(300_050, 2, &ticket());
    machine.opened(300_100, 2, Some(PROTOCOL));
    machine.frame(300_110, 2, HELLO_DECLARED);
    machine.frame(300_120, 2, SUBSCRIBED);
    assert_eq!(take(&mut machine), [connect(2)]);
    assert_eq!(advance(&mut machine, 302_120), [(302_120, Action::Refresh)]);
}

#[test]
fn close_4004_and_dropped_connections_back_off_from_one_second() {
    for code in [
        Some(4004),
        None,
        Some(1000),
        Some(1001),
        Some(1006),
        Some(1008),
        Some(1011),
    ] {
        let mut machine = live();
        // An earlier failure doubled the backoff; a subscription that held resets it.
        machine.backoff_ms = 8_000;
        assert!(advance(&mut machine, 60_000).is_empty());
        machine.closed(60_000, 1, code);
        assert!(take(&mut machine).is_empty(), "{code:?}");
        assert!(!machine.stopped());
        assert_eq!(
            advance(&mut machine, 61_000),
            [(61_000, mint(2))],
            "{code:?}"
        );
    }
}

#[test]
fn a_connection_that_drops_as_it_subscribes_never_reconnects_at_once() {
    let mut machine = started(0);
    let mut now = 0;
    let mut waits = Vec::new();
    for generation in 1..=7 {
        machine.mint_succeeded(now, generation, &ticket());
        machine.opened(now, generation, Some(PROTOCOL));
        machine.frame(now, generation, SUBSCRIBED);
        machine.clear();
        // A server that ends every connection 2 s after it subscribes, with a code that asks for at once.
        now += 2_000;
        machine.closed(
            now,
            generation,
            Some(if generation % 2 == 0 { 4001 } else { 4003 }),
        );
        assert!(
            !take(&mut machine).contains(&mint(generation + 1)),
            "{generation}"
        );
        let start = loop {
            let next = machine.next_deadline().unwrap();
            machine.tick(next);
            if take(&mut machine).contains(&mint(generation + 1)) {
                break next;
            }
        };
        waits.push(start - now);
        now = start;
    }
    assert_eq!(waits, [1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000]);
}

#[test]
fn the_subscription_open_and_ticket_each_time_out() {
    let mut machine = started(0);
    assert!(advance(&mut machine, 29_999).is_empty());
    assert_eq!(
        advance(&mut machine, 30_000),
        [(30_000, Action::Close { generation: 1 })]
    );
    assert_eq!(advance(&mut machine, 31_000), [(31_000, mint(2))]);
    // A late answer for the abandoned attempt is ignored.
    machine.mint_succeeded(31_100, 1, &ticket());
    assert!(take(&mut machine).is_empty());

    machine.mint_succeeded(31_200, 2, &ticket());
    machine.clear();
    assert_eq!(
        advance(&mut machine, 46_200),
        [(46_200, Action::Close { generation: 2 })]
    );
    assert_eq!(advance(&mut machine, 48_200), [(48_200, mint(3))]);

    machine.mint_succeeded(48_200, 3, &ticket());
    machine.opened(48_300, 3, Some(PROTOCOL));
    machine.clear();
    // HomeEventsTask.bs: not subscribed 15 s after the open.
    assert_eq!(
        advance(&mut machine, 63_300),
        [(63_300, Action::Close { generation: 3 })]
    );
    assert_eq!(advance(&mut machine, 67_300), [(67_300, mint(4))]);
}

#[test]
fn a_connection_past_its_lifetime_is_closed_and_replaced_at_once() {
    let mut machine = live();
    // The ticket allowed 300 s; the server's 4003 gets 5 s more before Bale ends it.
    assert!(advance(&mut machine, 304_999).is_empty());
    assert_eq!(
        advance(&mut machine, 305_000),
        [
            (305_000, Action::Close { generation: 1 }),
            (305_000, mint(2))
        ]
    );
    let mut short = started(0);
    short.mint_succeeded(
        0,
        1,
        &Ticket {
            max_connection_seconds: Some(60),
            ..ticket()
        },
    );
    short.opened(0, 1, Some(PROTOCOL));
    short.frame(0, 1, SUBSCRIBED);
    short.clear();
    let done = advance(&mut short, 65_000);
    assert_eq!(
        done[done.len() - 2..],
        [(65_000, Action::Close { generation: 1 }), (65_000, mint(2))]
    );
}

#[test]
fn an_unusable_ticket_backs_off_without_connecting() {
    let long = "a".repeat(MAX_TICKET_LEN + 1);
    for bad in [
        Ticket {
            protocol: Some("silo.room.v2".to_owned()),
            ..ticket()
        },
        Ticket {
            protocol: None,
            ..ticket()
        },
        Ticket {
            expires_in: Some(0),
            ..ticket()
        },
        Ticket {
            expires_in: Some(31),
            ..ticket()
        },
        Ticket {
            expires_in: None,
            ..ticket()
        },
        Ticket {
            max_connection_seconds: Some(0),
            ..ticket()
        },
        Ticket {
            max_connection_seconds: Some(14_400),
            ..ticket()
        },
        Ticket {
            ticket: Some(String::new()),
            ..ticket()
        },
        Ticket {
            ticket: Some("a b".to_owned()),
            ..ticket()
        },
        Ticket {
            ticket: Some("a,b".to_owned()),
            ..ticket()
        },
        Ticket {
            ticket: Some("é".to_owned()),
            ..ticket()
        },
        Ticket {
            ticket: Some(long.clone()),
            ..ticket()
        },
        Ticket::default(),
    ] {
        let mut machine = started(0);
        machine.clear();
        machine.mint_succeeded(10, 1, &bad);
        assert!(take(&mut machine).is_empty(), "{bad:?}");
        assert_eq!(advance(&mut machine, 1_010), [(1_010, mint(2))], "{bad:?}");
    }
    let mut machine = started(0);
    machine.clear();
    machine.mint_succeeded(
        0,
        1,
        &Ticket {
            ticket: Some("Ab9!#$%&'*+-.^_`|~".to_owned()),
            expires_in: Some(1),
            max_connection_seconds: Some(1),
            ..ticket()
        },
    );
    assert!(matches!(take(&mut machine)[..], [Action::Connect { .. }]));
}

#[test]
fn the_server_must_pick_the_events_protocol() {
    for protocol in [None, Some("silo.ticket.x"), Some("silo.room.v2")] {
        let mut machine = started(0);
        machine.mint_succeeded(0, 1, &ticket());
        machine.clear();
        machine.opened(10, 1, protocol);
        assert_eq!(take(&mut machine), [Action::Close { generation: 1 }]);
        assert_eq!(advance(&mut machine, 1_010), [(1_010, mint(2))]);
    }
}

#[test]
fn a_refused_channel_is_final() {
    let mut machine = started(0);
    machine.mint_succeeded(0, 1, &ticket());
    machine.opened(0, 1, Some(PROTOCOL));
    machine.frame(0, 1, HELLO_OWED);
    machine.clear();
    machine.frame(
        10,
        1,
        r#"{"type":"subscribed","request_id":"bale-home","channels":[],"rejected":[{"channel":"user_state","code":"forbidden","message":"This channel is not available to this connection"}]}"#,
    );
    assert_eq!(take(&mut machine), [Action::Close { generation: 1 }]);
    assert!(machine.stopped());
    assert!(
        !advance(&mut machine, 300_000)
            .iter()
            .any(|(_, action)| matches!(action, Action::Mint { .. }))
    );
}

#[test]
fn an_upgrade_that_fails_backs_off() {
    for status in [None, Some(400), Some(401), Some(403), Some(404), Some(503)] {
        let mut machine = started(0);
        machine.mint_succeeded(0, 1, &ticket());
        machine.clear();
        machine.connect_failed(10, 1, status);
        assert!(!machine.stopped(), "{status:?}");
        assert_eq!(
            advance(&mut machine, 1_010),
            [(1_010, mint(2))],
            "{status:?}"
        );
    }
    let mut machine = started(0);
    machine.mint_succeeded(0, 1, &ticket());
    machine.clear();
    machine.closed(10, 1, Some(1006));
    assert_eq!(advance(&mut machine, 1_010), [(1_010, mint(2))]);
}

#[test]
fn answers_for_an_older_attempt_are_ignored() {
    let mut machine = live();
    machine.closed(60_000, 1, Some(4003));
    assert_eq!(take(&mut machine), [mint(2)]);
    machine.mint_succeeded(60_010, 1, &ticket());
    machine.mint_failed(60_010, 1, Some(403), None);
    machine.opened(60_020, 1, Some(PROTOCOL));
    machine.connect_failed(60_020, 1, None);
    machine.frame(60_030, 1, &changed(PROFILE));
    machine.frame(60_030, 1, r#"{"type":"access_changed"}"#);
    machine.closed(60_040, 1, Some(4002));
    assert!(take(&mut machine).is_empty());
    assert!(!machine.stopped());
    // Out of order for the current one too: an open before its ticket, a frame before its open.
    machine.opened(60_050, 2, Some(PROTOCOL));
    machine.frame(60_050, 2, SUBSCRIBED);
    assert!(take(&mut machine).is_empty());
    assert!(!machine.live());
    machine.mint_succeeded(60_060, 2, &ticket());
    assert_eq!(take(&mut machine), [connect(2)]);
}

#[test]
fn nothing_runs_before_start_or_without_a_profile() {
    let mut machine = HomeEvents::new();
    machine.tick(1_000_000);
    machine.frame(0, 0, SUBSCRIBED);
    machine.closed(0, 0, Some(4001));
    machine.mint_failed(0, 0, None, None);
    assert!(take(&mut machine).is_empty());
    assert_eq!(machine.next_deadline(), None);
    assert!(!machine.stopped() && !machine.live());

    let mut empty = HomeEvents::new();
    empty.start(0, "", Policy::DEFAULT);
    assert!(take(&mut empty).is_empty());
    assert!(empty.stopped());
}

#[test]
fn notes_name_codes_and_waits_never_ids_or_tickets() {
    let mut machine = started(0);
    let mut notes = Vec::new();
    let mut record = |machine: &mut HomeEvents| {
        notes.extend(machine.notes().iter().cloned());
        machine.clear();
    };
    machine.mint_failed(0, 1, Some(503), Some(5_000));
    record(&mut machine);
    machine.tick(5_000);
    machine.mint_succeeded(5_000, 2, &ticket());
    machine.opened(5_000, 2, Some(PROTOCOL));
    machine.frame(5_000, 2, SUBSCRIBED);
    machine.frame(20_000, 2, &changed(PROFILE));
    machine.frame(
        21_000,
        2,
        r#"{"type":"event","channel":"user_state","event":"user_state.changed","data":{"profile_id":"profile-1","change":"Secret Title"}}"#,
    );
    machine.closed(40_000, 2, Some(4003));
    record(&mut machine);
    assert_eq!(
        notes,
        [
            "ticket failed (503)",
            "retrying in 5000 ms",
            "subscribed",
            "watch state changed (progress, playback stopped)",
            "watch state changed (other)",
            "closed (4003 lifetime_ended)",
        ]
    );
    for note in &notes {
        assert!(
            !note.contains(TICKET)
                && !note.contains(PROFILE)
                && !note.contains("episode")
                && !note.contains("Secret")
        );
    }
}

#[test]
fn the_default_policy_matches_the_clients() {
    let policy = Policy::DEFAULT;
    assert_eq!(
        (policy.backoff_base_ms, policy.backoff_max_ms),
        (1_000, 30_000)
    );
    assert_eq!(policy.poll_ms, 120_000);
    assert_eq!(policy.debounce_ms, 2_000);
    assert_eq!(policy.subscribe_timeout_ms, 15_000);
    assert_eq!(policy.retry_after_max_ms, 60_000);
}

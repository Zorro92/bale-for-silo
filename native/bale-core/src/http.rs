//! The shared HTTP client for Rust's media transport: one ureq agent (connection pool) for the
//! app's lifetime, with a caching resolver. Used by the range stream and the debug benchmarks.

use std::time::{Duration, Instant};

/// The app-wide agent: warm connections are reused across streams, as the C# HttpClient's are.
pub fn agent() -> &'static ureq::Agent {
    static AGENT: std::sync::OnceLock<ureq::Agent> = std::sync::OnceLock::new();
    AGENT.get_or_init(|| {
        let config = ureq::Agent::config_builder()
            .timeout_connect(Some(Duration::from_secs(10)))
            .timeout_global(Some(Duration::from_secs(30)))
            .http_status_as_error(false)
            .max_idle_connections_per_host(8)
            .build();
        ureq::Agent::with_parts(
            config,
            ureq::unversioned::transport::DefaultConnector::new(),
            CachingResolver::default(),
        )
    })
}

/// ureq resolves the host before it looks in its connection pool, so every request paid a DNS
/// lookup (on a spawned thread, with a timeout set) even on a warm connection; the C# handler takes a
/// pooled connection without one. Resolve each host once a minute instead.
#[derive(Debug, Default)]
struct CachingResolver {
    inner: ureq::unversioned::resolver::DefaultResolver,
    cache:
        std::sync::Mutex<std::collections::HashMap<String, (Instant, Vec<std::net::SocketAddr>)>>,
}

impl ureq::unversioned::resolver::Resolver for CachingResolver {
    fn resolve(
        &self,
        uri: &ureq::http::Uri,
        config: &ureq::config::Config,
        timeout: ureq::unversioned::transport::NextTimeout,
    ) -> Result<ureq::unversioned::resolver::ResolvedSocketAddrs, ureq::Error> {
        const FRESH: Duration = Duration::from_secs(60);
        let key = format!(
            "{}://{}",
            uri.scheme_str().unwrap_or_default(),
            uri.authority().map_or("", |a| a.as_str())
        );
        let cache = self
            .cache
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Some((at, addresses)) = cache.get(&key)
            && at.elapsed() < FRESH
        {
            let mut resolved = self.empty();
            for address in addresses {
                resolved.push(*address);
            }
            return Ok(resolved);
        }
        drop(cache);
        let resolved = self.inner.resolve(uri, config, timeout)?;
        let addresses = resolved.iter().copied().collect();
        self.cache
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .insert(key, (Instant::now(), addresses));
        Ok(resolved)
    }
}

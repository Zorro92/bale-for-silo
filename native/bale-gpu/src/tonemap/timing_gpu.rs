//! D3D11 query sets owned by the stage, never by its input/output textures.
//! GetData is attempted once per old slot per apply, with DONOTFLUSH and raw S_FALSE handling.
use super::{
    timing::{self, accounting::*},
    types::BaleToneMapTimingStats,
};
use windows::{
    Win32::{
        Foundation::{E_FAIL, S_FALSE, S_OK},
        Graphics::Direct3D11::*,
    },
    core::{Interface, Result},
};

struct Queries {
    disjoint: ID3D11Query,
    stamps: [ID3D11Query; 6],
    copied: bool,
}
impl Queries {
    fn create(device: &ID3D11Device) -> Result<Self> {
        let query = |kind| -> Result<ID3D11Query> {
            let mut result = None;
            // SAFETY: valid descriptor and local COM output; live same-device owner.
            unsafe {
                device.CreateQuery(
                    &D3D11_QUERY_DESC {
                        Query: kind,
                        MiscFlags: 0,
                    },
                    Some(&mut result),
                )?;
            }
            result.ok_or_else(|| E_FAIL.into())
        };
        let stamps = (0..6)
            .map(|_| query(D3D11_QUERY_TIMESTAMP))
            .collect::<Result<Vec<_>>>()?;
        Ok(Self {
            disjoint: query(D3D11_QUERY_TIMESTAMP_DISJOINT)?,
            stamps: stamps.try_into().map_err(|_| E_FAIL)?,
            copied: false,
        })
    }
    fn read(&self, context: &ID3D11DeviceContext) -> Result<Option<Sample>> {
        let mut disjoint = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
        if !read(context, &self.disjoint, &mut disjoint)? {
            return Ok(None);
        }
        if disjoint.Disjoint.as_bool() {
            return Ok(Some(Sample::Disjoint));
        }
        let mut ticks = [0u64; 6];
        for (query, tick) in self.stamps.iter().zip(&mut ticks) {
            if !read(context, query, tick)? {
                return Ok(None);
            }
        }
        Ok(Some(sample(ticks, disjoint.Frequency, false, self.copied)))
    }
}

// Only called with a query and data of matching kind and size, under caller multithread protection.
fn read<T>(context: &ID3D11DeviceContext, query: &ID3D11Query, value: &mut T) -> Result<bool> {
    // SAFETY: query/data compatibility is established at the two call sites above. The typed
    // windows-rs method folds S_FALSE into Ok(()), which would turn unready data into bogus zeroes.
    let hr = unsafe {
        (context.vtable().GetData)(
            context.as_raw(),
            query.as_raw(),
            std::ptr::from_mut(value).cast(),
            size_of::<T>() as u32,
            D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
        )
    };
    match hr {
        S_OK => Ok(true),
        S_FALSE => Ok(false),
        _ => Err(hr.into()),
    }
}

pub(super) struct Timing {
    queries: Vec<Queries>,
    ring: Ring,
    window: Window,
    stats: BaleToneMapTimingStats,
}
impl Timing {
    /// Called after the create self-test: it must never become part of the playback measurement.
    pub fn create(device: &ID3D11Device) -> Option<Self> {
        let mut stats = timing::start()?;
        match (0..RING_SIZE)
            .map(|_| Queries::create(device))
            .collect::<Result<Vec<_>>>()
        {
            Ok(queries) => Some(Self {
                queries,
                ring: Ring::default(),
                window: Window::default(),
                stats,
            }),
            Err(error) => {
                stats.state = 2;
                stats.error = error.code().0;
                timing::publish(stats);
                None // Measurement failure must never decline playback.
            }
        }
    }
    fn record(&mut self, sample: Sample) {
        if self.window.record(sample) {
            self.window.finish(&mut self.stats);
            self.stats.pending = self.ring.pending();
            timing::publish(self.stats);
        }
    }
    pub fn begin(&mut self, context: &ID3D11DeviceContext, copied: bool) -> Option<usize> {
        if self.stats.error != 0 {
            return None;
        }
        self.ring.advance();
        for slot in 0..RING_SIZE {
            if !self.ring.ready_to_poll(slot) {
                continue;
            }
            match self.queries[slot].read(context) {
                Ok(None) => {} // Still pending: keep its query set, never overwrite/recycle it.
                Ok(Some(sample)) => {
                    self.ring.release(slot);
                    self.record(sample);
                }
                Err(error) => {
                    self.stats.error = error.code().0;
                    self.stats.state = 2;
                    self.retire_pending();
                    self.window.finish(&mut self.stats);
                    timing::publish(self.stats);
                    return None;
                }
            }
        }
        let Some(slot) = self.ring.reserve() else {
            self.record(Sample::Unavailable); // Ring full: skip measurement, queue playback normally.
            return None;
        };
        self.queries[slot].copied = copied;
        // SAFETY: same-device idle query set, caller holds the immediate-context multithread lock.
        unsafe {
            context.Begin(&self.queries[slot].disjoint);
        }
        self.mark(context, slot, 0);
        Some(slot)
    }
    pub fn mark(&self, context: &ID3D11DeviceContext, slot: usize, boundary: usize) {
        // SAFETY: timestamp End (never Begin); queries are only reused after successful readback.
        unsafe {
            context.End(&self.queries[slot].stamps[boundary]);
        }
    }
    pub fn end(&self, context: &ID3D11DeviceContext, slot: usize) {
        self.mark(context, slot, 5);
        // SAFETY: closes this frame's disjoint interval after every timestamp was ended.
        unsafe {
            context.End(&self.queries[slot].disjoint);
        }
    }
    fn retire_pending(&mut self) {
        // No read/flush/wait at teardown or query failure; unresolved samples are unavailable.
        for _ in 0..self.ring.retire() {
            self.record(Sample::Unavailable);
        }
        self.stats.pending = 0;
    }
}
impl Drop for Timing {
    fn drop(&mut self) {
        self.retire_pending();
        self.window.finish(&mut self.stats);
        if self.stats.error == 0 {
            self.stats.state = 3;
        }
        timing::publish(self.stats);
    }
}

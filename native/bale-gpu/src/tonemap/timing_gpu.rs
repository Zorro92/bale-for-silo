//! One unresolved D3D11 disjoint interval at a time, owned by the stage, never its textures.
//! All query calls stay inside the private state/multithread guard. No flush, wait or retry loop.
use super::timing::{self, accounting::*};
use windows::{
    Win32::{
        Foundation::{E_FAIL, E_INVALIDARG},
        Graphics::Direct3D11::*,
    },
    core::Interface,
};

type TimingResult<T> = std::result::Result<T, Failure>;

fn failure(reason: FailureReason, error: windows::core::Error) -> Failure {
    Failure::new(reason, error.code().0)
}
fn sequence_failure() -> Failure {
    Failure::new(FailureReason::Sequence, E_FAIL.0)
}

struct Queries {
    disjoint: ID3D11Query,
    stamps: [ID3D11Query; 6],
    copied: bool,
    begun: bool,
    next_boundary: usize,
}
impl Queries {
    fn create(device: &ID3D11Device) -> TimingResult<Self> {
        let query = |kind, size| -> TimingResult<ID3D11Query> {
            let mut result = None;
            // SAFETY: initialized descriptor and local COM output; live same-device owner.
            unsafe {
                device.CreateQuery(
                    &D3D11_QUERY_DESC {
                        Query: kind,
                        MiscFlags: 0,
                    },
                    Some(&mut result),
                )
            }
            .map_err(|e| failure(FailureReason::Create, e))?;
            let query = result.ok_or(Failure::new(FailureReason::Create, E_FAIL.0))?;
            // SAFETY: live query and local descriptor; verify what the driver actually created.
            let desc = unsafe { query.GetDesc() };
            if desc.Query != kind || desc.MiscFlags != 0 {
                return Err(Failure::new(FailureReason::Create, E_INVALIDARG.0));
            }
            // In particular, never assume that a console disjoint record has desktop's size.
            data_size(unsafe { query.GetDataSize() }, size)?;
            Ok(query)
        };
        Ok(Self {
            disjoint: query(
                D3D11_QUERY_TIMESTAMP_DISJOINT,
                size_of::<D3D11_QUERY_DATA_TIMESTAMP_DISJOINT>(),
            )?,
            stamps: [
                query(D3D11_QUERY_TIMESTAMP, size_of::<u64>())?,
                query(D3D11_QUERY_TIMESTAMP, size_of::<u64>())?,
                query(D3D11_QUERY_TIMESTAMP, size_of::<u64>())?,
                query(D3D11_QUERY_TIMESTAMP, size_of::<u64>())?,
                query(D3D11_QUERY_TIMESTAMP, size_of::<u64>())?,
                query(D3D11_QUERY_TIMESTAMP, size_of::<u64>())?,
            ],
            copied: false,
            begun: false,
            next_boundary: 0,
        })
    }
    fn read(&self, context: &ID3D11DeviceContext) -> TimingResult<Option<Sample>> {
        if self.begun || self.next_boundary != self.stamps.len() {
            return Err(sequence_failure());
        }
        let mut disjoint = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
        if !read(context, &self.disjoint, &mut disjoint)? {
            return Ok(None);
        }
        // Read every timestamp before recycling the set, even for a disjoint interval.
        let mut ticks = [0u64; 6];
        for (query, tick) in self.stamps.iter().zip(&mut ticks) {
            if !read(context, query, tick)? {
                return Ok(None);
            }
        }
        if disjoint.Disjoint.as_bool() {
            return Ok(Some(Sample::Disjoint));
        }
        if disjoint.Frequency == 0 {
            return Err(Failure::new(FailureReason::Frequency, E_FAIL.0));
        }
        let sample = sample(ticks, disjoint.Frequency, false, self.copied);
        if sample == Sample::Unavailable {
            return Err(sequence_failure());
        }
        Ok(Some(sample))
    }
    fn begin(&mut self, context: &ID3D11DeviceContext, copied: bool) -> TimingResult<()> {
        if self.begun {
            return Err(sequence_failure());
        }
        self.copied = copied;
        self.next_boundary = 0;
        // SAFETY: same-device idle disjoint query; caller holds the immediate-context lock.
        unsafe { context.Begin(&self.disjoint) };
        self.begun = true;
        self.mark(context, 0)
    }
    fn mark(&mut self, context: &ID3D11DeviceContext, boundary: usize) -> TimingResult<()> {
        if !self.begun || boundary != self.next_boundary {
            return Err(sequence_failure());
        }
        let query = self.stamps.get(boundary).ok_or_else(sequence_failure)?;
        // SAFETY: timestamp End (never Begin); this set is not reused until every read completes.
        unsafe { context.End(query) };
        self.next_boundary += 1; // bounded by stamps.get above
        Ok(())
    }
    fn end(&mut self, context: &ID3D11DeviceContext) -> TimingResult<()> {
        self.mark(context, 5)?;
        self.close(context);
        Ok(())
    }
    fn close(&mut self, context: &ID3D11DeviceContext) {
        if self.begun {
            // SAFETY: balance exactly one successful Begin, including on a timing-only failure.
            unsafe { context.End(&self.disjoint) };
            self.begun = false;
        }
    }
}

// Query kind and T are paired at the two call sites. Validate the driver's size BEFORE the pointer
// is passed, on every read as well as creation. A mismatched/zero size disables instrumentation.
fn read<T>(
    context: &ID3D11DeviceContext,
    query: &ID3D11Query,
    value: &mut T,
) -> TimingResult<bool> {
    // SAFETY: live query metadata, no output buffer yet.
    data_size(unsafe { query.GetDataSize() }, size_of::<T>())?;
    // SAFETY: same-device query and matching initialized output, under the context lock.
    // The typed wrapper folds S_FALSE into Ok(()), so preserve the raw HRESULT here.
    let hr = unsafe {
        (context.vtable().GetData)(
            context.as_raw(),
            query.as_raw(),
            std::ptr::from_mut(value).cast(),
            size_of::<T>() as u32,
            D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
        )
    };
    data_ready(hr.0)
}

pub(super) struct Timing {
    device: ID3D11Device,
    queries: Option<Queries>,
    report: Report,
}
impl Timing {
    /// Called after the create self-test. Disabled instances stay latched; never retry per frame.
    pub fn create(device: &ID3D11Device) -> Option<Self> {
        let stats = timing::start()?;
        let mut timing = Self {
            device: device.clone(),
            queries: None,
            report: Report::new(stats),
        };
        timing.attempt(None, |queries, _| {
            *queries = Some(Queries::create(device)?);
            Ok(())
        });
        Some(timing)
    }
    fn attempt<T>(
        &mut self,
        context: Option<&ID3D11DeviceContext>,
        operation: impl FnOnce(&mut Option<Queries>, &mut Report) -> TimingResult<T>,
    ) -> Option<T> {
        let previous = (self.report.stats.state, self.report.stats.window);
        let result = self.report.attempt(|report| {
            if let Some(context) = context {
                // SAFETY: metadata on live same-device interfaces. Begin/End are void; device
                // status is the only HRESULT they expose indirectly. Never read a deferred context.
                if unsafe { context.GetType() } != D3D11_DEVICE_CONTEXT_IMMEDIATE {
                    return Err(Failure::new(FailureReason::Context, E_INVALIDARG.0));
                }
            }
            unsafe { self.device.GetDeviceRemovedReason() }
                .map_err(|e| failure(FailureReason::Device, e))?;
            let value = operation(&mut self.queries, report)?;
            unsafe { self.device.GetDeviceRemovedReason() }
                .map_err(|e| failure(FailureReason::Device, e))?;
            Ok(value)
        });
        if self.report.stats.state == 2
            && let (Some(context), Some(queries)) = (context, &mut self.queries)
        {
            queries.close(context);
        }
        if previous != (self.report.stats.state, self.report.stats.window) {
            timing::publish(self.report.stats);
        }
        result
    }
    pub fn begin(&mut self, context: &ID3D11DeviceContext, copied: bool) -> Option<usize> {
        self.attempt(Some(context), |queries, report| {
            let queries = queries.as_mut().ok_or_else(sequence_failure)?;
            report.ring.advance();
            if report.ring.ready_to_poll(0)
                && let Some(sample) = queries.read(context)?
            {
                if !report.ring.release(0) {
                    return Err(sequence_failure());
                }
                report.record(sample);
            }
            let Some(slot) = report.ring.reserve() else {
                report.record(Sample::Unavailable);
                return Ok(None); // Busy GPU: skip this measurement, never queue a second interval.
            };
            queries.begin(context, copied)?;
            Ok(Some(slot))
        })
        .flatten()
    }
    pub fn mark(&mut self, context: &ID3D11DeviceContext, slot: usize, boundary: usize) {
        self.attempt(Some(context), |queries, _| {
            if slot != 0 {
                return Err(sequence_failure());
            }
            queries
                .as_mut()
                .ok_or_else(sequence_failure)?
                .mark(context, boundary)
        });
    }
    pub fn end(&mut self, context: &ID3D11DeviceContext, slot: usize) {
        self.attempt(Some(context), |queries, _| {
            if slot != 0 {
                return Err(sequence_failure());
            }
            queries.as_mut().ok_or_else(sequence_failure)?.end(context)
        });
    }
}
impl Drop for Timing {
    fn drop(&mut self) {
        // No query calls at teardown: D3D11 owns the lifetime of submitted GPU work.
        self.report.close();
        timing::publish(self.report.stats);
    }
}

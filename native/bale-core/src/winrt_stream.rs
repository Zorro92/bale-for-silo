//! The player's `IRandomAccessStream` over the Rust range stream (slice 7, step 3 in
//! docs/rust-port.md). Media Foundation and FFmpegInteropX read through it; each `ReadAsync` runs on
//! the Windows thread pool and copies straight into the caller's buffer memory, so no read ever
//! passes through managed code or waits on .NET's garbage collector. Mirrors the C#
//! `RangeRandomAccessStream`: read-only, a shared position that `Seek` sets, and `Close` marks only
//! this adapter closed (the stream's owner closes the stream itself).

use std::sync::Arc;

use crate::range_stream::RangeStream;

/// The WinRT object as a raw `IRandomAccessStream` pointer owning one reference.
#[cfg(windows)]
pub fn create(stream: Arc<RangeStream>) -> *mut std::ffi::c_void {
    use windows::Storage::Streams::IRandomAccessStream;
    use windows_core::Interface;
    let object: IRandomAccessStream = imp::WinrtRangeStream::new(stream).into();
    object.into_raw()
}

#[cfg(not(windows))]
pub fn create(_stream: Arc<RangeStream>) -> *mut std::ffi::c_void {
    std::ptr::null_mut()
}

#[cfg(windows)]
mod imp {
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

    use windows::Foundation::{IClosable, IClosable_Impl};
    use windows::Storage::Streams::{
        IBuffer, IInputStream, IInputStream_Impl, IOutputStream, IOutputStream_Impl,
        IRandomAccessStream, IRandomAccessStream_Impl, InputStreamOptions,
    };
    use windows::Win32::System::WinRT::IBufferByteAccess;
    use windows_core::{
        AgileReference, Error, HRESULT, IUnknownImpl, Interface, Ref, Result, implement,
    };
    use windows_future::{IAsyncOperation, IAsyncOperationWithProgress};

    use crate::range_stream::{RangeStream, StreamError};

    const E_NOTIMPL: HRESULT = HRESULT(0x8000_4001_u32 as i32);
    const E_FAIL: HRESULT = HRESULT(0x8000_4005_u32 as i32);
    const E_INVALIDARG: HRESULT = HRESULT(0x8007_0057_u32 as i32);
    /// RO_E_CLOSED: the object has been closed.
    const RO_E_CLOSED: HRESULT = HRESULT(0x8000_0013_u32 as i32);

    fn error(code: HRESULT) -> Error {
        Error::from_hresult(code)
    }

    /// Transport failures surface as a plain failure: the player replans; no server text crosses.
    fn stream_error(error: StreamError) -> Error {
        Error::from_hresult(if error == StreamError::Disposed {
            RO_E_CLOSED
        } else {
            E_FAIL
        })
    }

    #[implement(IRandomAccessStream, IInputStream, IOutputStream, IClosable)]
    pub struct WinrtRangeStream {
        stream: Arc<RangeStream>,
        position: Arc<AtomicU64>,
        closed: AtomicBool,
    }

    impl WinrtRangeStream {
        pub fn new(stream: Arc<RangeStream>) -> Self {
            WinrtRangeStream {
                stream,
                position: Arc::new(AtomicU64::new(0)),
                closed: AtomicBool::new(false),
            }
        }

        fn check_open(&self) -> Result<()> {
            if self.closed.load(Ordering::Acquire) {
                Err(error(RO_E_CLOSED))
            } else {
                Ok(())
            }
        }
    }

    /// Fills `buffer` (up to `length` bytes, its capacity at most) from `position`, advancing it;
    /// a partial read returns after the first bytes (C# `StreamFill`). Runs on the thread pool.
    fn fill(
        stream: &RangeStream,
        position: &AtomicU64,
        buffer: &IBuffer,
        length: usize,
        partial: bool,
    ) -> Result<IBuffer> {
        buffer.SetLength(0)?;
        let access: IBufferByteAccess = buffer.cast()?;
        // SAFETY: IBufferByteAccess gives the buffer's backing memory, valid for its capacity while we
        // hold the buffer; `length` was clamped to that capacity.
        let memory = unsafe { access.Buffer()? };
        if memory.is_null() && length != 0 {
            return Err(error(E_FAIL));
        }
        let target: &mut [u8] = if length == 0 {
            &mut []
        } else {
            unsafe { std::slice::from_raw_parts_mut(memory, length) }
        };
        let mut total = 0;
        while total < length {
            let read = stream
                .read(position.load(Ordering::Acquire), &mut target[total..])
                .map_err(stream_error)?;
            if read == 0 {
                break;
            }
            position.fetch_add(read as u64, Ordering::AcqRel);
            total += read;
            if partial {
                break;
            }
        }
        buffer.SetLength(total as u32)?;
        Ok(buffer.clone())
    }

    impl IClosable_Impl for WinrtRangeStream_Impl {
        /// Marks this adapter closed. The range stream belongs to its owner, which closes it separately.
        fn Close(&self) -> Result<()> {
            self.closed.store(true, Ordering::Release);
            Ok(())
        }
    }

    impl IInputStream_Impl for WinrtRangeStream_Impl {
        fn ReadAsync(
            &self,
            buffer: Ref<IBuffer>,
            count: u32,
            options: InputStreamOptions,
        ) -> Result<IAsyncOperationWithProgress<IBuffer, u32>> {
            self.check_open()?;
            let buffer = buffer.ok()?.clone();
            if options != InputStreamOptions::None
                && options != InputStreamOptions::Partial
                && options != InputStreamOptions::ReadAhead
            {
                return Err(error(E_INVALIDARG));
            }
            let length = count.min(buffer.Capacity()?) as usize;
            let partial = options == InputStreamOptions::Partial;
            let (stream, position) = (self.stream.clone(), self.position.clone());
            // COM objects cross to the pool thread through an agile reference.
            let buffer = AgileReference::new(&buffer)?;
            Ok(IAsyncOperationWithProgress::spawn(move || {
                fill(&stream, &position, &buffer.resolve()?, length, partial)
            }))
        }
    }

    impl IOutputStream_Impl for WinrtRangeStream_Impl {
        fn WriteAsync(
            &self,
            _buffer: Ref<IBuffer>,
        ) -> Result<IAsyncOperationWithProgress<u32, u32>> {
            self.check_open()?;
            Err(error(E_NOTIMPL))
        }

        fn FlushAsync(&self) -> Result<IAsyncOperation<bool>> {
            self.check_open()?;
            Err(error(E_NOTIMPL))
        }
    }

    impl IRandomAccessStream_Impl for WinrtRangeStream_Impl {
        fn Size(&self) -> Result<u64> {
            self.check_open()?;
            Ok(self.stream.length())
        }

        fn SetSize(&self, _value: u64) -> Result<()> {
            self.check_open()?;
            Err(error(E_NOTIMPL))
        }

        fn GetInputStreamAt(&self, position: u64) -> Result<IInputStream> {
            self.Seek(position)?;
            Ok(self.to_interface::<IInputStream>())
        }

        fn GetOutputStreamAt(&self, _position: u64) -> Result<IOutputStream> {
            self.check_open()?;
            Err(error(E_NOTIMPL))
        }

        fn Position(&self) -> Result<u64> {
            self.check_open()?;
            Ok(self.position.load(Ordering::Acquire))
        }

        fn Seek(&self, position: u64) -> Result<()> {
            self.check_open()?;
            if position > i64::MAX as u64 {
                return Err(error(E_INVALIDARG));
            }
            self.position.store(position, Ordering::Release);
            Ok(())
        }

        fn CloneStream(&self) -> Result<IRandomAccessStream> {
            self.check_open()?;
            Err(error(E_NOTIMPL))
        }

        fn CanRead(&self) -> Result<bool> {
            self.check_open()?;
            Ok(true)
        }

        fn CanWrite(&self) -> Result<bool> {
            self.check_open()?;
            Ok(false)
        }
    }
}

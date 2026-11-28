#![deny(unsafe_op_in_unsafe_fn)]
mod archive;
use archive::{Error, Index, Result};
use std::{ffi::c_void, ptr, slice};

const INVALID: i32 = 0x8007000du32 as i32;
const TIMEOUT: i32 = 0x800705b4u32 as i32;
fn call(body: impl FnOnce() -> Result<()> + std::panic::UnwindSafe) -> i32 {
    match std::panic::catch_unwind(body) {
        Ok(Ok(())) => 0,
        Ok(Err(Error::Timeout)) => TIMEOUT,
        _ => INVALID,
    }
}

// All byte spans come from the synchronous C++ adapter. Its callers retain
// exclusive ownership of output storage and keep input alive for each call.
unsafe fn view<'a>(data: *const u8, size: usize, max: usize) -> Result<&'a [u8]> {
    if size == 0 {
        return Ok(&[]);
    }
    if size > max || data.is_null() {
        return Err(Error::Invalid);
    }
    Ok(unsafe { slice::from_raw_parts(data, size) })
}
unsafe fn index<'a>(handle: *const c_void) -> Result<&'a Index> {
    unsafe { handle.cast::<Index>().as_ref() }.ok_or(Error::Invalid)
}

#[unsafe(no_mangle)]
unsafe extern "C" fn ff_epub_open(data: *const u8, size: usize, handle: *mut *mut c_void) -> i32 {
    call(|| {
        if handle.is_null() {
            return Err(Error::Invalid);
        }
        let value = Index::open(unsafe { view(data, size, archive::MAX_ARCHIVE)? })?;
        unsafe {
            handle.write(Box::into_raw(Box::new(value)).cast());
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
unsafe extern "C" fn ff_epub_close(handle: *mut c_void) {
    if !handle.is_null() {
        drop(unsafe { Box::from_raw(handle.cast::<Index>()) });
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn ff_epub_size(
    handle: *const c_void,
    name: *const u8,
    length: usize,
    limit: usize,
    size: *mut usize,
) -> i32 {
    call(|| {
        if size.is_null() {
            return Err(Error::Invalid);
        }
        let value =
            unsafe { index(handle)? }.member_size(unsafe { view(name, length, 65535)? }, limit)?;
        unsafe {
            size.write(value);
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
unsafe extern "C" fn ff_epub_read(
    handle: *const c_void,
    data: *const u8,
    size: usize,
    name: *const u8,
    length: usize,
    dest: *mut u8,
    capacity: usize,
) -> i32 {
    call(|| {
        if capacity > archive::MAX_COVER || capacity != 0 && dest.is_null() {
            return Err(Error::Invalid);
        }
        let output = if capacity == 0 {
            &mut []
        } else {
            unsafe { slice::from_raw_parts_mut(dest, capacity) }
        };
        unsafe { index(handle)? }.extract(
            unsafe { view(data, size, archive::MAX_ARCHIVE)? },
            unsafe { view(name, length, 65535)? },
            output,
        )
    })
}

#[unsafe(no_mangle)]
unsafe extern "C" fn ff_epub_path(
    base: *const u8,
    base_len: usize,
    href: *const u8,
    href_len: usize,
    dest: *mut u8,
    capacity: usize,
    size: *mut usize,
) -> i32 {
    call(|| {
        if size.is_null() {
            return Err(Error::Invalid);
        }
        let path = archive::resolve(
            unsafe { view(base, base_len, archive::MAX_ARCHIVE)? },
            unsafe { view(href, href_len, archive::MAX_ARCHIVE)? },
        )?;
        if path.len() > capacity || dest.is_null() {
            return Err(Error::Invalid);
        }
        unsafe {
            ptr::copy_nonoverlapping(path.as_ptr(), dest, path.len());
            size.write(path.len());
        }
        Ok(())
    })
}

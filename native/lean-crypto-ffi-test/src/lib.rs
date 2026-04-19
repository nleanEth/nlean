//! Test-scheme FFI: exposes leanSpec's TEST scheme aggregated-XMSS verification
//! (LOG_LIFETIME=8, DIMENSION=4). Parallel to the PROD FFI in the sibling crate.
//!
//! Mirrors the minimal PROD API (leanmultisig_setup_verifier / leanmultisig_verify_aggregate)
//! with `_test` suffix. Name-clash with the PROD library is avoided by producing
//! a dedicated cdylib (`liblean_crypto_ffi_test.dylib`).

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::sync::Once;

use leansig_wrapper::{xmss_public_key_from_ssz, XmssPublicKey, MESSAGE_LENGTH};
use rec_aggregation::{init_aggregation_bytecode, xmss_verify_aggregation, AggregatedXMSS};

#[repr(C)]
pub struct LeanBuffer {
    pub ptr: *const u8,
    pub len: usize,
}

#[repr(i32)]
#[derive(Debug, Copy, Clone)]
pub enum LeanCryptoError {
    Ok = 0,
    NullPointer = 1,
    InvalidLength = 2,
    DeserializeError = 3,
    AggregateFailed = 5,
    Panic = 255,
}

impl LeanCryptoError {
    fn code(self) -> i32 {
        self as i32
    }
}

static VERIFIER_INIT: Once = Once::new();

#[no_mangle]
pub extern "C" fn leanmultisig_setup_verifier_test() -> i32 {
    wrap(|| {
        VERIFIER_INIT.call_once(|| {
            init_aggregation_bytecode();
        });
        Ok(())
    })
}

/// Verify an aggregated proof produced under the TEST signature scheme.
#[no_mangle]
pub extern "C" fn leanmultisig_verify_aggregate_test(
    pub_keys_ptr: *const LeanBuffer,
    pub_keys_len: usize,
    agg_ptr: *const u8,
    agg_len: usize,
    msg_ptr: *const u8,
    msg_len: usize,
    epoch: u32,
    out_is_valid: *mut u8,
) -> i32 {
    wrap(|| {
        ensure_out_ptrs(&[out_is_valid.cast()])?;

        let message = read_message(msg_ptr, msg_len)?;

        let mut pub_keys: Vec<XmssPublicKey> = read_buffers(pub_keys_ptr, pub_keys_len)?
            .into_iter()
            .map(|bytes| xmss_public_key_from_ssz(bytes).map_err(|_| LeanCryptoError::DeserializeError))
            .collect::<Result<Vec<_>, _>>()?;

        let agg_bytes = read_slice(agg_ptr, agg_len)?;
        let agg_sig = AggregatedXMSS::deserialize(agg_bytes).ok_or(LeanCryptoError::DeserializeError)?;

        pub_keys.sort();

        let is_valid = xmss_verify_aggregation(pub_keys, &agg_sig, &message, epoch).is_ok();

        unsafe {
            *out_is_valid = if is_valid { 1 } else { 0 };
        }

        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn lean_free_test(ptr: *mut u8, len: usize) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(ptr, len));
    }
}

fn wrap<F>(func: F) -> i32
where
    F: FnOnce() -> Result<(), LeanCryptoError> + std::panic::UnwindSafe,
{
    match catch_unwind(AssertUnwindSafe(func)) {
        Ok(result) => match result {
            Ok(_) => LeanCryptoError::Ok.code(),
            Err(err) => err.code(),
        },
        Err(_) => LeanCryptoError::Panic.code(),
    }
}

fn ensure_out_ptrs(ptrs: &[*mut u8]) -> Result<(), LeanCryptoError> {
    if ptrs.iter().any(|ptr| ptr.is_null()) {
        return Err(LeanCryptoError::NullPointer);
    }
    Ok(())
}

fn read_message(ptr: *const u8, len: usize) -> Result<[u8; MESSAGE_LENGTH], LeanCryptoError> {
    if len != MESSAGE_LENGTH {
        return Err(LeanCryptoError::InvalidLength);
    }
    let bytes = read_slice(ptr, len)?;
    let mut message = [0u8; MESSAGE_LENGTH];
    message.copy_from_slice(bytes);
    Ok(message)
}

fn read_slice<'a>(ptr: *const u8, len: usize) -> Result<&'a [u8], LeanCryptoError> {
    if ptr.is_null() {
        return Err(LeanCryptoError::NullPointer);
    }
    if len == 0 {
        return Err(LeanCryptoError::InvalidLength);
    }
    unsafe { Ok(std::slice::from_raw_parts(ptr, len)) }
}

fn read_buffers<'a>(ptr: *const LeanBuffer, len: usize) -> Result<Vec<&'a [u8]>, LeanCryptoError> {
    if len == 0 {
        return Ok(Vec::new());
    }
    if ptr.is_null() {
        return Err(LeanCryptoError::NullPointer);
    }

    let buffers = unsafe { std::slice::from_raw_parts(ptr, len) };
    let mut out = Vec::with_capacity(len);
    for buffer in buffers {
        let slice = read_slice(buffer.ptr, buffer.len)?;
        out.push(slice);
    }
    Ok(out)
}

// Unused in current API but kept to mirror the PROD FFI helpers verbatim.
#[allow(dead_code)]
fn write_output(bytes: Vec<u8>, out_ptr: *mut *mut u8, out_len: *mut usize) -> Result<(), LeanCryptoError> {
    if out_ptr.is_null() || out_len.is_null() {
        return Err(LeanCryptoError::NullPointer);
    }
    let boxed = bytes.into_boxed_slice();
    let len = boxed.len();
    let ptr = Box::into_raw(boxed) as *mut u8;
    unsafe {
        ptr::write(out_ptr, ptr);
        ptr::write(out_len, len);
    }
    Ok(())
}

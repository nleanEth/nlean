use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use leansig_hashsig::{
    MESSAGE_LENGTH,
    serialization::Serializable as HashsigSerializable,
    signature::SignatureScheme as HashsigSignatureSchemeTrait,
};
use lean_multisig::{
    xmss_aggregate_signatures,
    xmss_verify_aggregated_signatures,
    xmss_aggregation_setup_prover,
    xmss_aggregation_setup_verifier,
};
use rand::rng;
use ssz::{Decode, Encode};

pub type LeanSigHashsigScheme = leansig_hashsig::signature::generalized_xmss::instantiations_poseidon_top_level::lifetime_2_to_the_32::hashing_optimized::SIGTopLevelTargetSumLifetime32Dim64Base8;

type LeanSigHashsigPublicKey = <LeanSigHashsigScheme as leansig_hashsig::signature::SignatureScheme>::PublicKey;
type LeanSigHashsigSecretKey = <LeanSigHashsigScheme as leansig_hashsig::signature::SignatureScheme>::SecretKey;
type LeanSigHashsigSignature = <LeanSigHashsigScheme as leansig_hashsig::signature::SignatureScheme>::Signature;

type LeanSigAggPublicKey = LeanSigHashsigPublicKey;
type LeanSigAggSignature = LeanSigHashsigSignature;

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
    SigningFailed = 4,
    AggregateFailed = 5,
    ProofFailed = 6,
    InternalError = 7,
    Panic = 255,
}

impl LeanCryptoError {
    fn code(self) -> i32 {
        self as i32
    }
}

#[no_mangle]
pub extern "C" fn leansig_key_gen(
    activation_epoch: u32,
    num_active_epochs: u32,
    out_pk_ptr: *mut *mut u8,
    out_pk_len: *mut usize,
    out_sk_ptr: *mut *mut u8,
    out_sk_len: *mut usize,
) -> i32 {
    wrap(|| {
        ensure_out_ptrs(&[out_pk_ptr.cast(), out_pk_len.cast(), out_sk_ptr.cast(), out_sk_len.cast()])?;

        let mut rng = rng();
        let (pk, sk) = LeanSigHashsigScheme::key_gen(&mut rng, activation_epoch as usize, num_active_epochs as usize);
        write_output(pk.to_bytes(), out_pk_ptr, out_pk_len)?;
        write_output(sk.to_bytes(), out_sk_ptr, out_sk_len)?;
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn leansig_sign(
    sk_ptr: *const u8,
    sk_len: usize,
    epoch: u32,
    msg_ptr: *const u8,
    msg_len: usize,
    out_sig_ptr: *mut *mut u8,
    out_sig_len: *mut usize,
) -> i32 {
    wrap(|| {
        ensure_out_ptrs(&[out_sig_ptr.cast(), out_sig_len.cast()])?;

        let sk_bytes = read_slice(sk_ptr, sk_len)?;
        let sk = LeanSigHashsigSecretKey::from_bytes(sk_bytes).map_err(|_| LeanCryptoError::DeserializeError)?;

        let message = read_message(msg_ptr, msg_len)?;
        let sig = LeanSigHashsigScheme::sign(&sk, epoch, &message).map_err(|_| LeanCryptoError::SigningFailed)?;
        write_output(sig.to_bytes(), out_sig_ptr, out_sig_len)?;
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn leansig_verify(
    pk_ptr: *const u8,
    pk_len: usize,
    sig_ptr: *const u8,
    sig_len: usize,
    epoch: u32,
    msg_ptr: *const u8,
    msg_len: usize,
    out_is_valid: *mut u8,
) -> i32 {
    wrap(|| {
        ensure_out_ptrs(&[out_is_valid.cast()])?;

        let pk_bytes = read_slice(pk_ptr, pk_len)?;
        let sig_bytes = read_slice(sig_ptr, sig_len)?;

        let pk = LeanSigHashsigPublicKey::from_bytes(pk_bytes).map_err(|_| LeanCryptoError::DeserializeError)?;
        let sig = LeanSigHashsigSignature::from_bytes(sig_bytes).map_err(|_| LeanCryptoError::DeserializeError)?;

        let message = read_message(msg_ptr, msg_len)?;
        let valid = LeanSigHashsigScheme::verify(&pk, epoch, &message, &sig);

        unsafe {
            *out_is_valid = if valid { 1 } else { 0 };
        }
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn leanmultisig_setup_prover() -> i32 {
    wrap(|| {
        xmss_aggregation_setup_prover();
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn leanmultisig_setup_verifier() -> i32 {
    wrap(|| {
        xmss_aggregation_setup_verifier();
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn leanmultisig_aggregate(
    pub_keys_ptr: *const LeanBuffer,
    pub_keys_len: usize,
    sigs_ptr: *const LeanBuffer,
    sigs_len: usize,
    msg_ptr: *const u8,
    msg_len: usize,
    epoch: u32,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    wrap(|| {
        ensure_out_ptrs(&[out_ptr.cast(), out_len.cast()])?;
        if pub_keys_len != sigs_len {
            return Err(LeanCryptoError::InvalidLength);
        }

        let message = read_message(msg_ptr, msg_len)?;
        let pub_keys = read_buffers(pub_keys_ptr, pub_keys_len)?
            .into_iter()
            .map(|bytes| LeanSigAggPublicKey::from_bytes(bytes).map_err(|_| LeanCryptoError::DeserializeError))
            .collect::<Result<Vec<_>, _>>()?;

        let signatures = read_buffers(sigs_ptr, sigs_len)?
            .into_iter()
            .map(|bytes| LeanSigAggSignature::from_bytes(bytes).map_err(|_| LeanCryptoError::DeserializeError))
            .collect::<Result<Vec<_>, _>>()?;

        let agg = xmss_aggregate_signatures(&pub_keys, &signatures, &message, epoch)
            .map_err(|_| LeanCryptoError::AggregateFailed)?;

        write_output(agg.as_ssz_bytes(), out_ptr, out_len)?;
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn leanmultisig_verify_aggregate(
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
        let pub_keys = read_buffers(pub_keys_ptr, pub_keys_len)?
            .into_iter()
            .map(|bytes| LeanSigAggPublicKey::from_bytes(bytes).map_err(|_| LeanCryptoError::DeserializeError))
            .collect::<Result<Vec<_>, _>>()?;

        let agg_bytes = read_slice(agg_ptr, agg_len)?;
        let agg = Decode::from_ssz_bytes(agg_bytes)
            .map_err(|_| LeanCryptoError::DeserializeError)?;

        let is_valid = xmss_verify_aggregated_signatures(&pub_keys, &message, &agg, epoch).is_ok();

        unsafe {
            *out_is_valid = if is_valid { 1 } else { 0 };
        }

        Ok(())
    })
}


#[no_mangle]
pub extern "C" fn leansig_pk_json_to_bytes(
    json_ptr: *const u8,
    json_len: usize,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    wrap(|| {
        ensure_out_ptrs(&[out_ptr.cast(), out_len.cast()])?;
        let json_bytes = read_slice(json_ptr, json_len)?;
        let pk: LeanSigHashsigPublicKey =
            serde_json::from_slice(json_bytes).map_err(|_| LeanCryptoError::DeserializeError)?;
        write_output(pk.to_bytes(), out_ptr, out_len)?;
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn leansig_sk_json_to_bytes(
    json_ptr: *const u8,
    json_len: usize,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    wrap(|| {
        ensure_out_ptrs(&[out_ptr.cast(), out_len.cast()])?;
        let json_bytes = read_slice(json_ptr, json_len)?;
        let sk: LeanSigHashsigSecretKey =
            serde_json::from_slice(json_bytes).map_err(|_| LeanCryptoError::DeserializeError)?;
        write_output(sk.to_bytes(), out_ptr, out_len)?;
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn lean_free(ptr: *mut u8, len: usize) {
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

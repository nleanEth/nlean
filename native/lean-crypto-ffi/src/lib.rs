use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::sync::Once;

// ─── Individual keygen/sign/verify — leanSig devnet4 ─────────────────
use leansig_hashsig::{
    MESSAGE_LENGTH,
    serialization::Serializable as HashsigSerializable,
    signature::SignatureScheme as HashsigSignatureSchemeTrait,
};

// devnet4: instantiations_aborting with Dim46 (was instantiations_poseidon_top_level Dim64)
pub type LeanSigHashsigScheme = leansig_hashsig::signature::generalized_xmss::instantiations_aborting::lifetime_2_to_the_32::SchemeAbortingTargetSumLifetime32Dim46Base8;

type LeanSigHashsigPublicKey = <LeanSigHashsigScheme as leansig_hashsig::signature::SignatureScheme>::PublicKey;
type LeanSigHashsigSecretKey = <LeanSigHashsigScheme as leansig_hashsig::signature::SignatureScheme>::SecretKey;
type LeanSigHashsigSignature = <LeanSigHashsigScheme as leansig_hashsig::signature::SignatureScheme>::Signature;

// ─── Recursive aggregation — leanMultisig ─────────────────────────────
// leansig_wrapper re-exports the leanSig types that rec_aggregation accepts.
// Since both leansig_hashsig and leansig_wrapper resolve to the same leansig
// crate (devnet4 branch), these aliases should be identical to the hashsig types.
use leansig_wrapper::XmssPublicKey as AggXmssPublicKey;
use leansig_wrapper::XmssSignature as AggXmssSignature;
use rec_aggregation::{
    init_aggregation_bytecode,
    xmss_aggregate as rec_xmss_aggregate,
    xmss_verify_aggregation,
    AggregatedXMSS,
};

use rand::rngs::ThreadRng;

// Aggregation pub key / sig aliases — should be the same types as hashsig
// types when Cargo deduplicates the leansig dependency.
type LeanSigAggPublicKey = AggXmssPublicKey;
type LeanSigAggSignature = AggXmssSignature;

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

// ─── Individual key operations (unchanged API) ────────────────────────

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

        let mut rng = ThreadRng::default();
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

// ─── Aggregation setup / prove / verify ───────────────────────────────

static PROVER_INIT: Once = Once::new();
static VERIFIER_INIT: Once = Once::new();

#[no_mangle]
pub extern "C" fn leanmultisig_setup_prover() -> i32 {
    wrap(|| {
        PROVER_INIT.call_once(|| {
            init_aggregation_bytecode();
            backend::precompute_dft_twiddles::<backend::KoalaBear>(1 << 24);
        });
        Ok(())
    })
}

#[no_mangle]
pub extern "C" fn leanmultisig_setup_verifier() -> i32 {
    wrap(|| {
        VERIFIER_INIT.call_once(|| {
            init_aggregation_bytecode();
        });
        Ok(())
    })
}

/// Flat aggregate (no children) — backward-compatible FFI signature.
/// Uses rec_aggregation internally with empty children.
/// Output is now postcard+lz4 serialized (was SSZ).
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

        // Build raw XMSS pairs for rec_aggregation
        let raw_xmss: Vec<(AggXmssPublicKey, AggXmssSignature)> = pub_keys
            .into_iter()
            .zip(signatures.into_iter())
            .collect();

        let (_sorted_pub_keys, agg_sig) = rec_xmss_aggregate(
            &[],        // no children (flat aggregation)
            raw_xmss,
            &message,
            epoch,      // slot = epoch in current nlean model
            2,          // log_inv_rate: 2 for production (rate 1/4)
        );

        // Serialize using postcard+lz4 (new format, replaces SSZ)
        write_output(agg_sig.serialize(), out_ptr, out_len)?;
        Ok(())
    })
}

/// Recursive aggregate: merges existing children proofs (and optionally raw XMSS pairs).
/// Children are (public_keys, serialized_AggregatedXMSS) pairs.
/// Public keys for all children are passed as a flat array with per-child counts.
/// Output is postcard+lz4 serialized.
#[no_mangle]
pub extern "C" fn leanmultisig_aggregate_recursive(
    // Children aggregate proofs (serialized AggregatedXMSS), one per child
    children_proofs_ptr: *const LeanBuffer,
    children_count: usize,
    // Flat array of all public keys across all children
    children_pks_ptr: *const LeanBuffer,
    children_pks_total: usize,
    // Per-child PK counts (array of usize, length == children_count)
    children_pk_counts_ptr: *const usize,
    // Optional raw XMSS pairs
    raw_pks_ptr: *const LeanBuffer,
    raw_pks_len: usize,
    raw_sigs_ptr: *const LeanBuffer,
    raw_sigs_len: usize,
    // Message and epoch
    msg_ptr: *const u8,
    msg_len: usize,
    epoch: u32,
    // Output
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    wrap(|| {
        ensure_out_ptrs(&[out_ptr.cast(), out_len.cast()])?;
        if raw_pks_len != raw_sigs_len {
            return Err(LeanCryptoError::InvalidLength);
        }
        if children_count == 0 {
            return Err(LeanCryptoError::InvalidLength);
        }

        let message = read_message(msg_ptr, msg_len)?;

        // Read per-child PK counts
        if children_pk_counts_ptr.is_null() {
            return Err(LeanCryptoError::NullPointer);
        }
        let pk_counts = unsafe { std::slice::from_raw_parts(children_pk_counts_ptr, children_count) };

        // Read flat array of all children public keys
        let all_child_pks_bufs = read_buffers(children_pks_ptr, children_pks_total)?;
        let all_child_pks: Vec<LeanSigAggPublicKey> = all_child_pks_bufs
            .into_iter()
            .map(|bytes| LeanSigAggPublicKey::from_bytes(bytes).map_err(|_| LeanCryptoError::DeserializeError))
            .collect::<Result<Vec<_>, _>>()?;

        // Read and deserialize children proofs
        let children_proof_bufs = read_buffers(children_proofs_ptr, children_count)?;

        // Build (pub_keys_slice, AggregatedXMSS) tuples
        let mut offset = 0usize;
        let mut children_with_pks: Vec<(Vec<LeanSigAggPublicKey>, AggregatedXMSS)> = Vec::with_capacity(children_count);
        for i in 0..children_count {
            let count = pk_counts[i];
            let pks: Vec<LeanSigAggPublicKey> = all_child_pks[offset..offset + count].to_vec();
            offset += count;

            let agg = AggregatedXMSS::deserialize(children_proof_bufs[i])
                .ok_or(LeanCryptoError::DeserializeError)?;
            children_with_pks.push((pks, agg));
        }

        // Build children ref slice in the format rec_xmss_aggregate expects
        let children_refs: Vec<(&[LeanSigAggPublicKey], AggregatedXMSS)> = children_with_pks
            .iter()
            .map(|(pks, agg)| (pks.as_slice(), agg.clone()))
            .collect();

        // Deserialize raw XMSS pairs (may be empty)
        let raw_xmss = if raw_pks_len > 0 {
            let pub_keys = read_buffers(raw_pks_ptr, raw_pks_len)?
                .into_iter()
                .map(|bytes| LeanSigAggPublicKey::from_bytes(bytes).map_err(|_| LeanCryptoError::DeserializeError))
                .collect::<Result<Vec<_>, _>>()?;
            let signatures = read_buffers(raw_sigs_ptr, raw_sigs_len)?
                .into_iter()
                .map(|bytes| LeanSigAggSignature::from_bytes(bytes).map_err(|_| LeanCryptoError::DeserializeError))
                .collect::<Result<Vec<_>, _>>()?;
            pub_keys.into_iter().zip(signatures.into_iter()).collect()
        } else {
            Vec::new()
        };

        let (_sorted_pub_keys, agg_sig) = rec_xmss_aggregate(
            &children_refs,
            raw_xmss,
            &message,
            epoch,
            2, // log_inv_rate
        );

        write_output(agg_sig.serialize(), out_ptr, out_len)?;
        Ok(())
    })
}

/// Verify an aggregated proof.
/// Input proof must be postcard+lz4 serialized (new format).
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
        let mut pub_keys = read_buffers(pub_keys_ptr, pub_keys_len)?
            .into_iter()
            .map(|bytes| LeanSigAggPublicKey::from_bytes(bytes).map_err(|_| LeanCryptoError::DeserializeError))
            .collect::<Result<Vec<_>, _>>()?;

        let agg_bytes = read_slice(agg_ptr, agg_len)?;
        let agg_sig = AggregatedXMSS::deserialize(agg_bytes)
            .ok_or(LeanCryptoError::DeserializeError)?;

        // rec_aggregation requires sorted pub_keys
        pub_keys.sort();

        let is_valid = xmss_verify_aggregation(pub_keys, &agg_sig, &message, epoch).is_ok();

        unsafe {
            *out_is_valid = if is_valid { 1 } else { 0 };
        }

        Ok(())
    })
}

// ─── JSON key converters ──────────────────────────────────────────────

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

// ─── Memory management ────────────────────────────────────────────────

#[no_mangle]
pub extern "C" fn lean_free(ptr: *mut u8, len: usize) {
    if ptr.is_null() {
        return;
    }

    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(ptr, len));
    }
}

// ─── Internal helpers ─────────────────────────────────────────────────

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

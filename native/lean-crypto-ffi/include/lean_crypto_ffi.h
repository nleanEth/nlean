#ifndef LEAN_CRYPTO_FFI_H
#define LEAN_CRYPTO_FFI_H

#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>

typedef struct LeanBuffer {
  const uint8_t *ptr;
  uintptr_t len;
} LeanBuffer;

int32_t leansig_key_gen(uint32_t activation_epoch,
                        uint32_t num_active_epochs,
                        uint8_t **out_pk_ptr,
                        uintptr_t *out_pk_len,
                        uint8_t **out_sk_ptr,
                        uintptr_t *out_sk_len);

int32_t leansig_sign(const uint8_t *sk_ptr,
                     uintptr_t sk_len,
                     uint32_t epoch,
                     const uint8_t *msg_ptr,
                     uintptr_t msg_len,
                     uint8_t **out_sig_ptr,
                     uintptr_t *out_sig_len);

int32_t leansig_verify(const uint8_t *pk_ptr,
                       uintptr_t pk_len,
                       const uint8_t *sig_ptr,
                       uintptr_t sig_len,
                       uint32_t epoch,
                       const uint8_t *msg_ptr,
                       uintptr_t msg_len,
                       uint8_t *out_is_valid);

int32_t leanmultisig_setup_prover(void);

int32_t leanmultisig_setup_verifier(void);

/**
 * Flat aggregate (no children) — backward-compatible FFI signature.
 * Uses rec_aggregation internally with empty children.
 * Output is now postcard+lz4 serialized (was SSZ).
 */
int32_t leanmultisig_aggregate(const struct LeanBuffer *pub_keys_ptr,
                               uintptr_t pub_keys_len,
                               const struct LeanBuffer *sigs_ptr,
                               uintptr_t sigs_len,
                               const uint8_t *msg_ptr,
                               uintptr_t msg_len,
                               uint32_t epoch,
                               uint8_t **out_ptr,
                               uintptr_t *out_len);

/**
 * Recursive aggregate: merges existing children proofs (and optionally raw XMSS pairs).
 * Children are (public_keys, serialized_AggregatedXMSS) pairs.
 * Public keys for all children are passed as a flat array with per-child counts.
 * Output is postcard+lz4 serialized.
 */
int32_t leanmultisig_aggregate_recursive(const struct LeanBuffer *children_proofs_ptr,
                                         uintptr_t children_count,
                                         const struct LeanBuffer *children_pks_ptr,
                                         uintptr_t children_pks_total,
                                         const uintptr_t *children_pk_counts_ptr,
                                         const struct LeanBuffer *raw_pks_ptr,
                                         uintptr_t raw_pks_len,
                                         const struct LeanBuffer *raw_sigs_ptr,
                                         uintptr_t raw_sigs_len,
                                         const uint8_t *msg_ptr,
                                         uintptr_t msg_len,
                                         uint32_t epoch,
                                         uint8_t **out_ptr,
                                         uintptr_t *out_len);

/**
 * Verify an aggregated proof.
 * Input proof must be postcard+lz4 serialized (new format).
 */
int32_t leanmultisig_verify_aggregate(const struct LeanBuffer *pub_keys_ptr,
                                      uintptr_t pub_keys_len,
                                      const uint8_t *agg_ptr,
                                      uintptr_t agg_len,
                                      const uint8_t *msg_ptr,
                                      uintptr_t msg_len,
                                      uint32_t epoch,
                                      uint8_t *out_is_valid);

int32_t leansig_pk_json_to_bytes(const uint8_t *json_ptr,
                                 uintptr_t json_len,
                                 uint8_t **out_ptr,
                                 uintptr_t *out_len);

int32_t leansig_sk_json_to_bytes(const uint8_t *json_ptr,
                                 uintptr_t json_len,
                                 uint8_t **out_ptr,
                                 uintptr_t *out_len);

void lean_free(uint8_t *ptr, uintptr_t len);

#endif  /* LEAN_CRYPTO_FFI_H */

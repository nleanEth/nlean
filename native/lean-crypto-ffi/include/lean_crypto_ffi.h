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

int32_t leanmultisig_aggregate(const struct LeanBuffer *pub_keys_ptr,
                               uintptr_t pub_keys_len,
                               const struct LeanBuffer *sigs_ptr,
                               uintptr_t sigs_len,
                               const uint8_t *msg_ptr,
                               uintptr_t msg_len,
                               uint32_t epoch,
                               uint8_t **out_ptr,
                               uintptr_t *out_len);

int32_t leanmultisig_verify_aggregate(const struct LeanBuffer *pub_keys_ptr,
                                      uintptr_t pub_keys_len,
                                      const uint8_t *agg_ptr,
                                      uintptr_t agg_len,
                                      const uint8_t *msg_ptr,
                                      uintptr_t msg_len,
                                      uint32_t epoch,
                                      uint8_t *out_is_valid);

void lean_free(uint8_t *ptr, uintptr_t len);

#endif  /* LEAN_CRYPTO_FFI_H */

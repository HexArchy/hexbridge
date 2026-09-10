// Thin C wrapper around libopus.
//
// Swift cannot call C variadics, and `opus_encoder_ctl` is one, so every knob we
// need is set here at construction time and the encoder is handed back as an
// opaque pointer.
#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct MBEncoder MBEncoder;

/// Creates a mono voice encoder. Returns NULL and sets *error on failure.
MBEncoder *mb_encoder_create(int sample_rate,
                             int bitrate,
                             int complexity,
                             int inband_fec,
                             int expected_loss_percent,
                             int *error);

void mb_encoder_destroy(MBEncoder *encoder);

/// Encodes one frame. Returns the packet length, or a negative opus error code.
int mb_encoder_encode_float(MBEncoder *encoder,
                            const float *pcm,
                            int frame_samples,
                            unsigned char *out,
                            int out_capacity);

/// Human-readable text for a negative opus error code.
const char *mb_opus_strerror(int error);

#ifdef __cplusplus
}
#endif

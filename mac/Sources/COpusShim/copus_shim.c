#include "include/copus_shim.h"

#include <opus/opus.h>
#include <stdlib.h>

struct MBEncoder {
    OpusEncoder *enc;
};

MBEncoder *mb_encoder_create(int sample_rate,
                             int bitrate,
                             int complexity,
                             int inband_fec,
                             int expected_loss_percent,
                             int *error) {
    int err = OPUS_OK;
    OpusEncoder *enc = opus_encoder_create(sample_rate, 1, OPUS_APPLICATION_VOIP, &err);
    if (enc == NULL || err != OPUS_OK) {
        if (error) {
            *error = err;
        }
        if (enc) {
            opus_encoder_destroy(enc);
        }
        return NULL;
    }

    opus_encoder_ctl(enc, OPUS_SET_BITRATE(bitrate));
    opus_encoder_ctl(enc, OPUS_SET_COMPLEXITY(complexity));
    opus_encoder_ctl(enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));
    opus_encoder_ctl(enc, OPUS_SET_INBAND_FEC(inband_fec));
    opus_encoder_ctl(enc, OPUS_SET_PACKET_LOSS_PERC(expected_loss_percent));
    // DTX would save uplink during silence, but it leaves the host jitter buffer
    // guessing how long the gap is; a steady 50 packets/s is cheaper to reason about.
    opus_encoder_ctl(enc, OPUS_SET_DTX(0));

    MBEncoder *wrapper = calloc(1, sizeof(MBEncoder));
    if (wrapper == NULL) {
        opus_encoder_destroy(enc);
        if (error) {
            *error = OPUS_ALLOC_FAIL;
        }
        return NULL;
    }

    wrapper->enc = enc;
    if (error) {
        *error = OPUS_OK;
    }
    return wrapper;
}

void mb_encoder_destroy(MBEncoder *encoder) {
    if (encoder == NULL) {
        return;
    }
    opus_encoder_destroy(encoder->enc);
    free(encoder);
}

int mb_encoder_encode_float(MBEncoder *encoder,
                            const float *pcm,
                            int frame_samples,
                            unsigned char *out,
                            int out_capacity) {
    return opus_encode_float(encoder->enc, pcm, frame_samples, out, out_capacity);
}

const char *mb_opus_strerror(int error) {
    return opus_strerror(error);
}

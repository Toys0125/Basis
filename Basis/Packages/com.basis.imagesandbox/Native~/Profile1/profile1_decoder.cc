#include <jxl/codestream_header.h>
#include <jxl/color_encoding.h>
#include <jxl/decode.h>
#include <jxl/types.h>

#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <limits>

namespace {

constexpr uint64_t kAbiVersion = 1;
constexpr uint32_t kMaximumWidth = 2048;
constexpr uint32_t kMaximumHeight = 2048;
constexpr uint64_t kMaximumCanvasPixels = 4'194'304ULL;
constexpr uint32_t kMaximumLogicalFrames = 512;
constexpr uint64_t kMaximumSubmittedCanvasPixels = 33'554'432ULL;
constexpr uint32_t kMinimumFrameDurationMicroseconds = 33'334;
constexpr uint64_t kMaximumBaseTimelineMicroseconds = 300'000'000ULL;
constexpr uint32_t kTimebaseNumerator = 1'000'000;
constexpr uint32_t kTimebaseDenominator = 1;

// The host reads this as a little-endian u64 array. Keep additions append-only
// until the ABI version changes.
enum ResultSlot : uint32_t {
    kSlotAbiVersion = 0,
    kSlotStatus = 1,
    kSlotWidth = 2,
    kSlotHeight = 3,
    kSlotLogicalFrameCount = 4,
    kSlotTotalPlayCount = 5,
    kSlotSubmittedCanvasPixels = 6,
    kSlotBaseTimelineMicroseconds = 7,
    kSlotPublicRegularLayerCount = 8,
    kSlotPublicRegularLayerPixels = 9,
    kSlotCroppedLayerCount = 10,
    kSlotReferenceReadEdges = 11,
    kSlotSavedReferenceCount = 12,
    kSlotBlendOperationCount = 13,
    kSlotMaximumReferenceChainDepth = 14,
    kSlotPreviewPixels = 15,
    kSlotDurationCount = 16,
    kSlotDurations = 17,
    kSlotDiagnosticReason = kSlotDurations + kMaximumLogicalFrames,
    kResultSlotCount = kSlotDiagnosticReason + 1,
};

enum Status : uint32_t {
    kSuccess = 0,
    kMalformed = 1,
    kUnsupportedProfile = 2,
    kSharedLimitExceeded = 3,
};

enum DiagnosticReason : uint32_t {
    kReasonNone = 0,
    kReasonDimensions = 1,
    kReasonCanvasPixels = 2,
    kReasonBitsPerSample = 3,
    kReasonColorChannels = 4,
    kReasonExtraChannels = 5,
    kReasonAlpha = 6,
    kReasonPremultipliedAlpha = 7,
    kReasonOrientation = 8,
    kReasonExtraChannel = 9,
    kReasonMissingAnimation = 10,
    kReasonTimebase = 11,
    kReasonColorEncoding = 12,
    kReasonLogicalFrames = 13,
    kReasonFrameDuration = 14,
    kReasonTimeline = 15,
    kReasonSubmittedPixels = 16,
    kReasonStructuralLayerPixels = 17,
    kReasonStructuralLayerCount = 18,
    kReasonReferenceSource = 19,
    kReasonPreviewPixels = 20,
    kReasonLogicalMismatch = 21,
    kReasonDecoder = 22,
};

struct StructuralMetrics {
    uint64_t layer_count = 0;
    uint64_t layer_pixels = 0;
    uint64_t cropped_layer_count = 0;
    uint64_t reference_read_edges = 0;
    uint64_t saved_reference_count = 0;
    uint64_t blend_operations = 0;
    uint64_t maximum_reference_chain_depth = 0;
    uint64_t reference_depth[4] = {0, 0, 0, 0};
};

struct LogicalInfo {
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t total_play_count = 0;
    uint32_t frame_count = 0;
    uint64_t submitted_pixels = 0;
    uint64_t timeline_microseconds = 0;
    uint64_t preview_pixels = 0;
    uint64_t durations[kMaximumLogicalFrames] = {};
};

bool CheckedMultiply(uint64_t left, uint64_t right, uint64_t* result) {
    if (left != 0 && right > std::numeric_limits<uint64_t>::max() / left) {
        return false;
    }
    *result = left * right;
    return true;
}

bool CheckedAdd(uint64_t left, uint64_t right, uint64_t* result) {
    if (right > std::numeric_limits<uint64_t>::max() - left) {
        return false;
    }
    *result = left + right;
    return true;
}

Status ValidateBasicInfo(
    const JxlDecoder* decoder,
    const JxlBasicInfo& info,
    LogicalInfo* logical,
    DiagnosticReason* reason) {
    if (info.xsize == 0 || info.ysize == 0 || info.xsize > kMaximumWidth || info.ysize > kMaximumHeight) {
        *reason = kReasonDimensions;
        return kSharedLimitExceeded;
    }

    uint64_t canvas_pixels = 0;
    if (!CheckedMultiply(info.xsize, info.ysize, &canvas_pixels) || canvas_pixels > kMaximumCanvasPixels) {
        *reason = kReasonCanvasPixels;
        return kSharedLimitExceeded;
    }

    if (info.bits_per_sample != 8 || info.exponent_bits_per_sample != 0) {
        *reason = kReasonBitsPerSample;
        return kUnsupportedProfile;
    }
    if (info.num_color_channels != 3) {
        *reason = kReasonColorChannels;
        return kUnsupportedProfile;
    }
    if (info.num_extra_channels != 1) {
        *reason = kReasonExtraChannels;
        return kUnsupportedProfile;
    }
    if (info.alpha_bits != 8 || info.alpha_exponent_bits != 0) {
        *reason = kReasonAlpha;
        return kUnsupportedProfile;
    }
    if (info.alpha_premultiplied != JXL_FALSE) {
        *reason = kReasonPremultipliedAlpha;
        return kUnsupportedProfile;
    }
    if (info.orientation != JXL_ORIENT_IDENTITY) {
        *reason = kReasonOrientation;
        return kUnsupportedProfile;
    }

    JxlExtraChannelInfo alpha{};
    if (JxlDecoderGetExtraChannelInfo(decoder, 0, &alpha) != JXL_DEC_SUCCESS ||
        alpha.type != JXL_CHANNEL_ALPHA || alpha.bits_per_sample != 8 ||
        alpha.exponent_bits_per_sample != 0 || alpha.dim_shift != 0 ||
        alpha.alpha_premultiplied != JXL_FALSE) {
        *reason = kReasonExtraChannel;
        return kUnsupportedProfile;
    }

    // A Profile 1 payload always carries animation timing, including the
    // canonical one-logical-frame case.
    if (info.have_animation != JXL_TRUE) {
        *reason = kReasonMissingAnimation;
        return kUnsupportedProfile;
    }
    if (info.animation.tps_numerator != kTimebaseNumerator ||
        info.animation.tps_denominator != kTimebaseDenominator) {
        *reason = kReasonTimebase;
        return kUnsupportedProfile;
    }

    logical->width = info.xsize;
    logical->height = info.ysize;
    logical->total_play_count = info.animation.num_loops;
    if (info.have_preview == JXL_TRUE) {
        if (!CheckedMultiply(info.preview.xsize, info.preview.ysize, &logical->preview_pixels)) {
            *reason = kReasonPreviewPixels;
            return kSharedLimitExceeded;
        }
    }
    return kSuccess;
}

Status ValidateColorEncoding(const JxlDecoder* decoder, DiagnosticReason* reason) {
    JxlColorEncoding color{};
    if (JxlDecoderGetColorAsEncodedProfile(
            decoder,
            JXL_COLOR_PROFILE_TARGET_ORIGINAL,
            &color) != JXL_DEC_SUCCESS) {
        *reason = kReasonColorEncoding;
        return kUnsupportedProfile;
    }

    if (color.color_space != JXL_COLOR_SPACE_RGB ||
        color.white_point != JXL_WHITE_POINT_D65 ||
        color.primaries != JXL_PRIMARIES_SRGB ||
        color.transfer_function != JXL_TRANSFER_FUNCTION_SRGB) {
        *reason = kReasonColorEncoding;
        return kUnsupportedProfile;
    }
    return kSuccess;
}

bool BlendReadsReference(JxlBlendMode mode) {
    return mode != JXL_BLEND_REPLACE;
}

Status AccumulateStructure(
    const JxlFrameHeader& header,
    StructuralMetrics* metrics,
    DiagnosticReason* reason) {
    const JxlLayerInfo& layer = header.layer_info;
    uint64_t pixels = 0;
    if (!CheckedMultiply(layer.xsize, layer.ysize, &pixels) ||
        !CheckedAdd(metrics->layer_pixels, pixels, &metrics->layer_pixels)) {
        *reason = kReasonStructuralLayerPixels;
        return kSharedLimitExceeded;
    }
    if (metrics->layer_count == std::numeric_limits<uint64_t>::max()) {
        *reason = kReasonStructuralLayerCount;
        return kSharedLimitExceeded;
    }
    ++metrics->layer_count;

    if (layer.have_crop == JXL_TRUE) {
        ++metrics->cropped_layer_count;
    }

    uint64_t chain_depth = 1;
    if (BlendReadsReference(layer.blend_info.blendmode)) {
        if (layer.blend_info.source >= 4) {
            *reason = kReasonReferenceSource;
            return kMalformed;
        }
        ++metrics->reference_read_edges;
        ++metrics->blend_operations;
        chain_depth = metrics->reference_depth[layer.blend_info.source] + 1;
    }
    if (chain_depth > metrics->maximum_reference_chain_depth) {
        metrics->maximum_reference_chain_depth = chain_depth;
    }

    // save_as_reference is public decoder state. ID 0 is meaningful only when
    // the frame duration is zero; for displayed-duration frames, libjxl defines
    // zero as "not referenced in the future".
    if (layer.save_as_reference < 4 &&
        (layer.save_as_reference != 0 || header.duration == 0)) {
        metrics->reference_depth[layer.save_as_reference] = chain_depth;
        ++metrics->saved_reference_count;
    }
    return kSuccess;
}

void DiscardPixels(void*, size_t, size_t, size_t, const void*) {}

Status AccumulateLogicalDisplayedFrame(
    uint32_t duration,
    LogicalInfo* logical,
    DiagnosticReason* reason) {
    if (logical->frame_count >= kMaximumLogicalFrames) {
        *reason = kReasonLogicalFrames;
        return kSharedLimitExceeded;
    }
    if (duration < kMinimumFrameDurationMicroseconds) {
        *reason = kReasonFrameDuration;
        return kSharedLimitExceeded;
    }

    uint64_t timeline = 0;
    if (!CheckedAdd(logical->timeline_microseconds, duration, &timeline) ||
        timeline > kMaximumBaseTimelineMicroseconds) {
        *reason = kReasonTimeline;
        return kSharedLimitExceeded;
    }
    logical->timeline_microseconds = timeline;
    logical->durations[logical->frame_count++] = duration;

    uint64_t submitted = 0;
    if (!CheckedMultiply(
            static_cast<uint64_t>(logical->width) * logical->height,
            logical->frame_count,
            &submitted) ||
        submitted > kMaximumSubmittedCanvasPixels) {
        *reason = kReasonSubmittedPixels;
        return kSharedLimitExceeded;
    }
    logical->submitted_pixels = submitted;
    return kSuccess;
}

Status RunStructurePass(
    const uint8_t* data,
    size_t size,
    StructuralMetrics* metrics,
    DiagnosticReason* reason) {
    JxlDecoder* decoder = JxlDecoderCreate(nullptr);
    if (decoder == nullptr) {
        *reason = kReasonDecoder;
        return kMalformed;
    }

    Status result = kMalformed;
    do {
        if (JxlDecoderSetKeepOrientation(decoder, JXL_TRUE) != JXL_DEC_SUCCESS ||
            JxlDecoderSetCoalescing(decoder, JXL_FALSE) != JXL_DEC_SUCCESS ||
            JxlDecoderSubscribeEvents(decoder, JXL_DEC_FRAME | JXL_DEC_FULL_IMAGE) != JXL_DEC_SUCCESS ||
            JxlDecoderSetInput(decoder, data, size) != JXL_DEC_SUCCESS) {
            break;
        }
        JxlDecoderCloseInput(decoder);

        bool saw_frame = false;
        while (true) {
            const JxlDecoderStatus status = JxlDecoderProcessInput(decoder);
            if (status == JXL_DEC_FRAME) {
                saw_frame = true;
                JxlFrameHeader header{};
                if (JxlDecoderGetFrameHeader(decoder, &header) != JXL_DEC_SUCCESS) {
                    result = kMalformed;
                    break;
                }
                result = AccumulateStructure(header, metrics, reason);
                if (result != kSuccess) {
                    break;
                }
                continue;
            }
            if (status == JXL_DEC_NEED_IMAGE_OUT_BUFFER) {
                // Structural metrics are fully available from JxlFrameHeader. Do not
                // decode pixels a second time just to advance to the next regular layer.
                if (JxlDecoderSkipCurrentFrame(decoder) != JXL_DEC_SUCCESS) {
                    result = kMalformed;
                    break;
                }
                continue;
            }
            if (status == JXL_DEC_FULL_IMAGE) {
                continue;
            }
            if (status == JXL_DEC_SUCCESS) {
                const size_t remaining = JxlDecoderReleaseInput(decoder);
                result = saw_frame && remaining == 0 ? kSuccess : kMalformed;
                break;
            }
            if (status == JXL_DEC_NEED_MORE_INPUT || status == JXL_DEC_ERROR ||
                status == JXL_DEC_NEED_IMAGE_OUT_BUFFER || status == JXL_DEC_NEED_PREVIEW_OUT_BUFFER) {
                result = kMalformed;
                break;
            }
        }
    } while (false);

    JxlDecoderDestroy(decoder);
    return result;
}

Status RunLogicalHeaderPass(
    const uint8_t* data,
    size_t size,
    LogicalInfo* logical,
    DiagnosticReason* reason) {
    JxlDecoder* decoder = JxlDecoderCreate(nullptr);
    if (decoder == nullptr) {
        *reason = kReasonDecoder;
        return kMalformed;
    }

    Status result = kMalformed;
    do {
        // Coalescing would force libjxl to perform blend/reference work merely to
        // discover animation durations. With coalescing disabled, public
        // JxlFrameHeader events expose each regular layer. Non-zero durations are
        // displayed animation frames; a zero-duration last frame is also a
        // displayed frame and is rejected by Profile 1's minimum-duration rule.
        // The later coalesced validation pass remains the semantic backstop.
        if (JxlDecoderSetKeepOrientation(decoder, JXL_TRUE) != JXL_DEC_SUCCESS ||
            JxlDecoderSetCoalescing(decoder, JXL_FALSE) != JXL_DEC_SUCCESS ||
            JxlDecoderSubscribeEvents(
                decoder,
                JXL_DEC_BASIC_INFO | JXL_DEC_COLOR_ENCODING | JXL_DEC_FRAME |
                    JXL_DEC_FULL_IMAGE) != JXL_DEC_SUCCESS ||
            JxlDecoderSetInput(decoder, data, size) != JXL_DEC_SUCCESS) {
            break;
        }
        JxlDecoderCloseInput(decoder);

        bool saw_basic = false;
        bool saw_color = false;
        while (true) {
            const JxlDecoderStatus status = JxlDecoderProcessInput(decoder);
            if (status == JXL_DEC_BASIC_INFO) {
                JxlBasicInfo info{};
                if (JxlDecoderGetBasicInfo(decoder, &info) != JXL_DEC_SUCCESS) {
                    result = kMalformed;
                    break;
                }
                result = ValidateBasicInfo(decoder, info, logical, reason);
                if (result != kSuccess) {
                    break;
                }
                saw_basic = true;
                continue;
            }
            if (status == JXL_DEC_COLOR_ENCODING) {
                result = ValidateColorEncoding(decoder, reason);
                if (result != kSuccess) {
                    break;
                }
                saw_color = true;
                continue;
            }
            if (status == JXL_DEC_FRAME) {
                JxlFrameHeader header{};
                if (JxlDecoderGetFrameHeader(decoder, &header) != JXL_DEC_SUCCESS) {
                    result = kMalformed;
                    break;
                }
                if (header.duration == 0) {
                    if (header.is_last == JXL_TRUE) {
                        *reason = kReasonFrameDuration;
                        result = kSharedLimitExceeded;
                        break;
                    }
                    continue;
                }
                result = AccumulateLogicalDisplayedFrame(header.duration, logical, reason);
                if (result != kSuccess) {
                    break;
                }
                continue;
            }
            if (status == JXL_DEC_NEED_IMAGE_OUT_BUFFER) {
                if (JxlDecoderSkipCurrentFrame(decoder) != JXL_DEC_SUCCESS) {
                    result = kMalformed;
                    break;
                }
                continue;
            }
            if (status == JXL_DEC_FULL_IMAGE) {
                continue;
            }
            if (status == JXL_DEC_SUCCESS) {
                const size_t remaining = JxlDecoderReleaseInput(decoder);
                result = saw_basic && saw_color && logical->frame_count > 0 && remaining == 0
                    ? kSuccess
                    : kMalformed;
                break;
            }
            if (status == JXL_DEC_NEED_MORE_INPUT || status == JXL_DEC_ERROR ||
                status == JXL_DEC_NEED_IMAGE_OUT_BUFFER || status == JXL_DEC_NEED_PREVIEW_OUT_BUFFER) {
                result = kMalformed;
                break;
            }
        }
    } while (false);

    JxlDecoderDestroy(decoder);
    return result;
}

Status RunLogicalValidationPass(
    const uint8_t* data,
    size_t size,
    LogicalInfo* logical,
    DiagnosticReason* reason) {
    JxlDecoder* decoder = JxlDecoderCreate(nullptr);
    if (decoder == nullptr) {
        *reason = kReasonDecoder;
        return kMalformed;
    }

    Status result = kMalformed;
    do {
        if (JxlDecoderSetKeepOrientation(decoder, JXL_TRUE) != JXL_DEC_SUCCESS ||
            JxlDecoderSetCoalescing(decoder, JXL_TRUE) != JXL_DEC_SUCCESS ||
            JxlDecoderSubscribeEvents(
                decoder,
                JXL_DEC_BASIC_INFO | JXL_DEC_COLOR_ENCODING | JXL_DEC_FRAME |
                    JXL_DEC_FULL_IMAGE) != JXL_DEC_SUCCESS ||
            JxlDecoderSetInput(decoder, data, size) != JXL_DEC_SUCCESS) {
            break;
        }
        JxlDecoderCloseInput(decoder);

        bool saw_basic = false;
        bool saw_color = false;
        while (true) {
            const JxlDecoderStatus status = JxlDecoderProcessInput(decoder);
            if (status == JXL_DEC_BASIC_INFO) {
                JxlBasicInfo info{};
                if (JxlDecoderGetBasicInfo(decoder, &info) != JXL_DEC_SUCCESS) {
                    result = kMalformed;
                    break;
                }
                result = ValidateBasicInfo(decoder, info, logical, reason);
                if (result != kSuccess) {
                    break;
                }
                saw_basic = true;
                continue;
            }
            if (status == JXL_DEC_COLOR_ENCODING) {
                result = ValidateColorEncoding(decoder, reason);
                if (result != kSuccess) {
                    break;
                }
                saw_color = true;
                continue;
            }
            if (status == JXL_DEC_FRAME) {
                JxlFrameHeader header{};
                if (JxlDecoderGetFrameHeader(decoder, &header) != JXL_DEC_SUCCESS) {
                    result = kMalformed;
                    break;
                }
                result = AccumulateLogicalDisplayedFrame(header.duration, logical, reason);
                if (result != kSuccess) {
                    break;
                }
                continue;
            }
            if (status == JXL_DEC_NEED_IMAGE_OUT_BUFFER) {
                const JxlPixelFormat format = {4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
                if (JxlDecoderSetImageOutCallback(decoder, &format, DiscardPixels, nullptr) != JXL_DEC_SUCCESS) {
                    result = kMalformed;
                    break;
                }
                continue;
            }
            if (status == JXL_DEC_FULL_IMAGE) {
                continue;
            }
            if (status == JXL_DEC_SUCCESS) {
                const size_t remaining = JxlDecoderReleaseInput(decoder);
                result = saw_basic && saw_color && logical->frame_count > 0 && remaining == 0
                    ? kSuccess
                    : kMalformed;
                break;
            }
            if (status == JXL_DEC_NEED_MORE_INPUT || status == JXL_DEC_ERROR ||
                status == JXL_DEC_NEED_IMAGE_OUT_BUFFER || status == JXL_DEC_NEED_PREVIEW_OUT_BUFFER) {
                result = kMalformed;
                break;
            }
        }
    } while (false);

    JxlDecoderDestroy(decoder);
    return result;
}

bool LogicalInfoMatches(const LogicalInfo& left, const LogicalInfo& right) {
    if (left.width != right.width || left.height != right.height ||
        left.total_play_count != right.total_play_count ||
        left.frame_count != right.frame_count ||
        left.submitted_pixels != right.submitted_pixels ||
        left.timeline_microseconds != right.timeline_microseconds ||
        left.preview_pixels != right.preview_pixels) {
        return false;
    }
    for (uint32_t i = 0; i < left.frame_count; ++i) {
        if (left.durations[i] != right.durations[i]) {
            return false;
        }
    }
    return true;
}

void ClearResult(uint64_t* output) {
    if (output != nullptr) {
        std::memset(output, 0, sizeof(uint64_t) * kResultSlotCount);
        output[kSlotAbiVersion] = kAbiVersion;
    }
}

void StoreResult(
    Status status,
    DiagnosticReason reason,
    const LogicalInfo& logical,
    const StructuralMetrics& metrics,
    uint64_t* output) {
    ClearResult(output);
    output[kSlotStatus] = status;
    output[kSlotDiagnosticReason] = reason;
    if (status != kSuccess) {
        return;
    }

    output[kSlotWidth] = logical.width;
    output[kSlotHeight] = logical.height;
    output[kSlotLogicalFrameCount] = logical.frame_count;
    output[kSlotTotalPlayCount] = logical.total_play_count;
    output[kSlotSubmittedCanvasPixels] = logical.submitted_pixels;
    output[kSlotBaseTimelineMicroseconds] = logical.timeline_microseconds;
    output[kSlotPublicRegularLayerCount] = metrics.layer_count;
    output[kSlotPublicRegularLayerPixels] = metrics.layer_pixels;
    output[kSlotCroppedLayerCount] = metrics.cropped_layer_count;
    output[kSlotReferenceReadEdges] = metrics.reference_read_edges;
    output[kSlotSavedReferenceCount] = metrics.saved_reference_count;
    output[kSlotBlendOperationCount] = metrics.blend_operations;
    output[kSlotMaximumReferenceChainDepth] = metrics.maximum_reference_chain_depth;
    output[kSlotPreviewPixels] = logical.preview_pixels;
    output[kSlotDurationCount] = logical.frame_count;
    for (uint32_t i = 0; i < logical.frame_count; ++i) {
        output[kSlotDurations + i] = logical.durations[i];
    }
}

bool LoadLogicalResult(const uint64_t* output, LogicalInfo* logical) {
    if (output == nullptr || logical == nullptr || output[kSlotAbiVersion] != kAbiVersion ||
        output[kSlotStatus] != kSuccess || output[kSlotDurationCount] > kMaximumLogicalFrames) {
        return false;
    }
    logical->width = static_cast<uint32_t>(output[kSlotWidth]);
    logical->height = static_cast<uint32_t>(output[kSlotHeight]);
    logical->frame_count = static_cast<uint32_t>(output[kSlotLogicalFrameCount]);
    logical->total_play_count = static_cast<uint32_t>(output[kSlotTotalPlayCount]);
    logical->submitted_pixels = output[kSlotSubmittedCanvasPixels];
    logical->timeline_microseconds = output[kSlotBaseTimelineMicroseconds];
    logical->preview_pixels = output[kSlotPreviewPixels];
    if (logical->frame_count != output[kSlotDurationCount]) {
        return false;
    }
    for (uint32_t i = 0; i < logical->frame_count; ++i) {
        logical->durations[i] = output[kSlotDurations + i];
    }
    return true;
}

Status RunLogicalHeaderPreflight(const uint8_t* data, size_t size, uint64_t* output) {
    ClearResult(output);
    if (data == nullptr || output == nullptr || size == 0) {
        if (output != nullptr) {
            output[kSlotStatus] = kMalformed;
            output[kSlotDiagnosticReason] = kReasonDecoder;
        }
        return kMalformed;
    }

    DiagnosticReason reason = kReasonNone;
    LogicalInfo logical{};
    StructuralMetrics metrics{};
    Status status = RunLogicalHeaderPass(data, size, &logical, &reason);
    StoreResult(status, reason, logical, metrics, output);
    return status;
}

Status RunStructuralHeaderPreflight(const uint8_t* data, size_t size, uint64_t* output) {
    if (data == nullptr || output == nullptr || size == 0) {
        return kMalformed;
    }
    LogicalInfo logical{};
    if (!LoadLogicalResult(output, &logical)) {
        return static_cast<Status>(output[kSlotStatus]);
    }

    DiagnosticReason reason = kReasonNone;
    StructuralMetrics metrics{};
    Status status = RunStructurePass(data, size, &metrics, &reason);
    StoreResult(status, reason, logical, metrics, output);
    return status;
}

Status RunHeaderPreflight(const uint8_t* data, size_t size, uint64_t* output) {
    Status status = RunLogicalHeaderPreflight(data, size, output);
    if (status != kSuccess) {
        return status;
    }
    return RunStructuralHeaderPreflight(data, size, output);
}

Status RunValidationPreflight(const uint8_t* data, size_t size, uint64_t* output) {
    if (data == nullptr || output == nullptr || size == 0) {
        return kMalformed;
    }
    LogicalInfo expected{};
    if (!LoadLogicalResult(output, &expected)) {
        return static_cast<Status>(output[kSlotStatus]);
    }

    DiagnosticReason reason = kReasonNone;
    LogicalInfo decoded_logical{};
    Status status = RunLogicalValidationPass(data, size, &decoded_logical, &reason);
    if (status == kSuccess && !LogicalInfoMatches(expected, decoded_logical)) {
        reason = kReasonLogicalMismatch;
        status = kMalformed;
    }
    output[kSlotStatus] = status;
    output[kSlotDiagnosticReason] = reason;
    return status;
}

struct DecodeSession {
    JxlDecoder* decoder = nullptr;
    JxlPixelFormat format{4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t frame_index = 0;
    uint64_t current_duration = 0;
    bool current_frame_started = false;
    bool done = false;
};

DecodeSession g_session;

void ResetDecodeSession() {
    if (g_session.decoder != nullptr) {
        JxlDecoderDestroy(g_session.decoder);
    }
    g_session = DecodeSession{};
}

}  // namespace

extern "C" {

uint32_t p1_abi_version() {
    return static_cast<uint32_t>(kAbiVersion);
}

uint32_t p1_result_u64_count() {
    return kResultSlotCount;
}

void* p1_alloc(uint32_t size) {
    return std::malloc(size);
}

void p1_free(void* pointer) {
    std::free(pointer);
}

uint32_t p1_preflight_logical_headers(const uint8_t* data, uint32_t size, uint64_t* output) {
    return RunLogicalHeaderPreflight(data, size, output);
}

uint32_t p1_preflight_structural_headers(const uint8_t* data, uint32_t size, uint64_t* output) {
    return RunStructuralHeaderPreflight(data, size, output);
}

uint32_t p1_preflight_headers(const uint8_t* data, uint32_t size, uint64_t* output) {
    return RunHeaderPreflight(data, size, output);
}

uint32_t p1_preflight_validate(const uint8_t* data, uint32_t size, uint64_t* output) {
    return RunValidationPreflight(data, size, output);
}

uint32_t p1_preflight(const uint8_t* data, uint32_t size, uint64_t* output) {
    Status status = RunHeaderPreflight(data, size, output);
    if (status != kSuccess) {
        return status;
    }
    return RunValidationPreflight(data, size, output);
}

uint32_t p1_decode_open(const uint8_t* data, uint32_t size, uint32_t width, uint32_t height) {
    ResetDecodeSession();
    if (data == nullptr || size == 0 || width == 0 || height == 0 ||
        width > kMaximumWidth || height > kMaximumHeight) {
        return kMalformed;
    }

    JxlDecoder* decoder = JxlDecoderCreate(nullptr);
    if (decoder == nullptr) {
        return kMalformed;
    }
    g_session.decoder = decoder;
    g_session.width = width;
    g_session.height = height;

    if (JxlDecoderSetKeepOrientation(decoder, JXL_TRUE) != JXL_DEC_SUCCESS ||
        JxlDecoderSetCoalescing(decoder, JXL_TRUE) != JXL_DEC_SUCCESS ||
        JxlDecoderSubscribeEvents(decoder, JXL_DEC_BASIC_INFO | JXL_DEC_FRAME | JXL_DEC_FULL_IMAGE) != JXL_DEC_SUCCESS ||
        JxlDecoderSetInput(decoder, data, size) != JXL_DEC_SUCCESS) {
        ResetDecodeSession();
        return kMalformed;
    }
    JxlDecoderCloseInput(decoder);
    return kSuccess;
}

// Returns 0 with one complete RGBA8 frame in output, 4 when all frames are
// consumed, or a Profile 1 validation status on failure. Duration is written in
// microseconds because the preflight already proved the exact 1,000,000 / 1
// timebase.
uint32_t p1_decode_next(uint8_t* output, uint32_t output_size, uint64_t* duration_microseconds) {
    if (g_session.decoder == nullptr || output == nullptr || duration_microseconds == nullptr) {
        return kMalformed;
    }
    if (g_session.done) {
        return 4;
    }

    uint64_t required_bytes = 0;
    if (!CheckedMultiply(g_session.width, g_session.height, &required_bytes) ||
        !CheckedMultiply(required_bytes, 4, &required_bytes) ||
        required_bytes > output_size) {
        return kSharedLimitExceeded;
    }

    while (true) {
        const JxlDecoderStatus status = JxlDecoderProcessInput(g_session.decoder);
        if (status == JXL_DEC_BASIC_INFO) {
            JxlBasicInfo info{};
            if (JxlDecoderGetBasicInfo(g_session.decoder, &info) != JXL_DEC_SUCCESS ||
                info.xsize != g_session.width || info.ysize != g_session.height) {
                return kMalformed;
            }
            continue;
        }
        if (status == JXL_DEC_FRAME) {
            JxlFrameHeader header{};
            if (JxlDecoderGetFrameHeader(g_session.decoder, &header) != JXL_DEC_SUCCESS ||
                header.duration < kMinimumFrameDurationMicroseconds) {
                return kMalformed;
            }
            g_session.current_duration = header.duration;
            g_session.current_frame_started = true;
            continue;
        }
        if (status == JXL_DEC_NEED_IMAGE_OUT_BUFFER) {
            if (!g_session.current_frame_started ||
                JxlDecoderSetImageOutBuffer(
                    g_session.decoder,
                    &g_session.format,
                    output,
                    static_cast<size_t>(required_bytes)) != JXL_DEC_SUCCESS) {
                return kMalformed;
            }
            continue;
        }
        if (status == JXL_DEC_FULL_IMAGE) {
            if (!g_session.current_frame_started) {
                return kMalformed;
            }
            *duration_microseconds = g_session.current_duration;
            g_session.current_frame_started = false;
            ++g_session.frame_index;
            return kSuccess;
        }
        if (status == JXL_DEC_SUCCESS) {
            const size_t remaining = JxlDecoderReleaseInput(g_session.decoder);
            if (remaining != 0 || g_session.current_frame_started) {
                return kMalformed;
            }
            g_session.done = true;
            return 4;
        }
        if (status == JXL_DEC_NEED_MORE_INPUT || status == JXL_DEC_ERROR ||
            status == JXL_DEC_NEED_PREVIEW_OUT_BUFFER) {
            return kMalformed;
        }
    }
}

void p1_decode_close() {
    ResetDecodeSession();
}

}

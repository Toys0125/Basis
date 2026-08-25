#include <jxl/encode.h>

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

namespace {

enum class FixtureVariant {
    kValid,
    kBits16,
    kGrayscale,
    kNoAlpha,
    kAlpha16,
    kPremultipliedAlpha,
    kOrientation,
    kNoAnimation,
    kWrongTimebase,
    kLinearSrgb,
    kWidthOver,
    kHeightOver,
    kLogicalFramesExact,
    kLogicalFramesOver,
    kSubmittedBelow,
    kSubmittedExact,
    kSubmittedOver,
    kTimelineBelow,
    kTimelineExact,
    kTimelineOver,
    kDurationBelow,
    kDurationExact,
};

bool ParseVariant(const std::string& name, FixtureVariant* variant) {
    if (name == "valid") *variant = FixtureVariant::kValid;
    else if (name == "bits16") *variant = FixtureVariant::kBits16;
    else if (name == "grayscale") *variant = FixtureVariant::kGrayscale;
    else if (name == "no-alpha") *variant = FixtureVariant::kNoAlpha;
    else if (name == "alpha16") *variant = FixtureVariant::kAlpha16;
    else if (name == "premultiplied-alpha") *variant = FixtureVariant::kPremultipliedAlpha;
    else if (name == "orientation") *variant = FixtureVariant::kOrientation;
    else if (name == "no-animation") *variant = FixtureVariant::kNoAnimation;
    else if (name == "wrong-timebase") *variant = FixtureVariant::kWrongTimebase;
    else if (name == "linear-srgb") *variant = FixtureVariant::kLinearSrgb;
    else if (name == "width-over") *variant = FixtureVariant::kWidthOver;
    else if (name == "height-over") *variant = FixtureVariant::kHeightOver;
    else if (name == "logical-frames-exact") *variant = FixtureVariant::kLogicalFramesExact;
    else if (name == "logical-frames-over") *variant = FixtureVariant::kLogicalFramesOver;
    else if (name == "submitted-below") *variant = FixtureVariant::kSubmittedBelow;
    else if (name == "submitted-exact") *variant = FixtureVariant::kSubmittedExact;
    else if (name == "submitted-over") *variant = FixtureVariant::kSubmittedOver;
    else if (name == "timeline-below") *variant = FixtureVariant::kTimelineBelow;
    else if (name == "timeline-exact") *variant = FixtureVariant::kTimelineExact;
    else if (name == "timeline-over") *variant = FixtureVariant::kTimelineOver;
    else if (name == "duration-below") *variant = FixtureVariant::kDurationBelow;
    else if (name == "duration-exact") *variant = FixtureVariant::kDurationExact;
    else return false;
    return true;
}

constexpr std::array<uint8_t, 12> kSignature = {
    0x00, 0x00, 0x00, 0x0c, 0x4a, 0x58, 0x4c, 0x20,
    0x0d, 0x0a, 0x87, 0x0a,
};
constexpr std::array<uint8_t, 20> kFtyp = {
    0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
    0x6a, 0x78, 0x6c, 0x20, 0x00, 0x00, 0x00, 0x00,
    0x6a, 0x78, 0x6c, 0x20,
};

bool Check(JxlEncoderStatus status, const char* operation) {
    if (status == JXL_ENC_SUCCESS) {
        return true;
    }
    std::fprintf(stderr, "%s failed with encoder status %d\n", operation, status);
    return false;
}

uint32_t ReadBe32(const uint8_t* p) {
    return (static_cast<uint32_t>(p[0]) << 24) |
           (static_cast<uint32_t>(p[1]) << 16) |
           (static_cast<uint32_t>(p[2]) << 8) |
           static_cast<uint32_t>(p[3]);
}

void AppendBe32(std::vector<uint8_t>* output, uint32_t value) {
    output->push_back(static_cast<uint8_t>(value >> 24));
    output->push_back(static_cast<uint8_t>(value >> 16));
    output->push_back(static_cast<uint8_t>(value >> 8));
    output->push_back(static_cast<uint8_t>(value));
}

bool CanonicalizeCodestreamBoxes(
    const std::vector<uint8_t>& encoded,
    std::vector<uint8_t>* canonical) {
    if (encoded.size() < kSignature.size() + kFtyp.size() + 8 ||
        std::memcmp(encoded.data(), kSignature.data(), kSignature.size()) != 0 ||
        std::memcmp(encoded.data() + kSignature.size(), kFtyp.data(), kFtyp.size()) != 0) {
        std::fprintf(stderr, "Encoder did not emit the expected JPEG XL container prefix.\n");
        return false;
    }

    // Stage-B negative fixtures must still pass the exact Stage-A container contract.
    // libjxl may emit a jxll level declaration for intentionally out-of-profile
    // codestreams (for example 16-bit samples). Extract only the codestream bytes and
    // rewrap them in one canonical final-marked jxlp so the intended semantic defect
    // reaches Stage B instead of being rejected by Stage A.
    const size_t first_box = kSignature.size() + kFtyp.size();
    std::vector<uint8_t> codestream;
    size_t offset = first_box;
    uint32_t expected_sequence = 0;
    bool saw_codestream = false;
    bool saw_final_jxlp = false;
    while (offset < encoded.size()) {
        if (encoded.size() - offset < 8) {
            std::fprintf(stderr, "Encoder emitted a truncated box.\n");
            return false;
        }
        const uint32_t box_size = ReadBe32(encoded.data() + offset);
        if (box_size < 8 || offset + box_size > encoded.size()) {
            std::fprintf(stderr, "Encoder emitted an invalid box size.\n");
            return false;
        }
        const uint8_t* type = encoded.data() + offset + 4;
        if (std::memcmp(type, "jxll", 4) == 0) {
            offset += box_size;
            continue;
        }
        if (std::memcmp(type, "jxlc", 4) == 0) {
            if (saw_codestream) {
                std::fprintf(stderr, "Encoder emitted multiple codestream forms.\n");
                return false;
            }
            codestream.insert(codestream.end(), encoded.begin() + offset + 8, encoded.begin() + offset + box_size);
            saw_codestream = true;
            offset += box_size;
            continue;
        }
        if (std::memcmp(type, "jxlp", 4) == 0) {
            if (box_size < 12 || saw_final_jxlp) {
                std::fprintf(stderr, "Encoder emitted an invalid jxlp sequence.\n");
                return false;
            }
            const uint32_t counter = ReadBe32(encoded.data() + offset + 8);
            if ((counter & 0x7fffffffU) != expected_sequence) {
                std::fprintf(stderr, "Encoder emitted a nonconsecutive jxlp counter.\n");
                return false;
            }
            saw_codestream = true;
            saw_final_jxlp = (counter & 0x80000000U) != 0;
            codestream.insert(codestream.end(), encoded.begin() + offset + 12, encoded.begin() + offset + box_size);
            ++expected_sequence;
            offset += box_size;
            continue;
        }
        std::fprintf(stderr, "Encoder emitted an unsupported box while preparing a Stage-B fixture.\n");
        return false;
    }
    if (!saw_codestream || codestream.empty() || codestream.size() > UINT32_MAX - 12U) {
        std::fprintf(stderr, "Encoder did not emit a bounded codestream.\n");
        return false;
    }

    canonical->clear();
    canonical->insert(canonical->end(), kSignature.begin(), kSignature.end());
    canonical->insert(canonical->end(), kFtyp.begin(), kFtyp.end());
    AppendBe32(canonical, static_cast<uint32_t>(codestream.size() + 12U));
    canonical->insert(canonical->end(), {'j', 'x', 'l', 'p'});
    AppendBe32(canonical, 0x80000000U);
    canonical->insert(canonical->end(), codestream.begin(), codestream.end());
    return true;
}

bool AddFrame(
    JxlEncoder* encoder,
    const std::vector<uint8_t>& pixels,
    uint32_t channels,
    uint32_t duration_microseconds) {
    JxlEncoderFrameSettings* settings = JxlEncoderFrameSettingsCreate(encoder, nullptr);
    if (settings == nullptr ||
        !Check(JxlEncoderSetFrameLossless(settings, JXL_TRUE), "JxlEncoderSetFrameLossless") ||
        !Check(
            JxlEncoderFrameSettingsSetOption(settings, JXL_ENC_FRAME_SETTING_EFFORT, 1),
            "JxlEncoderFrameSettingsSetOption(EFFORT)")) {
        return false;
    }

    JxlFrameHeader header{};
    JxlEncoderInitFrameHeader(&header);
    header.duration = duration_microseconds;
    if (!Check(JxlEncoderSetFrameHeader(settings, &header), "JxlEncoderSetFrameHeader")) {
        return false;
    }

    const JxlPixelFormat format = {channels, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
    return Check(
        JxlEncoderAddImageFrame(settings, &format, pixels.data(), pixels.size()),
        "JxlEncoderAddImageFrame");
}

bool EncodeFixture(FixtureVariant variant, std::vector<uint8_t>* canonical) {
    JxlEncoder* encoder = JxlEncoderCreate(nullptr);
    if (encoder == nullptr) {
        return false;
    }

    uint32_t width = 2;
    uint32_t height = 1;
    uint32_t uniform_frame_count = 0;
    uint32_t uniform_duration = 0;
    switch (variant) {
        case FixtureVariant::kWidthOver:
            width = 2049; height = 1; uniform_frame_count = 1; uniform_duration = 33'334; break;
        case FixtureVariant::kHeightOver:
            width = 1; height = 2049; uniform_frame_count = 1; uniform_duration = 33'334; break;
        case FixtureVariant::kLogicalFramesExact:
            width = 8; height = 8; uniform_frame_count = 512; uniform_duration = 33'334; break;
        case FixtureVariant::kLogicalFramesOver:
            width = 8; height = 8; uniform_frame_count = 513; uniform_duration = 33'334; break;
        case FixtureVariant::kSubmittedBelow:
            width = 257; height = 256; uniform_frame_count = 510; uniform_duration = 33'334; break;
        case FixtureVariant::kSubmittedExact:
            width = 256; height = 256; uniform_frame_count = 512; uniform_duration = 33'334; break;
        case FixtureVariant::kSubmittedOver:
            width = 257; height = 256; uniform_frame_count = 511; uniform_duration = 33'334; break;
        case FixtureVariant::kTimelineBelow:
            width = 8; height = 8; uniform_frame_count = 1; uniform_duration = 299'999'999; break;
        case FixtureVariant::kTimelineExact:
            width = 8; height = 8; uniform_frame_count = 1; uniform_duration = 300'000'000; break;
        case FixtureVariant::kTimelineOver:
            width = 8; height = 8; uniform_frame_count = 1; uniform_duration = 300'000'001; break;
        case FixtureVariant::kDurationBelow:
            width = 8; height = 8; uniform_frame_count = 1; uniform_duration = 33'333; break;
        case FixtureVariant::kDurationExact:
            width = 8; height = 8; uniform_frame_count = 1; uniform_duration = 33'334; break;
        default:
            break;
    }

    bool ok = false;
    do {
        if (!Check(JxlEncoderUseContainer(encoder, JXL_TRUE), "JxlEncoderUseContainer")) {
            break;
        }

        JxlBasicInfo info{};
        JxlEncoderInitBasicInfo(&info);
        info.xsize = width;
        info.ysize = height;
        info.bits_per_sample = variant == FixtureVariant::kBits16 ? 16 : 8;
        info.exponent_bits_per_sample = 0;
        info.uses_original_profile = JXL_TRUE;
        info.num_color_channels = variant == FixtureVariant::kGrayscale ? 1 : 3;
        info.num_extra_channels = variant == FixtureVariant::kNoAlpha ? 0 : 1;
        info.alpha_bits = variant == FixtureVariant::kNoAlpha ? 0 :
            (variant == FixtureVariant::kAlpha16 ? 16 : 8);
        info.alpha_exponent_bits = 0;
        info.alpha_premultiplied = variant == FixtureVariant::kPremultipliedAlpha ? JXL_TRUE : JXL_FALSE;
        info.orientation = variant == FixtureVariant::kOrientation ? JXL_ORIENT_ROTATE_90_CW : JXL_ORIENT_IDENTITY;
        info.have_animation = variant == FixtureVariant::kNoAnimation ? JXL_FALSE : JXL_TRUE;
        if (info.have_animation == JXL_TRUE) {
            info.animation.tps_numerator = variant == FixtureVariant::kWrongTimebase ? 1'000 : 1'000'000;
            info.animation.tps_denominator = 1;
            info.animation.num_loops = 0;
            info.animation.have_timecodes = JXL_FALSE;
        }
        if (!Check(JxlEncoderSetBasicInfo(encoder, &info), "JxlEncoderSetBasicInfo")) {
            break;
        }

        if (info.num_extra_channels == 1) {
            JxlExtraChannelInfo alpha{};
            JxlEncoderInitExtraChannelInfo(JXL_CHANNEL_ALPHA, &alpha);
            alpha.bits_per_sample = variant == FixtureVariant::kAlpha16 ? 16 : 8;
            alpha.exponent_bits_per_sample = 0;
            alpha.dim_shift = 0;
            alpha.alpha_premultiplied = variant == FixtureVariant::kPremultipliedAlpha ? JXL_TRUE : JXL_FALSE;
            if (!Check(
                    JxlEncoderSetExtraChannelInfo(encoder, 0, &alpha),
                    "JxlEncoderSetExtraChannelInfo")) {
                break;
            }
        }

        JxlColorEncoding color{};
        if (variant == FixtureVariant::kLinearSrgb) {
            JxlColorEncodingSetToLinearSRGB(&color, JXL_FALSE);
        } else {
            JxlColorEncodingSetToSRGB(&color, variant == FixtureVariant::kGrayscale ? JXL_TRUE : JXL_FALSE);
        }
        if (!Check(JxlEncoderSetColorEncoding(encoder, &color), "JxlEncoderSetColorEncoding")) {
            break;
        }

        if (uniform_frame_count != 0) {
            const uint64_t frame_bytes = static_cast<uint64_t>(width) * height * 4ULL;
            if (frame_bytes == 0 || frame_bytes > SIZE_MAX) break;
            std::vector<uint8_t> frame(static_cast<size_t>(frame_bytes), 0);
            for (size_t i = 3; i < frame.size(); i += 4) frame[i] = 255;
            bool frames_ok = true;
            for (uint32_t i = 0; i < uniform_frame_count; ++i) {
                if (!frame.empty()) frame[0] = static_cast<uint8_t>(i);
                if (!AddFrame(encoder, frame, 4, uniform_duration)) {
                    frames_ok = false;
                    break;
                }
            }
            if (!frames_ok) break;
        } else {
            uint32_t channels = 4;
            std::vector<uint8_t> frame0 = {17, 39, 201, 0, 1, 2, 3, 255};
            std::vector<uint8_t> frame1 = {4, 5, 6, 128, 7, 8, 9, 255};
            if (variant == FixtureVariant::kNoAlpha) {
                channels = 3;
                frame0 = {17, 39, 201, 1, 2, 3};
                frame1 = {4, 5, 6, 7, 8, 9};
            } else if (variant == FixtureVariant::kGrayscale) {
                channels = 2;
                frame0 = {17, 0, 1, 255};
                frame1 = {4, 128, 7, 255};
            }
            const uint32_t duration0 = variant == FixtureVariant::kNoAnimation ? 0 : 33'334;
            const uint32_t duration1 = variant == FixtureVariant::kNoAnimation ? 0 : 50'001;
            if (!AddFrame(encoder, frame0, channels, duration0) ||
                !AddFrame(encoder, frame1, channels, duration1)) {
                break;
            }
        }
        JxlEncoderCloseInput(encoder);

        std::vector<uint8_t> encoded(4096);
        uint8_t* next_out = encoded.data();
        size_t avail_out = encoded.size();
        while (true) {
            JxlEncoderStatus status = JxlEncoderProcessOutput(encoder, &next_out, &avail_out);
            if (status == JXL_ENC_SUCCESS) {
                encoded.resize(encoded.size() - avail_out);
                break;
            }
            if (status != JXL_ENC_NEED_MORE_OUTPUT) {
                std::fprintf(stderr, "JxlEncoderProcessOutput failed with status %d\n", status);
                break;
            }

            const size_t used = encoded.size() - avail_out;
            encoded.resize(encoded.size() * 2);
            next_out = encoded.data() + used;
            avail_out = encoded.size() - used;
        }

        ok = CanonicalizeCodestreamBoxes(encoded, canonical);
    } while (false);

    JxlEncoderDestroy(encoder);
    return ok;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2 || argc > 3) {
        std::fprintf(stderr, "Usage: profile1_fixture_encoder <output.jxl> [valid|bits16|grayscale|no-alpha|alpha16|premultiplied-alpha|orientation|no-animation|wrong-timebase|linear-srgb|width-over|height-over|logical-frames-exact|logical-frames-over|submitted-below|submitted-exact|submitted-over|timeline-below|timeline-exact|timeline-over|duration-below|duration-exact]\n");
        return 2;
    }

    FixtureVariant variant = FixtureVariant::kValid;
    if (argc == 3 && !ParseVariant(argv[2], &variant)) {
        std::fprintf(stderr, "Unknown fixture variant: %s\n", argv[2]);
        return 2;
    }

    std::vector<uint8_t> canonical;
    if (!EncodeFixture(variant, &canonical)) {
        return 3;
    }

    std::ofstream output(argv[1], std::ios::binary | std::ios::trunc);
    if (!output) {
        std::fprintf(stderr, "Could not open output path.\n");
        return 4;
    }
    output.write(reinterpret_cast<const char*>(canonical.data()), canonical.size());
    if (!output) {
        std::fprintf(stderr, "Could not write fixture.\n");
        return 5;
    }

    std::printf("Wrote %zu-byte canonical Profile 1 fixture.\n", canonical.size());
    return 0;
}

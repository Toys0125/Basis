#include <jxl/encode.h>

#include <array>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <vector>

#if defined(_WIN32)
#define BASIS_P1_EXPORT extern "C" __declspec(dllexport)
#else
#define BASIS_P1_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
constexpr std::array<uint8_t, 8> kMagic = {'B','P','1','G','I','F','0','1'};
constexpr std::array<uint8_t, 12> kSignature = {
    0x00, 0x00, 0x00, 0x0c, 0x4a, 0x58, 0x4c, 0x20,
    0x0d, 0x0a, 0x87, 0x0a,
};
constexpr std::array<uint8_t, 20> kFtyp = {
    0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
    0x6a, 0x78, 0x6c, 0x20, 0x00, 0x00, 0x00, 0x00,
    0x6a, 0x78, 0x6c, 0x20,
};

enum ResultCode {
  kSuccess = 0,
  kInvalidArgument = 1,
  kMalformedTimeline = 2,
  kEncodeFailure = 3,
  kAllocationFailure = 4,
};

uint32_t ReadLe32(const uint8_t* p) {
  return static_cast<uint32_t>(p[0]) |
         (static_cast<uint32_t>(p[1]) << 8) |
         (static_cast<uint32_t>(p[2]) << 16) |
         (static_cast<uint32_t>(p[3]) << 24);
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

bool Check(JxlEncoderStatus status) {
  return status == JXL_ENC_SUCCESS;
}

bool Canonicalize(const std::vector<uint8_t>& encoded, std::vector<uint8_t>* output) {
  if (encoded.size() < kSignature.size() + kFtyp.size() + 8 ||
      std::memcmp(encoded.data(), kSignature.data(), kSignature.size()) != 0 ||
      std::memcmp(encoded.data() + kSignature.size(), kFtyp.data(), kFtyp.size()) != 0) {
    return false;
  }

  const size_t first = kSignature.size() + kFtyp.size();
  const uint32_t first_size = ReadBe32(encoded.data() + first);
  if (first_size >= 8 &&
      first + first_size == encoded.size() &&
      std::memcmp(encoded.data() + first + 4, "jxlc", 4) == 0 &&
      first_size <= std::numeric_limits<uint32_t>::max() - 4U) {
    output->clear();
    output->insert(output->end(), encoded.begin(), encoded.begin() + first);
    AppendBe32(output, first_size + 4U);
    output->insert(output->end(), {'j','x','l','p'});
    AppendBe32(output, 0x80000000U);
    output->insert(output->end(), encoded.begin() + first + 8, encoded.end());
    return true;
  }

  size_t offset = first;
  uint32_t expected_sequence = 0;
  bool saw_final = false;
  std::vector<uint8_t> codestream;
  while (offset < encoded.size()) {
    if (encoded.size() - offset < 12) return false;
    const uint32_t box_size = ReadBe32(encoded.data() + offset);
    if (box_size < 12 || offset + box_size > encoded.size() ||
        std::memcmp(encoded.data() + offset + 4, "jxlp", 4) != 0) {
      return false;
    }

    const uint32_t counter = ReadBe32(encoded.data() + offset + 8);
    if ((counter & 0x7fffffffU) != expected_sequence) return false;
    const bool is_final = (counter & 0x80000000U) != 0;
    codestream.insert(
        codestream.end(),
        encoded.begin() + offset + 12,
        encoded.begin() + offset + box_size);
    offset += box_size;
    ++expected_sequence;

    if (is_final) {
      saw_final = true;
      if (offset != encoded.size()) return false;
      break;
    }
  }

  if (!saw_final || expected_sequence == 0 ||
      codestream.size() > std::numeric_limits<uint32_t>::max() - 12U) {
    return false;
  }

  output->clear();
  output->insert(output->end(), encoded.begin(), encoded.begin() + first);
  AppendBe32(output, static_cast<uint32_t>(codestream.size()) + 12U);
  output->insert(output->end(), {'j','x','l','p'});
  AppendBe32(output, 0x80000000U);
  output->insert(output->end(), codestream.begin(), codestream.end());
  return true;
}

int EncodeTimeline(const uint8_t* input, size_t input_size, std::vector<uint8_t>* canonical) {
  if (!input || !canonical || input_size < 24 || std::memcmp(input, kMagic.data(), kMagic.size()) != 0) {
    return kMalformedTimeline;
  }

  const uint32_t width = ReadLe32(input + 8);
  const uint32_t height = ReadLe32(input + 12);
  const uint32_t frames = ReadLe32(input + 16);
  const uint32_t loops = ReadLe32(input + 20);
  if (width == 0 || height == 0 || frames == 0) return kMalformedTimeline;

  const uint64_t frame_bytes64 = static_cast<uint64_t>(width) * height * 4ULL;
  const uint64_t durations_bytes64 = static_cast<uint64_t>(frames) * 4ULL;
  const uint64_t header_bytes64 = 24ULL + durations_bytes64;
  const uint64_t pixels_bytes64 = frame_bytes64 * frames;
  const uint64_t expected_size64 = header_bytes64 + pixels_bytes64;
  if (frame_bytes64 > static_cast<uint64_t>(SIZE_MAX) || expected_size64 != input_size) {
    return kMalformedTimeline;
  }

  const size_t frame_bytes = static_cast<size_t>(frame_bytes64);
  const uint8_t* durations = input + 24;
  const uint8_t* frames_base = input + static_cast<size_t>(header_bytes64);

  JxlEncoder* encoder = JxlEncoderCreate(nullptr);
  if (!encoder) return kAllocationFailure;
  int result = kEncodeFailure;
  do {
    if (!Check(JxlEncoderUseContainer(encoder, JXL_TRUE))) break;

    JxlBasicInfo info{};
    JxlEncoderInitBasicInfo(&info);
    info.xsize = width;
    info.ysize = height;
    info.bits_per_sample = 8;
    info.exponent_bits_per_sample = 0;
    info.uses_original_profile = JXL_TRUE;
    info.num_color_channels = 3;
    info.num_extra_channels = 1;
    info.alpha_bits = 8;
    info.alpha_exponent_bits = 0;
    info.alpha_premultiplied = JXL_FALSE;
    info.have_animation = JXL_TRUE;
    info.animation.tps_numerator = 1000000;
    info.animation.tps_denominator = 1;
    info.animation.num_loops = loops;
    info.animation.have_timecodes = JXL_FALSE;
    if (!Check(JxlEncoderSetBasicInfo(encoder, &info))) break;

    JxlExtraChannelInfo alpha{};
    JxlEncoderInitExtraChannelInfo(JXL_CHANNEL_ALPHA, &alpha);
    alpha.bits_per_sample = 8;
    alpha.exponent_bits_per_sample = 0;
    alpha.alpha_premultiplied = JXL_FALSE;
    if (!Check(JxlEncoderSetExtraChannelInfo(encoder, 0, &alpha))) break;

    JxlColorEncoding color{};
    JxlColorEncodingSetToSRGB(&color, JXL_FALSE);
    if (!Check(JxlEncoderSetColorEncoding(encoder, &color))) break;

    const JxlPixelFormat format = {4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
    bool frames_ok = true;
    for (uint32_t i = 0; i < frames; ++i) {
      const uint32_t duration = ReadLe32(durations + static_cast<size_t>(i) * 4U);
      if (duration == 0) { frames_ok = false; break; }
      JxlEncoderFrameSettings* settings = JxlEncoderFrameSettingsCreate(encoder, nullptr);
      if (!settings ||
          !Check(JxlEncoderSetFrameLossless(settings, JXL_TRUE)) ||
          !Check(JxlEncoderFrameSettingsSetOption(settings, JXL_ENC_FRAME_SETTING_EFFORT, 1))) {
        frames_ok = false;
        break;
      }
      JxlFrameHeader header{};
      JxlEncoderInitFrameHeader(&header);
      header.duration = duration;
      const uint8_t* rgba = frames_base + static_cast<size_t>(i) * frame_bytes;
      if (!Check(JxlEncoderSetFrameHeader(settings, &header)) ||
          !Check(JxlEncoderAddImageFrame(settings, &format, rgba, frame_bytes))) {
        frames_ok = false;
        break;
      }
    }
    if (!frames_ok) break;

    JxlEncoderCloseInput(encoder);
    std::vector<uint8_t> encoded(4096);
    uint8_t* next = encoded.data();
    size_t available = encoded.size();
    while (true) {
      JxlEncoderStatus status = JxlEncoderProcessOutput(encoder, &next, &available);
      if (status == JXL_ENC_SUCCESS) {
        encoded.resize(encoded.size() - available);
        break;
      }
      if (status != JXL_ENC_NEED_MORE_OUTPUT) {
        frames_ok = false;
        break;
      }
      const size_t used = encoded.size() - available;
      if (encoded.size() > std::numeric_limits<size_t>::max() / 2U) {
        frames_ok = false;
        break;
      }
      encoded.resize(encoded.size() * 2U);
      next = encoded.data() + used;
      available = encoded.size() - used;
    }
    if (!frames_ok || !Canonicalize(encoded, canonical)) break;
    result = kSuccess;
  } while (false);

  JxlEncoderDestroy(encoder);
  return result;
}
}  // namespace

BASIS_P1_EXPORT uint32_t basis_profile1_editor_abi_version() {
  return 1U;
}

BASIS_P1_EXPORT int basis_profile1_editor_encode_timeline(
    const uint8_t* input,
    size_t input_size,
    uint8_t** output,
    size_t* output_size) {
  if (!input || input_size == 0 || !output || !output_size) return kInvalidArgument;
  *output = nullptr;
  *output_size = 0;

  std::vector<uint8_t> canonical;
  const int result = EncodeTimeline(input, input_size, &canonical);
  if (result != kSuccess) return result;

  void* memory = std::malloc(canonical.size());
  if (!memory) return kAllocationFailure;
  std::memcpy(memory, canonical.data(), canonical.size());
  *output = static_cast<uint8_t*>(memory);
  *output_size = canonical.size();
  return kSuccess;
}

BASIS_P1_EXPORT void basis_profile1_editor_free(void* memory) {
  std::free(memory);
}

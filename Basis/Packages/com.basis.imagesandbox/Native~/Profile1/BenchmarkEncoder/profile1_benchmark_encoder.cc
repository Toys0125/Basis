#include <jxl/encode.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

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

uint32_t ReadLe32(std::istream& input) {
  uint8_t bytes[4];
  input.read(reinterpret_cast<char*>(bytes), sizeof(bytes));
  return static_cast<uint32_t>(bytes[0]) |
         (static_cast<uint32_t>(bytes[1]) << 8) |
         (static_cast<uint32_t>(bytes[2]) << 16) |
         (static_cast<uint32_t>(bytes[3]) << 24);
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

bool Check(JxlEncoderStatus status, const char* operation) {
  if (status == JXL_ENC_SUCCESS) return true;
  std::cerr << operation << " failed with encoder status " << status << "\n";
  return false;
}

bool Canonicalize(const std::vector<uint8_t>& encoded, std::vector<uint8_t>* output) {
  if (encoded.size() < kSignature.size() + kFtyp.size() + 8 ||
      std::memcmp(encoded.data(), kSignature.data(), kSignature.size()) != 0 ||
      std::memcmp(encoded.data() + kSignature.size(), kFtyp.data(), kFtyp.size()) != 0) {
    std::cerr << "libjxl did not emit the expected JPEG XL container prefix\n";
    return false;
  }
  const size_t first = kSignature.size() + kFtyp.size();
  const uint32_t size = ReadBe32(encoded.data() + first);
  if (size < 8 || first + size != encoded.size() ||
      std::memcmp(encoded.data() + first + 4, "jxlc", 4) != 0) {
    std::cerr << "benchmark encoder expected one jxlc output box\n";
    return false;
  }
  output->clear();
  output->insert(output->end(), encoded.begin(), encoded.begin() + first);
  AppendBe32(output, size + 4);
  output->insert(output->end(), {'j','x','l','p'});
  AppendBe32(output, 0x80000000U);
  output->insert(output->end(), encoded.begin() + first + 8, encoded.end());
  return true;
}

bool EncodeOne(const fs::path& input_path, const fs::path& output_path) {
  std::ifstream input(input_path, std::ios::binary);
  if (!input) return false;
  std::array<uint8_t, 8> magic{};
  input.read(reinterpret_cast<char*>(magic.data()), magic.size());
  if (!input || magic != kMagic) {
    std::cerr << input_path << ": invalid benchmark timeline magic\n";
    return false;
  }
  const uint32_t width = ReadLe32(input);
  const uint32_t height = ReadLe32(input);
  const uint32_t frames = ReadLe32(input);
  const uint32_t loops = ReadLe32(input);
  if (!input || width == 0 || height == 0 || frames == 0) return false;
  const uint64_t frame_bytes64 = static_cast<uint64_t>(width) * height * 4;
  if (frame_bytes64 > static_cast<uint64_t>(SIZE_MAX)) return false;
  const size_t frame_bytes = static_cast<size_t>(frame_bytes64);

  std::vector<uint32_t> durations(frames);
  for (uint32_t i = 0; i < frames; ++i) durations[i] = ReadLe32(input);
  if (!input) return false;

  JxlEncoder* encoder = JxlEncoderCreate(nullptr);
  if (!encoder) return false;
  bool ok = false;
  do {
    if (!Check(JxlEncoderUseContainer(encoder, JXL_TRUE), "JxlEncoderUseContainer")) break;
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
    if (!Check(JxlEncoderSetBasicInfo(encoder, &info), "JxlEncoderSetBasicInfo")) break;

    JxlExtraChannelInfo alpha{};
    JxlEncoderInitExtraChannelInfo(JXL_CHANNEL_ALPHA, &alpha);
    alpha.bits_per_sample = 8;
    alpha.exponent_bits_per_sample = 0;
    alpha.alpha_premultiplied = JXL_FALSE;
    if (!Check(JxlEncoderSetExtraChannelInfo(encoder, 0, &alpha), "JxlEncoderSetExtraChannelInfo")) break;

    JxlColorEncoding color{};
    JxlColorEncodingSetToSRGB(&color, JXL_FALSE);
    if (!Check(JxlEncoderSetColorEncoding(encoder, &color), "JxlEncoderSetColorEncoding")) break;

    const JxlPixelFormat format = {4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
    std::vector<uint8_t> rgba(frame_bytes);
    bool frames_ok = true;
    for (uint32_t i = 0; i < frames; ++i) {
      input.read(reinterpret_cast<char*>(rgba.data()), rgba.size());
      if (!input) { frames_ok = false; break; }
      JxlEncoderFrameSettings* settings = JxlEncoderFrameSettingsCreate(encoder, nullptr);
      if (!settings ||
          !Check(JxlEncoderSetFrameLossless(settings, JXL_TRUE), "JxlEncoderSetFrameLossless") ||
          !Check(JxlEncoderFrameSettingsSetOption(settings, JXL_ENC_FRAME_SETTING_EFFORT, 1), "JxlEncoder effort")) {
        frames_ok = false;
        break;
      }
      JxlFrameHeader header{};
      JxlEncoderInitFrameHeader(&header);
      header.duration = durations[i];
      if (!Check(JxlEncoderSetFrameHeader(settings, &header), "JxlEncoderSetFrameHeader") ||
          !Check(JxlEncoderAddImageFrame(settings, &format, rgba.data(), rgba.size()), "JxlEncoderAddImageFrame")) {
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
      if (status != JXL_ENC_NEED_MORE_OUTPUT) { frames_ok = false; break; }
      const size_t used = encoded.size() - available;
      encoded.resize(encoded.size() * 2);
      next = encoded.data() + used;
      available = encoded.size() - used;
    }
    if (!frames_ok) break;

    std::vector<uint8_t> canonical;
    if (!Canonicalize(encoded, &canonical)) break;
    std::ofstream output(output_path, std::ios::binary | std::ios::trunc);
    output.write(reinterpret_cast<const char*>(canonical.data()), canonical.size());
    ok = static_cast<bool>(output);
  } while (false);
  JxlEncoderDestroy(encoder);
  return ok;
}
}  // namespace

int main(int argc, char** argv) {
  if (argc != 3) {
    std::cerr << "Usage: profile1_benchmark_encoder <input-dir> <output-dir>\n";
    return 2;
  }
  const fs::path input_dir(argv[1]);
  const fs::path output_dir(argv[2]);
  fs::create_directories(output_dir);
  int failed = 0;
  for (const auto& entry : fs::directory_iterator(input_dir)) {
    if (!entry.is_regular_file() || entry.path().extension() != ".bp1gif") continue;
    fs::path output = output_dir / entry.path().filename();
    output.replace_extension(".jxl");
    if (!EncodeOne(entry.path(), output)) {
      std::cerr << "Failed to encode " << entry.path() << "\n";
      ++failed;
    } else {
      std::cout << "Encoded " << entry.path().filename() << " -> " << output.filename() << "\n";
    }
  }
  return failed == 0 ? 0 : 3;
}

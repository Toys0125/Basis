#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <string>
#include <vector>

extern "C" {
uint32_t p1_result_u64_count();
uint32_t p1_preflight(const uint8_t* data, uint32_t size, uint64_t* output);
uint32_t p1_decode_open(const uint8_t* data, uint32_t size, uint32_t width, uint32_t height);
uint32_t p1_decode_next(uint8_t* output, uint32_t output_size, uint64_t* duration_microseconds);
void p1_decode_close();
}

namespace {

bool ReadFile(const char* path, std::vector<uint8_t>* bytes) {
  std::ifstream input(path, std::ios::binary);
  if (!input) return false;
  input.seekg(0, std::ios::end);
  const std::streamoff size = input.tellg();
  if (size <= 0 || static_cast<uint64_t>(size) > std::numeric_limits<uint32_t>::max()) return false;
  input.seekg(0, std::ios::beg);
  bytes->resize(static_cast<size_t>(size));
  input.read(reinterpret_cast<char*>(bytes->data()), size);
  return static_cast<bool>(input);
}

void PrintHex(const std::vector<uint8_t>& bytes) {
  static constexpr char kHex[] = "0123456789abcdef";
  for (uint8_t value : bytes) {
    std::cout << kHex[value >> 4] << kHex[value & 0x0f];
  }
}

}  // namespace

int main(int argc, char** argv) {
  if (argc < 2 || argc > 3) {
    std::cerr << "Usage: profile1_native_oracle <canonical-profile1.jxl> [--preflight-only]\n";
    return 2;
  }
  const bool preflight_only = argc == 3 && std::string(argv[2]) == "--preflight-only";
  if (argc == 3 && !preflight_only) {
    std::cerr << "Unknown option: " << argv[2] << '\n';
    return 2;
  }

  std::vector<uint8_t> payload;
  if (!ReadFile(argv[1], &payload)) {
    std::cerr << "Could not read input payload.\n";
    return 3;
  }

  const uint32_t slots = p1_result_u64_count();
  if (slots == 0 || slots > 4096) {
    std::cerr << "Native decoder returned an invalid result-slot count.\n";
    return 4;
  }
  std::vector<uint64_t> result(slots);
  const uint32_t status = p1_preflight(
      payload.data(), static_cast<uint32_t>(payload.size()), result.data());

  std::cout << "RESULT " << status << ' ' << slots;
  for (uint64_t value : result) std::cout << ' ' << value;
  std::cout << '\n';

  if (status != 0 || preflight_only) return 0;
  if (slots < 17 || result[2] == 0 || result[3] == 0 ||
      result[2] > std::numeric_limits<uint32_t>::max() ||
      result[3] > std::numeric_limits<uint32_t>::max()) {
    std::cerr << "Successful native preflight returned an invalid envelope.\n";
    return 5;
  }

  const uint64_t frame_bytes64 = result[2] * result[3] * 4ULL;
  if (frame_bytes64 == 0 || frame_bytes64 > std::numeric_limits<uint32_t>::max()) {
    std::cerr << "Native decoded frame size is invalid.\n";
    return 6;
  }
  std::vector<uint8_t> frame(static_cast<size_t>(frame_bytes64));
  uint32_t decode_status = p1_decode_open(
      payload.data(), static_cast<uint32_t>(payload.size()),
      static_cast<uint32_t>(result[2]), static_cast<uint32_t>(result[3]));
  if (decode_status != 0) {
    std::cerr << "Native decode_open failed with status " << decode_status << ".\n";
    return 7;
  }

  uint32_t frame_index = 0;
  while (true) {
    uint64_t duration = 0;
    decode_status = p1_decode_next(
        frame.data(), static_cast<uint32_t>(frame.size()), &duration);
    if (decode_status == 4) break;
    if (decode_status != 0) {
      p1_decode_close();
      std::cerr << "Native decode_next failed with status " << decode_status << ".\n";
      return 8;
    }
    std::cout << "FRAME " << frame_index++ << ' ' << duration << ' ';
    PrintHex(frame);
    std::cout << '\n';
  }
  p1_decode_close();
  std::cout << "END " << frame_index << '\n';
  return 0;
}

#include <jxl/cms.h>
#include <jxl/color_encoding.h>
#include <jxl/decode.h>
#include <jxl/encode.h>
#include <jxl/memory_manager.h>

#include "lib/extras/codec_in_out.h"
#include "lib/jxl/base/span.h"
#include "lib/jxl/enc_aux_out.h"
#include "lib/jxl/enc_bit_writer.h"
#include "lib/jxl/enc_external_image.h"
#include "lib/jxl/enc_fields.h"
#include "lib/jxl/enc_frame.h"
#include "lib/jxl/enc_icc_codec.h"
#include "lib/jxl/memory_manager_internal.h"
#include "lib/jxl/padded_bytes.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <numeric>
#include <vector>

#if defined(_WIN32)
#define BASIS_P1_EXPORT extern "C" __declspec(dllexport)
#else
#define BASIS_P1_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
constexpr std::array<uint8_t, 8> kTimelineMagic = {'B','P','1','G','I','F','0','1'};
constexpr uint32_t kMinimumDurationUs = 33'334;
constexpr uint64_t kMaximumTimelineBytes = 2ULL * 1024ULL * 1024ULL * 1024ULL;

enum ToolResult {
  kToolSuccess = 0,
  kToolInvalidArgument = 1,
  kToolDecodeFailure = 2,
  kToolTimingUnrepresentable = 3,
  kToolEncodeFailure = 4,
  kToolAllocationFailure = 5,
};

enum SyntheticFixtureKind : uint32_t {
  kSyntheticCrop = 0,
  kSyntheticBlendPrevious = 1,
  kSyntheticSavedReference = 2,
  kSyntheticReferenceChain = 3,
  kSyntheticZeroDurationLayers = 4,
  kSyntheticCropBlendReference = 5,
  kSyntheticStructuralStress = 6,
  kSyntheticWidthBelow = 7,
  kSyntheticWidthAt = 8,
  kSyntheticWidthAbove = 9,
  kSyntheticFramesBelow = 10,
  kSyntheticFramesAt = 11,
  kSyntheticFramesAbove = 12,
  kSyntheticSubmittedBelow = 13,
  kSyntheticSubmittedExact = 14,
  kSyntheticSubmittedAbove = 15,
  kSyntheticTimelineBelow = 16,
  kSyntheticTimelineAt = 17,
  kSyntheticTimelineAbove = 18,
  kSyntheticDurationBelow = 19,
  kSyntheticDurationAt = 20,
  kSyntheticDurationAbove = 21,
  kSyntheticPreview = 22,
  kSyntheticCanvasBelow = 23,
  kSyntheticCanvasExact = 24,
};

void AppendLe32(std::vector<uint8_t>* output, uint32_t value) {
  output->push_back(static_cast<uint8_t>(value));
  output->push_back(static_cast<uint8_t>(value >> 8));
  output->push_back(static_cast<uint8_t>(value >> 16));
  output->push_back(static_cast<uint8_t>(value >> 24));
}

bool CheckedMultiply(uint64_t a, uint64_t b, uint64_t* value) {
  if (a != 0 && b > std::numeric_limits<uint64_t>::max() / a) return false;
  *value = a * b;
  return true;
}

bool ConvertTicksToMicroseconds(
    uint32_t ticks,
    uint32_t tps_numerator,
    uint32_t tps_denominator,
    uint32_t* microseconds) {
  if (tps_numerator == 0 || tps_denominator == 0 || microseconds == nullptr) return false;
  uint64_t value = ticks;
  uint64_t factor = static_cast<uint64_t>(tps_denominator) * 1'000'000ULL;
  uint64_t divisor = tps_numerator;
  uint64_t g = std::gcd(value, divisor);
  value /= g;
  divisor /= g;
  g = std::gcd(factor, divisor);
  factor /= g;
  divisor /= g;
  if (divisor != 1 || value > std::numeric_limits<uint64_t>::max() / factor) return false;
  uint64_t result = value * factor;
  if (result == 0 || result > std::numeric_limits<uint32_t>::max()) return false;
  *microseconds = static_cast<uint32_t>(result);
  return true;
}

int DecodeJxlTimeline(const uint8_t* input, size_t input_size, std::vector<uint8_t>* timeline) {
  if (input == nullptr || input_size == 0 || timeline == nullptr) return kToolInvalidArgument;

  JxlDecoder* decoder = JxlDecoderCreate(nullptr);
  if (decoder == nullptr) return kToolAllocationFailure;

  int result = kToolDecodeFailure;
  JxlBasicInfo basic{};
  bool have_basic = false;
  bool have_color = false;
  uint32_t current_duration_us = kMinimumDurationUs;
  std::vector<uint32_t> durations;
  std::vector<uint8_t> frames;
  std::vector<uint8_t> frame_buffer;
  const JxlPixelFormat format = {4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};

  do {
    const JxlCmsInterface* cms = JxlGetDefaultCms();
    if (cms == nullptr || JxlDecoderSetCms(decoder, *cms) != JXL_DEC_SUCCESS ||
        JxlDecoderSetKeepOrientation(decoder, JXL_FALSE) != JXL_DEC_SUCCESS ||
        JxlDecoderSetUnpremultiplyAlpha(decoder, JXL_TRUE) != JXL_DEC_SUCCESS ||
        JxlDecoderSetCoalescing(decoder, JXL_TRUE) != JXL_DEC_SUCCESS ||
        JxlDecoderSubscribeEvents(
            decoder,
            JXL_DEC_BASIC_INFO | JXL_DEC_COLOR_ENCODING | JXL_DEC_FRAME | JXL_DEC_FULL_IMAGE) != JXL_DEC_SUCCESS ||
        JxlDecoderSetInput(decoder, input, input_size) != JXL_DEC_SUCCESS) {
      break;
    }
    JxlDecoderCloseInput(decoder);

    while (true) {
      JxlDecoderStatus status = JxlDecoderProcessInput(decoder);
      if (status == JXL_DEC_BASIC_INFO) {
        if (JxlDecoderGetBasicInfo(decoder, &basic) != JXL_DEC_SUCCESS || basic.xsize == 0 || basic.ysize == 0) break;
        uint64_t frame_bytes = 0;
        if (!CheckedMultiply(basic.xsize, basic.ysize, &frame_bytes) ||
            !CheckedMultiply(frame_bytes, 4, &frame_bytes) || frame_bytes > SIZE_MAX) break;
        frame_buffer.resize(static_cast<size_t>(frame_bytes));
        have_basic = true;
        continue;
      }
      if (status == JXL_DEC_COLOR_ENCODING) {
        JxlColorEncoding srgb{};
        JxlColorEncodingSetToSRGB(&srgb, JXL_FALSE);
        if (JxlDecoderSetOutputColorProfile(decoder, &srgb, nullptr, 0) != JXL_DEC_SUCCESS) break;
        have_color = true;
        continue;
      }
      if (status == JXL_DEC_FRAME) {
        JxlFrameHeader header{};
        if (JxlDecoderGetFrameHeader(decoder, &header) != JXL_DEC_SUCCESS) break;
        if (basic.have_animation == JXL_TRUE) {
          if (!ConvertTicksToMicroseconds(
                  header.duration,
                  basic.animation.tps_numerator,
                  basic.animation.tps_denominator,
                  &current_duration_us)) {
            result = kToolTimingUnrepresentable;
            break;
          }
        } else {
          current_duration_us = kMinimumDurationUs;
        }
        continue;
      }
      if (status == JXL_DEC_NEED_IMAGE_OUT_BUFFER) {
        if (!have_basic || frame_buffer.empty() ||
            JxlDecoderSetImageOutBuffer(decoder, &format, frame_buffer.data(), frame_buffer.size()) != JXL_DEC_SUCCESS) {
          break;
        }
        continue;
      }
      if (status == JXL_DEC_FULL_IMAGE) {
        if (frame_buffer.empty()) break;
        uint64_t new_size = static_cast<uint64_t>(frames.size()) + frame_buffer.size();
        if (new_size > kMaximumTimelineBytes || new_size > SIZE_MAX) {
          result = kToolAllocationFailure;
          break;
        }
        durations.push_back(current_duration_us);
        frames.insert(frames.end(), frame_buffer.begin(), frame_buffer.end());
        continue;
      }
      if (status == JXL_DEC_SUCCESS) {
        if (!have_basic || !have_color || durations.empty() || JxlDecoderReleaseInput(decoder) != 0) break;
        if (durations.size() > std::numeric_limits<uint32_t>::max()) break;
        uint64_t expected_pixels = 0;
        if (!CheckedMultiply(basic.xsize, basic.ysize, &expected_pixels) ||
            !CheckedMultiply(expected_pixels, 4, &expected_pixels) ||
            !CheckedMultiply(expected_pixels, durations.size(), &expected_pixels) ||
            expected_pixels != frames.size()) break;

        uint64_t total_size = 24ULL + durations.size() * 4ULL + frames.size();
        if (total_size > kMaximumTimelineBytes || total_size > SIZE_MAX) {
          result = kToolAllocationFailure;
          break;
        }
        timeline->clear();
        timeline->reserve(static_cast<size_t>(total_size));
        timeline->insert(timeline->end(), kTimelineMagic.begin(), kTimelineMagic.end());
        AppendLe32(timeline, basic.xsize);
        AppendLe32(timeline, basic.ysize);
        AppendLe32(timeline, static_cast<uint32_t>(durations.size()));
        AppendLe32(timeline, basic.have_animation == JXL_TRUE ? basic.animation.num_loops : 0U);
        for (uint32_t duration : durations) AppendLe32(timeline, duration);
        timeline->insert(timeline->end(), frames.begin(), frames.end());
        result = kToolSuccess;
        break;
      }
      if (status == JXL_DEC_NEED_MORE_INPUT || status == JXL_DEC_ERROR ||
          status == JXL_DEC_NEED_PREVIEW_OUT_BUFFER) {
        break;
      }
    }
  } while (false);

  JxlDecoderDestroy(decoder);
  return result;
}

bool EncoderOk(JxlEncoderStatus status) { return status == JXL_ENC_SUCCESS; }

bool ConfigureProfile1Encoder(JxlEncoder* encoder, uint32_t width, uint32_t height, uint32_t loops) {
  if (!EncoderOk(JxlEncoderUseContainer(encoder, JXL_TRUE))) return false;
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
  info.animation.tps_numerator = 1'000'000;
  info.animation.tps_denominator = 1;
  info.animation.num_loops = loops;
  info.animation.have_timecodes = JXL_FALSE;
  if (!EncoderOk(JxlEncoderSetBasicInfo(encoder, &info))) return false;

  JxlExtraChannelInfo alpha{};
  JxlEncoderInitExtraChannelInfo(JXL_CHANNEL_ALPHA, &alpha);
  alpha.bits_per_sample = 8;
  alpha.exponent_bits_per_sample = 0;
  alpha.alpha_premultiplied = JXL_FALSE;
  if (!EncoderOk(JxlEncoderSetExtraChannelInfo(encoder, 0, &alpha))) return false;

  JxlColorEncoding color{};
  JxlColorEncodingSetToSRGB(&color, JXL_FALSE);
  return EncoderOk(JxlEncoderSetColorEncoding(encoder, &color));
}

struct FrameSpec {
  uint32_t duration = kMinimumDurationUs;
  bool crop = false;
  int32_t x = 0;
  int32_t y = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  JxlBlendMode blend = JXL_BLEND_REPLACE;
  uint32_t source = 0;
  uint32_t save_reference = 0;
  uint8_t value = 0;
};

bool AddSyntheticFrame(
    JxlEncoder* encoder,
    uint32_t canvas_width,
    uint32_t canvas_height,
    const FrameSpec& spec) {
  uint32_t width = spec.crop ? spec.width : canvas_width;
  uint32_t height = spec.crop ? spec.height : canvas_height;
  if (width == 0 || height == 0) return false;
  uint64_t bytes64 = static_cast<uint64_t>(width) * height * 4ULL;
  if (bytes64 > SIZE_MAX) return false;
  std::vector<uint8_t> rgba(static_cast<size_t>(bytes64), spec.value);
  for (size_t i = 3; i < rgba.size(); i += 4) rgba[i] = 255;

  JxlEncoderFrameSettings* settings = JxlEncoderFrameSettingsCreate(encoder, nullptr);
  if (settings == nullptr ||
      !EncoderOk(JxlEncoderSetFrameLossless(settings, JXL_TRUE)) ||
      !EncoderOk(JxlEncoderFrameSettingsSetOption(settings, JXL_ENC_FRAME_SETTING_EFFORT, 1))) {
    return false;
  }

  JxlFrameHeader header{};
  JxlEncoderInitFrameHeader(&header);
  header.duration = spec.duration;
  header.layer_info.have_crop = spec.crop ? JXL_TRUE : JXL_FALSE;
  header.layer_info.crop_x0 = spec.x;
  header.layer_info.crop_y0 = spec.y;
  header.layer_info.xsize = width;
  header.layer_info.ysize = height;
  header.layer_info.blend_info.blendmode = spec.blend;
  header.layer_info.blend_info.source = spec.source;
  header.layer_info.blend_info.alpha = 0;
  header.layer_info.blend_info.clamp = JXL_FALSE;
  header.layer_info.save_as_reference = spec.save_reference;
  if (!EncoderOk(JxlEncoderSetFrameHeader(settings, &header))) return false;
  if (spec.blend != JXL_BLEND_REPLACE &&
      !EncoderOk(JxlEncoderSetExtraChannelBlendInfo(settings, 0, &header.layer_info.blend_info))) {
    return false;
  }

  const JxlPixelFormat format = {4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
  return EncoderOk(JxlEncoderAddImageFrame(settings, &format, rgba.data(), rgba.size()));
}

bool FinishEncoder(JxlEncoder* encoder, std::vector<uint8_t>* output) {
  JxlEncoderCloseInput(encoder);
  output->assign(4096, 0);
  uint8_t* next = output->data();
  size_t available = output->size();
  while (true) {
    JxlEncoderStatus status = JxlEncoderProcessOutput(encoder, &next, &available);
    if (status == JXL_ENC_SUCCESS) {
      output->resize(output->size() - available);
      return true;
    }
    if (status != JXL_ENC_NEED_MORE_OUTPUT) return false;
    size_t used = output->size() - available;
    if (output->size() > std::numeric_limits<size_t>::max() / 2U) return false;
    output->resize(output->size() * 2U);
    next = output->data() + used;
    available = output->size() - used;
  }
}

bool AddRepeatedFrames(
    JxlEncoder* encoder,
    uint32_t width,
    uint32_t height,
    uint32_t count,
    uint32_t duration) {
  for (uint32_t i = 0; i < count; ++i) {
    FrameSpec frame{};
    frame.duration = duration;
    frame.value = static_cast<uint8_t>(i);
    if (!AddSyntheticFrame(encoder, width, height, frame)) return false;
  }
  return true;
}

bool EncodeInternalPreviewFixture(std::vector<uint8_t>* output) {
  if (output == nullptr) return false;

  JxlMemoryManager memory_manager{};
  if (!jxl::MemoryManagerInit(&memory_manager, nullptr)) return false;
  jxl::CodecInOut io(&memory_manager);
  if (!io.SetSize(8, 8)) return false;
  io.metadata.m.SetUintSamples(8);
  io.metadata.m.SetAlphaBits(8, false);
  io.metadata.m.color_encoding = jxl::ColorEncoding::SRGB(false);
  if (!io.metadata.m.color_encoding.CreateICC()) return false;
  io.metadata.m.xyb_encoded = false;
  io.metadata.m.have_animation = true;
  io.metadata.m.animation.tps_numerator = 1'000'000;
  io.metadata.m.animation.tps_denominator = 1;
  io.metadata.m.animation.num_loops = 0;
  io.metadata.m.animation.have_timecodes = false;
  io.metadata.m.have_preview = true;
  if (!io.metadata.m.preview_size.Set(2, 2)) return false;

  const JxlPixelFormat format = {4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
  std::vector<uint8_t> main_pixels(8U * 8U * 4U);
  for (size_t i = 0; i < main_pixels.size(); i += 4) {
    main_pixels[i + 0] = static_cast<uint8_t>((i / 4U) * 3U);
    main_pixels[i + 1] = static_cast<uint8_t>(255U - ((i / 4U) * 2U));
    main_pixels[i + 2] = 96;
    main_pixels[i + 3] = 255;
  }
  io.frames.clear();
  io.frames.emplace_back(&memory_manager, &io.metadata.m);
  if (!jxl::ConvertFromExternal(
          jxl::Bytes(main_pixels.data(), main_pixels.size()),
          8,
          8,
          jxl::ColorEncoding::SRGB(false),
          8,
          format,
          nullptr,
          &io.frames[0],
          true)) {
    return false;
  }
  io.frames[0].duration = kMinimumDurationUs;

  std::vector<uint8_t> preview_pixels(2U * 2U * 4U);
  for (size_t i = 0; i < preview_pixels.size(); i += 4) {
    preview_pixels[i + 0] = static_cast<uint8_t>(32U + i * 5U);
    preview_pixels[i + 1] = 160;
    preview_pixels[i + 2] = 224;
    preview_pixels[i + 3] = 255;
  }
  if (!jxl::ConvertFromExternal(
          jxl::Bytes(preview_pixels.data(), preview_pixels.size()),
          2,
          2,
          jxl::ColorEncoding::SRGB(false),
          8,
          format,
          nullptr,
          &io.preview_frame,
          true)) {
    return false;
  }
  if (!io.CheckMetadata()) return false;

  jxl::CompressParams cparams;
  cparams.SetLossless();
  cparams.speed_tier = jxl::SpeedTier::kThunder;
  const JxlCmsInterface* cms = JxlGetDefaultCms();
  if (cms == nullptr) return false;
  cparams.SetCms(*cms);
  if (!jxl::ParamsPostInit(&cparams)) return false;

  jxl::CodecMetadata metadata = io.metadata;
  if (!metadata.size.Set(io.xsize(), io.ysize())) return false;
  metadata.m.xyb_encoded = false;

  jxl::BitWriter writer(&memory_manager);
  if (!jxl::WriteCodestreamHeaders(&metadata, &writer, nullptr)) return false;
  if (metadata.m.color_encoding.WantICC()) {
    if (!jxl::WriteICC(
            jxl::Bytes(metadata.m.color_encoding.ICC()),
            &writer,
            jxl::LayerType::Header,
            nullptr)) {
      return false;
    }
  }

  jxl::AuxOut preview_aux;
  jxl::FrameInfo preview_info;
  preview_info.is_preview = true;
  if (!jxl::EncodeFrame(
          &memory_manager,
          cparams,
          preview_info,
          &metadata,
          io.preview_frame,
          *cms,
          nullptr,
          &writer,
          &preview_aux)) {
    return false;
  }
  writer.ZeroPadToByte();

  jxl::AuxOut frame_aux;
  jxl::FrameInfo frame_info;
  frame_info.is_last = true;
  if (!jxl::EncodeFrame(
          &memory_manager,
          cparams,
          frame_info,
          &metadata,
          io.frames[0],
          *cms,
          nullptr,
          &writer,
          &frame_aux)) {
    return false;
  }

  jxl::PaddedBytes encoded = std::move(writer).TakeBytes();
  output->clear();
  jxl::Bytes(encoded).AppendTo(*output);
  return !output->empty();
}

int EncodeSynthetic(uint32_t kind, std::vector<uint8_t>* output) {
  if (output == nullptr) return kToolInvalidArgument;
  if (kind == kSyntheticPreview) {
    return EncodeInternalPreviewFixture(output) ? kToolSuccess : kToolEncodeFailure;
  }
  uint32_t width = 8;
  uint32_t height = 8;
  uint32_t loops = 0;
  JxlEncoder* encoder = JxlEncoderCreate(nullptr);
  if (encoder == nullptr) return kToolAllocationFailure;
  bool ok = true;

  switch (kind) {
    case kSyntheticWidthBelow: width = 2047; height = 1; break;
    case kSyntheticWidthAt: width = 2048; height = 1; break;
    case kSyntheticWidthAbove: width = 2049; height = 1; break;
    case kSyntheticSubmittedBelow: width = 257; height = 256; break;
    case kSyntheticSubmittedExact: width = 256; height = 256; break;
    case kSyntheticSubmittedAbove: width = 257; height = 256; break;
    case kSyntheticCanvasBelow: width = 2048; height = 2047; break;
    case kSyntheticCanvasExact: width = 2048; height = 2048; break;
    default: break;
  }

  if (!ConfigureProfile1Encoder(encoder, width, height, loops)) ok = false;
  if (ok) {
    switch (kind) {
      case kSyntheticCrop: {
        FrameSpec base{};
        base.value = 16;
        ok = AddSyntheticFrame(encoder, width, height, base);
        FrameSpec crop{};
        crop.crop = true;
        crop.x = 2; crop.y = 2; crop.width = 4; crop.height = 4; crop.value = 192;
        ok = ok && AddSyntheticFrame(encoder, width, height, crop);
        break;
      }
      case kSyntheticBlendPrevious: {
        FrameSpec base{};
        base.value = 32;
        ok = AddSyntheticFrame(encoder, width, height, base);
        FrameSpec blend{};
        blend.blend = JXL_BLEND_BLEND;
        blend.source = 0;
        blend.value = 160;
        ok = ok && AddSyntheticFrame(encoder, width, height, blend);
        break;
      }
      case kSyntheticSavedReference: {
        FrameSpec saved{};
        saved.duration = 0;
        saved.save_reference = 1;
        saved.value = 48;
        ok = AddSyntheticFrame(encoder, width, height, saved);
        FrameSpec display{};
        display.blend = JXL_BLEND_BLEND;
        display.source = 1;
        display.value = 176;
        ok = ok && AddSyntheticFrame(encoder, width, height, display);
        break;
      }
      case kSyntheticReferenceChain: {
        FrameSpec f0{};
        f0.duration = 0; f0.save_reference = 0; f0.value = 16;
        ok = AddSyntheticFrame(encoder, width, height, f0);
        FrameSpec f1{};
        f1.duration = 0; f1.blend = JXL_BLEND_BLEND; f1.source = 0; f1.save_reference = 1; f1.value = 64;
        ok = ok && AddSyntheticFrame(encoder, width, height, f1);
        FrameSpec f2{};
        f2.duration = 0; f2.blend = JXL_BLEND_BLEND; f2.source = 1; f2.save_reference = 2; f2.value = 112;
        ok = ok && AddSyntheticFrame(encoder, width, height, f2);
        FrameSpec f3{};
        f3.blend = JXL_BLEND_BLEND; f3.source = 2; f3.value = 192;
        ok = ok && AddSyntheticFrame(encoder, width, height, f3);
        break;
      }
      case kSyntheticZeroDurationLayers: {
        for (uint32_t i = 0; i < 8 && ok; ++i) {
          FrameSpec layer{};
          layer.duration = 0;
          layer.save_reference = i == 0 ? 0 : 1;
          layer.value = static_cast<uint8_t>(i * 20);
          ok = AddSyntheticFrame(encoder, width, height, layer);
        }
        FrameSpec display{};
        display.value = 220;
        ok = ok && AddSyntheticFrame(encoder, width, height, display);
        break;
      }
      case kSyntheticCropBlendReference: {
        FrameSpec base{};
        base.duration = 0; base.save_reference = 1; base.value = 24;
        ok = AddSyntheticFrame(encoder, width, height, base);
        FrameSpec patch{};
        patch.crop = true; patch.x = 1; patch.y = 1; patch.width = 6; patch.height = 6;
        patch.blend = JXL_BLEND_BLEND; patch.source = 1; patch.save_reference = 2; patch.duration = 0; patch.value = 96;
        ok = ok && AddSyntheticFrame(encoder, width, height, patch);
        FrameSpec display{};
        display.crop = true; display.x = 2; display.y = 2; display.width = 4; display.height = 4;
        display.blend = JXL_BLEND_BLEND; display.source = 2; display.value = 220;
        ok = ok && AddSyntheticFrame(encoder, width, height, display);
        break;
      }
      case kSyntheticStructuralStress: {
        for (uint32_t i = 0; i < 128 && ok; ++i) {
          FrameSpec layer{};
          layer.duration = 0;
          layer.save_reference = static_cast<uint32_t>(i % 3);
          layer.value = static_cast<uint8_t>(i);
          if (i != 0) {
            layer.blend = JXL_BLEND_BLEND;
            layer.source = static_cast<uint32_t>((i - 1) % 3);
          }
          ok = AddSyntheticFrame(encoder, width, height, layer);
        }
        FrameSpec display{};
        display.blend = JXL_BLEND_BLEND; display.source = 1; display.value = 255;
        ok = ok && AddSyntheticFrame(encoder, width, height, display);
        break;
      }
      case kSyntheticWidthBelow:
      case kSyntheticWidthAt:
      case kSyntheticWidthAbove:
      case kSyntheticCanvasBelow:
      case kSyntheticCanvasExact:
        ok = AddRepeatedFrames(encoder, width, height, 1, kMinimumDurationUs);
        break;
      case kSyntheticFramesBelow:
        ok = AddRepeatedFrames(encoder, width, height, 511, kMinimumDurationUs);
        break;
      case kSyntheticFramesAt:
        ok = AddRepeatedFrames(encoder, width, height, 512, kMinimumDurationUs);
        break;
      case kSyntheticFramesAbove:
        ok = AddRepeatedFrames(encoder, width, height, 513, kMinimumDurationUs);
        break;
      case kSyntheticSubmittedBelow:
        ok = AddRepeatedFrames(encoder, width, height, 510, kMinimumDurationUs);
        break;
      case kSyntheticSubmittedExact:
        ok = AddRepeatedFrames(encoder, width, height, 512, kMinimumDurationUs);
        break;
      case kSyntheticSubmittedAbove:
        ok = AddRepeatedFrames(encoder, width, height, 511, kMinimumDurationUs);
        break;
      case kSyntheticTimelineBelow:
        ok = AddRepeatedFrames(encoder, width, height, 1, 299'999'999U);
        break;
      case kSyntheticTimelineAt:
        ok = AddRepeatedFrames(encoder, width, height, 1, 300'000'000U);
        break;
      case kSyntheticTimelineAbove:
        ok = AddRepeatedFrames(encoder, width, height, 1, 300'000'001U);
        break;
      case kSyntheticDurationBelow:
        ok = AddRepeatedFrames(encoder, width, height, 1, 33'333U);
        break;
      case kSyntheticDurationAt:
        ok = AddRepeatedFrames(encoder, width, height, 1, 33'334U);
        break;
      case kSyntheticDurationAbove:
        ok = AddRepeatedFrames(encoder, width, height, 1, 33'335U);
        break;
      default:
        ok = false;
        break;
    }
  }

  if (ok) ok = FinishEncoder(encoder, output);
  JxlEncoderDestroy(encoder);
  return ok ? kToolSuccess : kToolEncodeFailure;
}

int CopyOutput(const std::vector<uint8_t>& bytes, uint8_t** output, size_t* output_size) {
  if (output == nullptr || output_size == nullptr) return kToolInvalidArgument;
  *output = nullptr;
  *output_size = 0;
  if (bytes.empty()) return kToolDecodeFailure;
  void* memory = std::malloc(bytes.size());
  if (memory == nullptr) return kToolAllocationFailure;
  std::memcpy(memory, bytes.data(), bytes.size());
  *output = static_cast<uint8_t*>(memory);
  *output_size = bytes.size();
  return kToolSuccess;
}
}  // namespace

BASIS_P1_EXPORT int basis_profile1_editor_decode_jxl_timeline(
    const uint8_t* input,
    size_t input_size,
    uint8_t** output,
    size_t* output_size) {
  if (output == nullptr || output_size == nullptr) return kToolInvalidArgument;
  *output = nullptr;
  *output_size = 0;
  std::vector<uint8_t> timeline;
  int result = DecodeJxlTimeline(input, input_size, &timeline);
  if (result != kToolSuccess) return result;
  return CopyOutput(timeline, output, output_size);
}

BASIS_P1_EXPORT int basis_profile1_editor_generate_synthetic_fixture(
    uint32_t kind,
    uint8_t** output,
    size_t* output_size) {
  if (output == nullptr || output_size == nullptr) return kToolInvalidArgument;
  *output = nullptr;
  *output_size = 0;
  std::vector<uint8_t> encoded;
  int result = EncodeSynthetic(kind, &encoded);
  if (result != kToolSuccess) return result;
  return CopyOutput(encoded, output, output_size);
}

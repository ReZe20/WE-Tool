/*
 * Minimal-build link stubs for encoder factories disabled by args.gn.
 *
 * WE Tool 只用 SKCodec 播 GIF(src/c 包装层其余编码能力不被 C# 层调用):
 * PNG/JPEG/WebP 编码器、JPEG 解码助手、SVG 画布均未编译进本 dll,
 * 此文件为 src/c 无条件调用点提供返回失败的空实现,保证链接通过。
 * 与官方 src/encode/*_none.cpp 桩模式一致。若恢复完整版构建,
 * 从 BUILD.gn 的 SkiaSharp 目标里移除本文件即可。
 */

#include "include/c/sk_types.h"

#include "include/codec/SkCodec.h"
#include "include/codec/SkJpegDecoder.h"
#include "include/core/SkCanvas.h"
#include "include/core/SkData.h"
#include "include/core/SkImage.h"
#include "include/core/SkPixmap.h"
#include "include/core/SkRect.h"
#include "include/core/SkRefCnt.h"
#include "include/core/SkStream.h"
#include "include/encode/SkEncoder.h"
#include "include/encode/SkJpegEncoder.h"
#include "include/encode/SkPngEncoder.h"
#include "include/encode/SkWebpEncoder.h"
#include "include/svg/SkSVGCanvas.h"

#include <memory>
#include <utility>

namespace SkJpegDecoder {

std::unique_ptr<SkCodec> Decode(sk_sp<const SkData>, SkCodec::Result* result, SkCodecs::DecodeContext) {
    if (result) { *result = SkCodec::Result::kInvalidInput; }
    return nullptr;
}

}  // namespace SkJpegDecoder

namespace SkJpegEncoder {

bool Encode(SkWStream*, const SkPixmap&, const Options&) {
    return false;
}

sk_sp<SkData> Encode(GrDirectContext*, const SkImage*, const Options&) {
    return nullptr;
}

}  // namespace SkJpegEncoder

namespace SkPngEncoder {

bool Encode(SkWStream*, const SkPixmap&, const Options&) {
    return false;
}

sk_sp<SkData> Encode(GrDirectContext*, const SkImage*, const Options&) {
    return nullptr;
}

}  // namespace SkPngEncoder

namespace SkWebpEncoder {

bool Encode(SkWStream*, const SkPixmap&, const Options&) {
    return false;
}

bool EncodeAnimated(SkWStream*, SkSpan<const SkEncoder::Frame>, const Options&) {
    return false;
}

}  // namespace SkWebpEncoder

#if !defined(SK_DISABLE_LEGACY_SVG_FACTORIES)
std::unique_ptr<SkCanvas> SkSVGCanvas::Make(const SkRect&, SkWStream*, uint32_t) {
    return nullptr;
}
#endif

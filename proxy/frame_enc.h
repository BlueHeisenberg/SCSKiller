// scskiller_frames.bin's records (format at proxy.cpp's frame hooks), shared by the D3D recorder and the Vulkan layer.
#pragma once
#include <algorithm>
#include <cstdint>
#include <vector>

struct FrameAt { int64_t qpc; void* sc; bool game; };  // game: a frame generation swap chain's, the frames the game renders

static const uint64_t kFramesCap = 32ull << 20;  // 8M frames: over 7 hours at 300 FPS
struct FrameEnc {
    int64_t qpc0 = 0, freq = 1;
    uint64_t last_us = 0, bytes = 0;
    std::vector<void*> scs;
    std::vector<uint32_t> out;
    // Top 4 bits 15 with the low 28 bits under kTagEnd: a tagged record, not a gap. A gap is written only for 2^28 us or
    // more, so its milliseconds never go under kTagEnd, and a reader from before tagged records takes one for a gap of
    // under 268 s. Tags 0-14: that swap chain index is frame generation's; 15 up to kTagEnd are free (ARCHITECTURE.md).
    static constexpr uint32_t kTagEnd = (1u << 28) / 1000;
    void put(const FrameAt& f) {
        int64_t t = f.qpc - qpc0;
        uint64_t us = t <= 0 ? 0 : (uint64_t)(t / freq * 1000000 + t % freq * 1000000 / freq);
        uint64_t d = us > last_us ? us - last_us : 0;
        last_us += d;
        while (d >> 28) {
            uint64_t ms = std::min<uint64_t>(d / 1000, 0x0FFFFFFE);  // at least kTagEnd: d / 1000 >= 2^28 / 1000
            out.push_back(0xF0000000u | (uint32_t)ms);
            d -= ms * 1000;
        }
        size_t i = std::find(scs.begin(), scs.end(), f.sc) - scs.begin();
        if (i == scs.size() && i < 14) {
            scs.push_back(f.sc);
            if (f.game) out.push_back(0xF0000000u | (uint32_t)i);
        }
        out.push_back((uint32_t)std::min<size_t>(i, 14) << 28 | (uint32_t)d);
    }
};

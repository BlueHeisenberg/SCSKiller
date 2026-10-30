// The in-game panel of a before and after run, its pure parts: proxy.cpp draws it, selftest.cpp checks them.
// The panel is painted on the CPU into a canvas (sRGB bytes, as an SDR screen shows them), converted to the back buffer's
// format and copied in: no shader, root signature or PSO, so nothing goes through the recorder's device hooks or into the
// driver's cache.
#pragma once
#ifdef _WIN32
#include <windows.h>
#include <dxgiformat.h>
#include <dxgicommon.h>
#include <DirectXPackedVector.h>
#else
#include <cwctype>
struct RECT { long left, top, right, bottom; };
#endif
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>
#include "overlay_font.h"

namespace ov {

// The panel's scale: 1 at 720p, 2 at 1080p, 3 at 1440p, 4 from 4K up (the font's sizes, the buffers are sized for it).
constexpr int kMaxScale = 4;
inline int scale_for(uint64_t height) { return height < 960 ? 1 : (int)std::min<uint64_t>(height / 480, kMaxScale); }
constexpr const ovfont::Size& font(int s) { return ovfont::kSizes[std::clamp(s, 1, kMaxScale) - 1]; }
constexpr const ovfont::Glyph* glyph(const ovfont::Size& f, char c) {
    for (int i = 0; i < f.n; ++i)
        if (f.glyphs[i].c == c) return &f.glyphs[i];
    return nullptr;  // a space, or a character the font doesn't have: blank
}
constexpr long text_width(const char* t, int s) {
    const auto& f = font(s);
    long w = 0;
    for (; *t; ++t) w += *t == ' ' ? f.space : glyph(f, *t) ? glyph(f, *t)->adv : f.space;
    return w;
}

inline constexpr char kDone[] = "Done, you can close the game";
inline constexpr char kStutter[] = "SHADER COMPILATION STUTTER!";
// "SCSKiller before 3:42": the seconds left, rounded up, so 0:00 shows only once the time is played. Written into b.
inline const char* countdown(bool after, double left_s, char (&b)[48]) {
    long s = left_s <= 0 ? 0 : (long)std::ceil(left_s);
    snprintf(b, sizeof b, "SCSKiller %s %ld:%02ld", after ? "after" : "before", s / 60, s % 60);
    return b;
}
inline std::string countdown(bool after, double left_s) {
    char b[48];
    return countdown(after, left_s, b);
}

// Frame times of the last 5 s in columns of 100 ms, each the longest frame that overlaps it; a column a shader
// compilation stutter overlaps is marked.
struct Graph {
    static constexpr int kCols = 50;
    static constexpr double kColMs = 100;
    float ms[kCols] = {};
    bool shader[kCols] = {};
    long newest = -1;  // the column of the last frame's end
    void advance(long b) {
        for (long k = std::max(newest + 1, b - kCols + 1); k <= b; ++k) ms[k % kCols] = 0, shader[k % kCols] = false;
        newest = std::max(newest, b);
    }
    template <class F> void each(double s, double e, F f) {
        for (long k = std::max((long)(s / kColMs), newest - kCols + 1); k <= (long)(e / kColMs) && k <= newest; ++k) f(k % kCols);
    }
    void frame(double s, double e) {
        if (e < 0 || s > e) return;
        advance((long)(e / kColMs));
        each(std::max(s, 0.0), e, [&](long i) { ms[i] = std::max(ms[i], float(e - s)); });
    }
    void mark(double s, double e) { each(std::max(s, 0.0), e, [&](long i) { shader[i] = true; }); }
};

// The panel at the top right of a w x h back buffer: a line of text, a thin bar, the graph with its 50 ms line, and
// (stutters) a line for the shader compilation stutter flash. Positions are from the panel's top left.
struct Layout {
    int s = 1;
    long w = 0, h = 0, pad = 0, text_y = 0, track_y = 0, track_h = 0, graph_y = 0, graph_h = 0, flash_y = 0;
    RECT at = {};   // on the buffer, clipped to it; empty: left == right
    long sx = 0, sy = 0;  // where at's top left is in the panel
};
inline void layout(uint64_t bw, uint64_t bh, const char* widest, bool stutters, Layout& L) {
    int s = L.s = scale_for(bh);
    const auto& f = font(s);
    long gap = 3 * s, margin = 8 * s;
    L.pad = 4 * s;
    long tw = std::max({text_width(widest, s), text_width(kDone, s), stutters ? text_width(kStutter, s) : 0L});
    L.w = tw + 2 * L.pad;
    L.text_y = L.pad;
    L.track_y = L.text_y + f.height + gap, L.track_h = s;
    L.graph_y = L.track_y + L.track_h + gap, L.graph_h = 24 * s;
    L.flash_y = L.graph_y + L.graph_h + gap;
    L.h = (stutters ? L.flash_y + f.height : L.graph_y + L.graph_h) + L.pad;
    long r = (long)bw - margin, l = r - L.w, t = margin;
    L.at = {std::max(l, 0L), std::max(t, 0L), std::min(r, (long)bw), std::min(t + L.h, (long)bh)};
    if (L.at.right <= L.at.left || L.at.bottom <= L.at.top) L.at.right = L.at.left, L.at.bottom = L.at.top;
    L.sx = L.at.left - l, L.sy = L.at.top - t;
}

// Colours as an SDR screen shows them, 0xAABBGGRR.
constexpr uint32_t rgb(uint32_t r, uint32_t g, uint32_t b) { return r | g << 8 | b << 16 | 0xFF000000u; }
inline constexpr uint32_t kPanel = rgb(5, 5, 5), kTrack = rgb(51, 51, 51), kFill = rgb(64, 166, 255), kText = rgb(255, 255, 255),
                          kGraphBg = rgb(22, 22, 22), kLine = rgb(150, 150, 150), kSlow = rgb(255, 184, 0), kShader = rgb(255, 64, 64);

// fg over bg by coverage a (0-255), per byte as the screen shows them.
inline uint32_t mix(uint32_t fg, uint32_t bg, uint32_t a) {
    uint32_t o = 0xFF000000u;
    for (int sh = 0; sh < 24; sh += 8) o |= (((fg >> sh & 255) * a + (bg >> sh & 255) * (255 - a) + 127) / 255) << sh;
    return o;
}
inline void fill(uint32_t* img, long pitch, long x0, long y0, long x1, long y1, uint32_t c) {
    for (long y = y0; y < y1; ++y) std::fill(img + y * pitch + x0, img + y * pitch + x1, c);
}
// t with its line's top at y, from x, in colour c over the solid bg under it.
inline void text(uint32_t* img, long pitch, long x, long y, const char* t, int s, uint32_t c, uint32_t bg) {
    const auto& f = font(s);
    for (; *t; ++t) {
        const ovfont::Glyph* g = *t == ' ' ? nullptr : glyph(f, *t);
        if (!g) { x += f.space; continue; }
        for (int r = 0; r < g->h; ++r)
            for (int k = 0; k < g->w; ++k) {
                uint32_t i = g->at + r * ((g->w + 1) & ~1) + k, a = (ovfont::kPixels[i / 2] >> (i % 2 * 4) & 15) * 17;
                if (a) img[(y + g->y + r) * pitch + x + g->x + k] = mix(c, bg, a);
            }
        x += g->adv;
    }
}
// The whole panel into img (L.w x L.h, pitch pixels a row): the text, the bar filled to frac, the graph, and the flash
// line when flash.
inline void paint(const Layout& L, const char* t, double frac, const Graph& g, bool flash, uint32_t* img, long pitch) {
    int s = L.s;
    long p = L.pad, iw = L.w - 2 * p;
    fill(img, pitch, 0, 0, L.w, L.h, kPanel);
    text(img, pitch, p, L.text_y, t, s, kText, kPanel);
    fill(img, pitch, p, L.track_y, p + iw, L.track_y + L.track_h, kTrack);
    fill(img, pitch, p, L.track_y, p + std::lround(iw * std::clamp(frac, 0.0, 1.0)), L.track_y + L.track_h, kFill);
    long gy = L.graph_y, gh = L.graph_h;
    fill(img, pitch, p, gy, p + iw, gy + gh, kGraphBg);
    for (int i = 0; i < Graph::kCols; ++i) {  // oldest at the left, 0-100 ms bottom to top
        long k = g.newest - Graph::kCols + 1 + i;
        if (k < 0) continue;
        float ms = g.ms[k % Graph::kCols];
        long x0 = p + iw * i / Graph::kCols, x1 = p + iw * (i + 1) / Graph::kCols - (iw >= 3 * Graph::kCols);
        long bar = std::lround(gh * std::min(ms, 100.0f) / 100);
        uint32_t c = g.shader[k % Graph::kCols] ? kShader : ms >= 50 ? kSlow : kFill;
        if (bar > 0) fill(img, pitch, x0, gy + gh - bar, std::max(x1, x0 + 1), gy + gh, c);
    }
    long line = gy + gh / 2;  // 50 ms
    for (long x = p; x < p + iw; x += 2 * s) fill(img, pitch, x, line, std::min(x + s, p + iw), line + std::max(1, s / 2), kLine);
    if (flash) text(img, pitch, p, L.flash_y, kStutter, s, kShader, kPanel);
}

// What a back buffer's pixels get: SDR 8-bit (RGBA or BGRA) or 10-bit, HDR10 (PQ, BT.2020) or scRGB (FP16, linear).
enum Out { kNone, kRgba8, kBgra8, kRgb10, kPq10, kScRgb };
#ifdef _WIN32
// cs_known: the colour space is our SetColorSpace1 hook's, so a 10-bit buffer's can be told. An FP16 swap chain is scRGB.
inline Out out_of(DXGI_FORMAT f, int cs, bool cs_known) {
    switch (f) {
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return kRgba8;
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return kBgra8;
    case DXGI_FORMAT_R10G10B10A2_UNORM:
        return !cs_known ? kNone : cs == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 ? kRgb10 : cs == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ? kPq10 : kNone;
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
        return cs == DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 || cs == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 ? kScRgb : kNone;
    default: return kNone;
    }
}
inline bool drawn_format(DXGI_FORMAT f) { return out_of(f, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, true) != kNone; }
inline uint16_t half(float v) { return DirectX::PackedVector::XMConvertFloatToHalf(v); }
#else
// v in a half's normal range or 0, rounded to nearest even as XMConvertFloatToHalf rounds
inline uint16_t half(float v) {
    uint32_t b;
    memcpy(&b, &v, 4);
    if (!(b & 0x7fffffff)) return (uint16_t)(b >> 16);
    uint32_t h = (b >> 16 & 0x8000) | ((b >> 23 & 255) - 112) << 10 | (b & 0x7fffff) >> 13, rest = b & 0x1fff;
    return (uint16_t)(h + (rest > 0x1000 || (rest == 0x1000 && (h & 1))));
}
#endif
inline int bytes(Out o) { return o == kScRgb ? 8 : 4; }

// SDR white in HDR: BT.2408's reference white.
constexpr double kWhiteNits = 203;
struct Luts {
    float lin[256];       // an sRGB byte as linear light, SDR white 1
    uint16_t ten[256];    // as a 10-bit SDR code
    uint16_t half[256];   // as scRGB (1 = 80 nits), FP16
    uint16_t pq[4096];    // linear light 0-1 (SDR white) as a 10-bit PQ code
};
inline Luts make_luts() {
    Luts l;
    for (int v = 0; v < 256; ++v) {
        double c = v / 255.0, x = c <= 0.04045 ? c / 12.92 : std::pow((c + 0.055) / 1.055, 2.4);
        l.lin[v] = (float)x, l.ten[v] = (uint16_t)std::lround(c * 1023);
        l.half[v] = half((float)(x * kWhiteNits / 80));
    }
    for (int i = 0; i < 4096; ++i) {
        double y = std::pow(i / 4095.0 * kWhiteNits / 10000, 0.1593017578125);
        l.pq[i] = (uint16_t)std::lround(std::pow((0.8359375 + 18.8515625 * y) / (1 + 18.6875 * y), 78.84375) * 1023);
    }
    return l;
}
inline const Luts& luts() {  // the first call, before any present, makes them
    static const Luts l = make_luts();
    return l;
}
// w x h canvas pixels from src as o's pixels into dst (rows of dpitch bytes).
inline void convert(const uint32_t* src, long spitch, long w, long h, Out o, uint8_t* dst, long dpitch) {
    const Luts& L = luts();
    for (long y = 0; y < h; ++y) {
        const uint32_t* s = src + y * spitch;
        uint8_t* d = dst + y * dpitch;
        if (o == kRgba8) { memcpy(d, s, w * 4); continue; }
        for (long x = 0; x < w; ++x) {
            uint32_t p = s[x], r = p & 255, g = p >> 8 & 255, b = p >> 16 & 255;
            if (o == kBgra8) ((uint32_t*)d)[x] = b | g << 8 | r << 16 | 0xFF000000u;
            else if (o == kRgb10) ((uint32_t*)d)[x] = L.ten[r] | L.ten[g] << 10 | L.ten[b] << 20 | 3u << 30;
            else if (o == kScRgb) ((uint64_t*)d)[x] = L.half[r] | (uint64_t)L.half[g] << 16 | (uint64_t)L.half[b] << 32 | (uint64_t)0x3C00 << 48;
            else {  // BT.709 to BT.2020 primaries, then PQ
                float R = L.lin[r], G = L.lin[g], B = L.lin[b];
                auto q = [&](float v) { return (uint32_t)L.pq[std::lround(std::clamp(v, 0.0f, 1.0f) * 4095)]; };
                ((uint32_t*)d)[x] = q(0.6274f * R + 0.3293f * G + 0.0433f * B) | q(0.0691f * R + 0.9195f * G + 0.0114f * B) << 10 |
                                    q(0.0164f * R + 0.0880f * G + 0.8956f * B) << 20 | 3u << 30;
            }
        }
    }
}

// The panel at kMaxScale, in pixels, as layout() makes it: the widest line (a countdown has 5 digits at most) and every row.
constexpr long kMaxW = std::max({text_width(kDone, kMaxScale), text_width(kStutter, kMaxScale), text_width("SCSKiller before 00:00", kMaxScale)}) + 2 * 4 * kMaxScale;
constexpr long kMaxH = 2 * 4 * kMaxScale + 2 * font(kMaxScale).height + 3 * 3 * kMaxScale + kMaxScale + 24 * kMaxScale;

// A present queue frame generation makes (FSR 3's "AMD FSR PresentQueue", XeSS's "XefgInterpolationSwapChain::present_queue_").
inline bool fg_queue(const std::wstring& name) {
    std::wstring n = name;
    for (auto& c : n) c = towlower(c);
    for (const wchar_t* k : {L"fsr", L"xefg", L"interpolat", L"framegen", L"frame gen", L"dlssg"})
        if (n.find(k) != std::wstring::npos) return true;
    return false;
}
// Loaded, these mean frame generation was asked for (DLSS-G's, its FSR 3 replacements, XeSS's).
inline const wchar_t* const kFgModules[] = {L"nvngx_dlssg.dll", L"dlssg_to_fsr3_amd_is_better.dll", L"libxess_fg.dll"};

// Frame ends from presents to the thread that draws, in order. One present pushes at a time: a push that finds another
// pushing, or the ring full (nothing drawn for N frames), drops its frame rather than wait.
struct Feed {
    static constexpr uint64_t N = 1024;
    std::atomic<double> t[N];
    std::atomic<uint64_t> head{0}, tail{0};
    std::atomic_flag pushing;
    void push(double v) {
        if (pushing.test_and_set(std::memory_order_acquire)) return;
        uint64_t h = head.load(std::memory_order_relaxed);
        if (h - tail.load(std::memory_order_acquire) < N) t[h % N].store(v, std::memory_order_relaxed), head.store(h + 1, std::memory_order_release);
        pushing.clear(std::memory_order_release);
    }
    template <class F> void drain(F f) {  // one thread at a time
        uint64_t h = head.load(std::memory_order_acquire), i = tail.load(std::memory_order_relaxed);
        for (; i < h; ++i) f(t[i % N].load(std::memory_order_relaxed));
        tail.store(i, std::memory_order_release);
    }
};

// The creates the recorder saw last, for FrameLog's hitch causes live: written by one thread at a time (the recorder's
// lock), read by the draw without one (a slot being written is skipped).
struct Creates {
    static constexpr uint32_t N = 2048;
    enum : uint8_t { kCompile = 1, kBlocks = 2, kPresents = 4 };
    struct Slot { std::atomic<uint32_t> seq{0}, tid{0}; std::atomic<double> end{0}, ms{0}; std::atomic<uint8_t> flags{0}; } s[N];
    std::atomic<uint32_t> head{0};
    void add(double end, double ms, uint32_t tid, uint8_t flags) {
        uint32_t i = head.load(std::memory_order_relaxed);
        Slot& x = s[i % N];
        uint32_t q = x.seq.load(std::memory_order_relaxed);
        x.seq.store(q + 1, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);
        x.end.store(end, std::memory_order_relaxed), x.ms.store(ms, std::memory_order_relaxed);
        x.tid.store(tid, std::memory_order_relaxed), x.flags.store(flags, std::memory_order_relaxed);
        x.seq.store(q + 2, std::memory_order_release);
        head.store(i + 1, std::memory_order_release);
    }
    // FrameLog's cause of a frame of 50 ms or more in play from s to e, with the creates that end in it (so each counts for
    // one frame at most): true for a shader compilation stutter (compiles of 100 ms or more for half of it, counted inside
    // it, else any compile that blocks), false for a load (a compile on 8 threads or more, none the presenter's, or 100
    // creates) or another hitch. Newest first, down to creates ending 5 s before the frame.
    bool shader_stutter(double s, double e) const {
        double cold = 0;
        int n = 0, nt = 0;
        uint32_t tids[8];
        bool compile = false, blocks = false, presenter = false;
        for (uint32_t h = head.load(std::memory_order_acquire), k = 0; k < N && k < h; ++k) {
            const Slot& x = this->s[(h - 1 - k) % N];
            uint32_t q = x.seq.load(std::memory_order_acquire);
            if (!q || q & 1) continue;
            double end = x.end.load(std::memory_order_relaxed), ms = x.ms.load(std::memory_order_relaxed);
            uint32_t tid = x.tid.load(std::memory_order_relaxed);
            uint8_t f = x.flags.load(std::memory_order_relaxed);
            std::atomic_thread_fence(std::memory_order_acquire);
            if (x.seq.load(std::memory_order_relaxed) != q) continue;
            if (end < s - 5000) break;  // logged a little out of order: a create waits for the recorder's lock after it returns
            if (end <= s || end > e) continue;
            ++n, presenter |= (f & kPresents) != 0;
            if (nt < 8 && std::find(tids, tids + nt, tid) == tids + nt) tids[nt++] = tid;
            if (!(f & kCompile)) continue;
            compile = true, blocks |= (f & kBlocks) != 0;
            if (ms >= 100) cold += end - std::max(end - ms, s);
        }
        if (compile && nt >= 8 && !presenter) return false;
        if (cold >= (e - s) / 2) return true;
        return n < 100 && blocks;
    }
};
// DXIL that traces rays inline (RayQuery): the SFI0 part's feature flag, as SCSKiller's Dxbc.InlineRayTracing reads it.
inline bool inline_rt(const char* p, size_t n) {
    auto u32 = [&](size_t at) { uint32_t v; memcpy(&v, p + at, 4); return v; };
    if (n < 32 || memcmp(p, "DXBC", 4)) return false;
    for (uint32_t i = 0, parts = u32(28); i < parts && 32 + 4 * (size_t)i + 4 <= n; ++i) {
        size_t at = u32(32 + 4 * i);
        if (at + 16 <= n && !memcmp(p + at, "SFI0", 4) && u32(at + 4) >= 8) return (u32(at + 8) & 0x100000) != 0;
    }
    return false;
}

// Frames of 50 ms or more, judged once play's start is known and their creates are in (kWaitMs: a create is logged once
// the recorder's lock is free). A shader compilation stutter marks its columns and, if it is recent, flashes kStutter
// for kFlashMs.
struct Slow {
    static constexpr double kWaitMs = 1000, kFlashMs = 2000;
    static constexpr int kMax = 32;  // a startup's loading frames wait here too until play's start is known: the oldest go first
    struct F { double s, e; } f[kMax];
    int n = 0;
    double flash_until = -1;
    uint32_t found = 0;
    void frame(double s, double e) {
        if (e - s < 50) return;
        if (n == kMax) std::copy(f + 1, f + kMax, f), --n;
        f[n++] = {s, e};
    }
    double checked = -1e18;
    void check(const Creates& c, double now, double startup, Graph& g) {
        if (startup < 0 || now - checked < 100) return;  // ten times a second at most: each check scans the creates
        checked = now;
        for (int i = 0; i < n;)
            if (bool shader = f[i].e > startup && c.shader_stutter(f[i].s, f[i].e); shader || f[i].e <= startup || now > f[i].e + kWaitMs) {
                if (shader) {
                    g.mark(f[i].s, f[i].e), ++found;
                    if (now - f[i].e < kFlashMs) flash_until = now + kFlashMs;
                }
                std::copy(f + i + 1, f + n, f + i), --n;
            } else ++i;
    }
    bool flashing(double now) const { return now < flash_until; }
};

// Play as FrameLog counts a before and after run's, live: every frame from the launch's first present, loading and pauses
// included. shown() leaves out the frames FrameLog leaves out as quitting if the launch ended now (those starting in its
// last 10 s), so it is the play the app reads from the frame log at that moment and the report would count. Times are ms
// on the recorder's clock (the csv's t_ms). One thread at a time.
struct Clock {
    static constexpr double kQuitMs = 10000;  // FrameLog.QuitMs
    double startup = -1, last = -1, played = 0;  // startup: the first present
    double recent[16] = {};  // play ms per second of its frames' start, the last 16 s
    long recent_s[16] = {-1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1};

    // A present at t. True when it is the first: play counts from it.
    bool frame(double t) {
        bool first = startup < 0;
        if (first) startup = t;
        else if (t > last) count(last, t - last);
        last = t;
        return first;
    }
    void count(double start, double ms) {
        played += ms;
        recently(start, ms);
    }
    void recently(double start, double ms) {
        long s = (long)(start / 1000);
        auto& at = recent_s[s & 15];
        if (s < at) return;  // older than the last 16 s
        if (s > at) at = s, recent[s & 15] = 0;
        recent[s & 15] += ms;
    }
    double shown(double now) const {
        double v = played;
        for (int k = 0; k < 16; ++k)
            if (recent_s[k] >= 0 && (recent_s[k] + 1) * 1000.0 > now - kQuitMs) v -= recent[k];
        return std::max(v, 0.0);
    }
};

}  // namespace ov

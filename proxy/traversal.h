// Traversal stutter, included by proxy.cpp (logf, patch, g_dir). At a streaming boundary a game creates many small
// committed resources at once: each create makes its memory resident and the calling thread waits on the paging fence.
// Probe (always, in a hooked device): resource creation and residency calls timed per frame and thread; a frame whose
// total reaches probe_ms is logged ("traversal: frame"), and the totals per call every 30 s.
// Fix (scskiller.ini traversal_suballocate=1): a committed create that can live in a shared heap is placed in one of a
// few heaps made ahead on a background thread, which are resident already: CREATE_NOT_ZEROED (or, with traversal_zeroed,
// a DEFAULT or UPLOAD buffer without it) and no other heap flag; on the
// DEFAULT heap a buffer or a texture without render target, depth stencil, simultaneous access or UAV; on the
// UPLOAD or READBACK heap a buffer without flags. Never an acceleration structure buffer. Always max_resource_mb at most,
// 64 KB alignment, no clear value, one node, no protected session. Anything else, any failure, and a create a
// layer grew past its range, takes the game's committed create. One device only: the device of the game's latest command queue at the first placed create, its heaps
// made ahead from the game's first queue on (moved, empty, to a later queue's device until then); every other device's
// stay committed. A freed range waits reuse_frames presents, and the game's GPU work submitted before the free on every
// queue (a fence of ours per game queue), before it's placed again, and a heap
// as long after its last free before it's released.
// Heaps grow while the memory segment's usage plus a heap stays under 90% of its budget; over 95%, an empty heap is
// released each second. A create that finds no room makes one heap itself, after the one being made, if any. The pool thread reads traversal_suballocate again each second: 0 stops placing, for the session, and releases
// every empty heap.
// Keys, in [scskiller] (admission reads an ini with another section as unsigned): traversal_suballocate=0,
// traversal_buffers=1, traversal_textures=1 (DEFAULT), traversal_upload=1, traversal_readback=1, traversal_uav_buffers=0,
// traversal_uav_textures=0 (NOT_ZEROED UAV-only textures in the textures pool: legacy-state ones have no metadata to init per the Enhanced Barriers spec, AMD advises init of every placed resource),
// traversal_reuse_frames=3 (0: once the GPU is past it), traversal_reuse_ms=50 (instead, while no frame came for 500 ms), traversal_dred=0 (DRED on, before the first device: its output and what of ours
// was at the fault are logged at a removal, and every placement), traversal_heap_mb=128, traversal_spare_heaps=2 (free
// heaps kept per DEFAULT pool, 1 for UPLOAD and READBACK), traversal_max_pool_mb=2048 (every pool's heaps; at it, a create
// releases an empty heap of another pool for its own), traversal_max_resource_mb=16,
// traversal_probe_ms=5, traversal_zeroed=0 (DEFAULT buffers created without CREATE_NOT_ZEROED, in the zeroed pool),
// traversal_zeroed_textures=0 (such non-RT/DS/UAV textures too, in the zeroed textures pool),
// traversal_zeroed_upload=traversal_zeroed (UPLOAD buffers created without CREATE_NOT_ZEROED, in the zeroed upload pool),
// traversal_zeroed_reuse=1 when traversal_zeroed or traversal_zeroed_upload is, else 0 (1: a freed range of the zeroed
// buffer and upload pools is zeroed again by the pool thread and reused; 0, only when set with them on: each range used
// once, the zeroed pools churn OS-zeroed heaps; the zeroed textures pool is always once-only),
// traversal_test_budget_mb=0 (the local budget taken as this many MB, a smaller card's).
// traversal_copy_high=0 (with or without suballocate): the game's COPY queues made at NORMAL priority are made at HIGH,
// at the original priority when the driver refuses it.
#pragma once

namespace trav {

enum Kind { CR, CR1, CR2, CR3, PLACED, PLACED1, PLACED2, RESERVED, RESERVED1, RESERVED2, HEAP, HEAP1, MAKE_RESIDENT, ENQUEUE_RESIDENT, EVICT, KINDS };
static const char* const kKind[KINDS] = {
    "CreateCommittedResource", "CreateCommittedResource1", "CreateCommittedResource2", "CreateCommittedResource3", "CreatePlacedResource",
    "CreatePlacedResource1", "CreatePlacedResource2", "CreateReservedResource", "CreateReservedResource1", "CreateReservedResource2",
    "CreateHeap", "CreateHeap1", "MakeResident", "EnqueueMakeResident", "Evict"};
// no room, by why the pool couldn't grow: W_ROOM_CAP .. W_ROOM_NONE follow grow()'s Grow values
enum Why { W_HEAP, W_NODE, W_ZEROED, W_HEAP_FLAGS, W_DIM, W_CLEAR, W_FLAGS, W_UAV_TEX, W_UAV_BUF, W_CPU, W_ACCEL, W_SESSION, W_SIZE, W_ALIGN, W_ROOM,
           W_ROOM_CAP, W_ROOM_BUDGET, W_ROOM_FAILED, W_ROOM_NONE, W_ERROR, W_QUEUE, W_DEVICE, W_ZEROED_ROOM, W_ZEROED_TEX, W_ZEROED_UPLOAD,
           W_ZEROED_READBACK, W_ROOM_BUSY, W_GROWN, W_BLIND, WHYS };
static const char* const kWhy[WHYS] = {"heap type off", "custom heap or node mask", "zeroed buffer", "other heap flags", "unknown dimension",
                                       "clear value", "RT/DS/simultaneous/MSAA", "UAV texture", "UAV buffer", "UPLOAD/READBACK texture or flags",
                                       "acceleration structure", "protected session", "too large", "alignment", "no room in a grown heap",
                                       "no room, pool at max_pool_mb", "no room, over 90% of the budget", "no room, heaps failed",
                                       "no room, no budget read yet", "create failed", "device without a command queue", "another device",
                                       "zeroed, no fresh range", "zeroed texture", "zeroed upload", "zeroed readback",
                                       "no room, a heap being made", "grown by a layer", "a game queue we can't fence"};

// Per thread: creates and residency calls since the last frame. ponytail: 64 slots, later threads share the last one.
struct Slot { std::atomic<DWORD> tid; std::atomic<uint64_t> n, ticks, rn, rticks; };
static Slot g_slot[64];
static std::atomic<int> g_nslot;
static thread_local Slot* t_slot;
static thread_local int t_depth;  // the runtime may route one create through another hooked one: only the outermost counts
static std::atomic<uint64_t> g_kind_n[KINDS], g_kind_ticks[KINDS];
static std::atomic<uint64_t> g_frame_placed, g_frame_why[WHYS];  // since the last frame: placed, committed by why
static std::atomic<uint64_t> g_frame_tick;  // GetTickCount64 at the last frame, 0: none yet
static std::atomic<uint64_t> g_frames;  // outermost presents, of any swap chain of the process
static int64_t g_freq = 1, g_probe_ticks = INT64_MAX;
static int g_probe_ms = 5;

template <class F> static HRESULT timed(int k, F&& call) {
    if (t_depth++) {
        HRESULT hr = call();
        --t_depth;
        return hr;
    }
    LARGE_INTEGER a, b;
    QueryPerformanceCounter(&a);
    HRESULT hr = call();
    QueryPerformanceCounter(&b);
    --t_depth;
    uint64_t d = b.QuadPart - a.QuadPart;
    if (!t_slot) {
        int i = g_nslot.fetch_add(1);
        if (i < 64) g_slot[i].tid = GetCurrentThreadId();
        t_slot = &g_slot[std::min(i, 63)];
    }
    const bool res = k >= MAKE_RESIDENT;
    (res ? t_slot->rn : t_slot->n).fetch_add(1, std::memory_order_relaxed);
    (res ? t_slot->rticks : t_slot->ticks).fetch_add(d, std::memory_order_relaxed);
    g_kind_n[k].fetch_add(1, std::memory_order_relaxed), g_kind_ticks[k].fetch_add(d, std::memory_order_relaxed);
    return hr;
}

struct ThreadFrame { DWORD tid; uint64_t n, ticks, rn, rticks; };
struct FrameRec { double t_ms; uint64_t placed; std::vector<ThreadFrame> threads; std::vector<std::pair<int, uint64_t>> why; };
static std::mutex& g_qmx = *new std::mutex;
static std::vector<FrameRec>& g_q = *new std::vector<FrameRec>;  // under g_qmx: frames over probe_ms, for tick
static uint64_t g_q_over;                                         // under g_qmx: frames past the queue's cap

// The signed traversal_suballocate=1 at the first hooked device: hooks installed, probe and fix on. Off, nothing of
// traversal runs: no hook, and frame / tick return at once, as before traversal.h.
static std::atomic<bool> g_active;

// At each frame (the outermost present of a thread): the counters since the last one, queued when over probe_ms.
static void frame(double t_ms) {
    if (!g_active.load(std::memory_order_relaxed)) return;
    g_frames.fetch_add(1, std::memory_order_relaxed);
    g_frame_tick.store(GetTickCount64(), std::memory_order_relaxed);
    ThreadFrame v[64];
    int n = 0;
    uint64_t total = 0;
    for (int i = 0, end = std::min(g_nslot.load(std::memory_order_relaxed), 64); i < end; ++i) {
        Slot& s = g_slot[i];
        if (!s.n.load(std::memory_order_relaxed) && !s.rn.load(std::memory_order_relaxed)) continue;
        v[n] = {s.tid, s.n.exchange(0), s.ticks.exchange(0), s.rn.exchange(0), s.rticks.exchange(0)};
        total += v[n].ticks + v[n].rticks, ++n;
    }
    uint64_t placed = n ? g_frame_placed.exchange(0) : 0, why[WHYS] = {};
    if (n)
        for (int w = 0; w < WHYS; ++w) why[w] = g_frame_why[w].load(std::memory_order_relaxed) ? g_frame_why[w].exchange(0) : 0;
    if (!n || (int64_t)total < g_probe_ticks) return;
    std::vector<std::pair<int, uint64_t>> by;
    for (int w = 0; w < WHYS; ++w)
        if (why[w]) by.push_back({w, why[w]});
    std::sort(v, v + n, [](auto& a, auto& b) { return a.ticks + a.rticks > b.ticks + b.rticks; });
    std::lock_guard l(g_qmx);
    if (g_q.size() < 64) g_q.push_back({t_ms, placed, {v, v + n}, std::move(by)});
    else ++g_q_over;
}

// Pools: DEFAULT buffers and non-RT/DS textures apart (every resource heap tier accepts that), UPLOAD and READBACK buffers.
struct Heap {
    ID3D12Heap* h;
    std::map<uint64_t, uint64_t> free;  // offset -> bytes
    uint64_t used;
    int id;
    uint64_t va;         // its GPU address, from a buffer placed in it (0: none yet, or a texture heap)
    uint64_t last_free;  // g_frames at its last free
    uint64_t last_tick = 0;  // GetTickCount64 then
    uint64_t fresh = 0;      // zeroed pool: where its never-used tail starts
    ID3D12Resource* alias = nullptr;  // re-zeroed pools: a buffer over the whole heap, the pool thread zeroes freed ranges through
    uint8_t* cpu = nullptr;           // re-zeroed UPLOAD pool: the alias, mapped for good
};
// The alias holds its heap: it goes first.
static ID3D12Heap* unalias(Heap& h) {
    if (h.alias) h.alias->Release(), h.alias = nullptr;
    return h.h;
}
struct Pool {
    const char* name;
    D3D12_HEAP_TYPE type;
    D3D12_HEAP_FLAGS flags;
    DXGI_MEMORY_SEGMENT_GROUP seg;  // whose budget its heaps count against
    bool on;
    std::list<Heap> heaps;
    uint64_t free_bytes, placed;
};
// The zeroed pool serves DEFAULT buffers created without CREATE_NOT_ZEROED from the never-used tail of heaps the OS
// zeroed at their creation; a freed range there is never taken again, and a spent heap goes once it's empty. The zeroed
// textures pool does the same for non-RT/DS/UAV textures (traversal_zeroed_textures): the memory is zero, and in a format
// without compression metadata any layout of zero bytes reads as zero; a GPU that keeps metadata for shader-read textures
// could read otherwise, which D3D12 leaves undefined for a placed texture. The zeroed upload pool: UPLOAD buffers, in system memory.
// traversal_zeroed_reuse: the zeroed buffer and upload pools keep free lists like the others, and a freed range, once
// settled, is zeroed again on the pool thread before it goes back: an UPLOAD one by a memset through its heap's alias
// buffer, mapped for good; a DEFAULT one by copies from a zero buffer of ours into its heap's alias, on a COPY queue of
// ours, given back once our fence is past them. Textures are never zeroed again: their layouts are the driver's.
enum { P_BUFFERS, P_TEXTURES, P_UPLOAD, P_READBACK, P_ZEROED, P_ZTEX, P_ZUP, POOLS };
static const uint64_t kPage = D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT;
static const auto kBuffers = D3D12_HEAP_FLAG_ALLOW_ONLY_BUFFERS | D3D12_HEAP_FLAG_CREATE_NOT_ZEROED;
// never destroyed: a placed resource's last Release can come during the runtime's teardown
static std::mutex& g_pmx = *new std::mutex;
static std::condition_variable& g_pcv = *new std::condition_variable;
static Pool* const g_pool = new Pool[POOLS]{
    {"buffers", D3D12_HEAP_TYPE_DEFAULT, kBuffers, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, true},
    {"textures", D3D12_HEAP_TYPE_DEFAULT, D3D12_HEAP_FLAG_ALLOW_ONLY_NON_RT_DS_TEXTURES | D3D12_HEAP_FLAG_CREATE_NOT_ZEROED, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, true},
    {"upload buffers", D3D12_HEAP_TYPE_UPLOAD, kBuffers, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL},
    {"readback buffers", D3D12_HEAP_TYPE_READBACK, kBuffers, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL},
    {"zeroed buffers", D3D12_HEAP_TYPE_DEFAULT, D3D12_HEAP_FLAG_ALLOW_ONLY_BUFFERS, DXGI_MEMORY_SEGMENT_GROUP_LOCAL},
    {"zeroed textures", D3D12_HEAP_TYPE_DEFAULT, D3D12_HEAP_FLAG_ALLOW_ONLY_NON_RT_DS_TEXTURES, DXGI_MEMORY_SEGMENT_GROUP_LOCAL},
    {"zeroed upload buffers", D3D12_HEAP_TYPE_UPLOAD, D3D12_HEAP_FLAG_ALLOW_ONLY_BUFFERS, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL}};
static Pool& g_zeroed = g_pool[P_ZEROED];
// a zeroed pool: its free memory is zero
static bool zero(const Pool& p) { return &p == &g_zeroed || &p == &g_pool[P_ZTEX] || &p == &g_pool[P_ZUP]; }
// traversal_zeroed_reuse: g_zreuse_buf false at the pool's start when our COPY queue can't be made (no heap yet then)
static std::atomic<bool> g_zreuse_buf;
static bool g_zreuse_up;
static bool rezero(const Pool& p) { return &p == &g_zeroed ? g_zreuse_buf.load(std::memory_order_relaxed) : &p == &g_pool[P_ZUP] && g_zreuse_up; }
// a zeroed pool whose ranges are used once, from its heaps' fresh tails
static bool once(const Pool& p) { return zero(p) && !rezero(p); }
static uint64_t g_pool_bytes;  // under g_pmx: every heap's size
static std::atomic<bool> g_on;
static bool g_uav_tex;
static uint64_t g_heap_bytes, g_max_pool, g_max_res;
static int g_spare;
static uint64_t spare_bytes(const Pool& p) { return (p.type == D3D12_HEAP_TYPE_DEFAULT ? g_spare : 1) * g_heap_bytes; }
// the pool's: the device of the game's latest command queue until the first placement, then that one's for good (a
// feature probe's device makes none; an overlay's or launcher's short-lived one may come first)
static std::atomic<ID3D12Device*> g_dev;
static std::atomic<ID3D12Device*> g_next_dev;  // held: a later game queue's device, for the pool thread to move to
static std::atomic<bool> g_bound;               // under g_pmx: a range was taken, the device stays
static std::atomic<ID3D12Device*> g_queue_dev;  // the device of the game's latest command queue
static thread_local bool t_own_queue;           // the proxy's own queue (entry_hooks), not the game's
// UAV buffers stay committed by default: engines carve acceleration structures out of plain UAV buffers, and the DXR spec
// forbids acceleration structure and other buffers overlapping in placed memory; AMD removes the device (INVALID_CALL)
static bool g_uav_buf;
// A freed range waits this many frames before it's taken again: the game releases a resource once its own fence says the
// GPU is done, but queued work may still name the old memory, which the OS keeps for a committed resource until then.
// The frames are only a minimum: the range also waits for the GPU (Track).
static int g_reuse_frames = 3;
// No frame for 500 ms (none yet, or presents the proxy doesn't see, as beside some upscaler mods): it waits this long instead.
static int g_reuse_ms = 50;
static std::atomic<bool> g_by_time;         // a range or heap was settled by time: logged once
// Under g_pmx: what was freed at that frame and tick may be used again.
static bool settled(uint64_t frame, uint64_t tick) {
    if (g_frames.load(std::memory_order_relaxed) >= frame + g_reuse_frames) return true;
    const uint64_t now = GetTickCount64(), last = g_frame_tick.load(std::memory_order_relaxed);
    if ((last && now < last + 500) || now < tick + g_reuse_ms) return false;
    return g_by_time.store(true, std::memory_order_relaxed), true;
}
// The GPU's side of a free: each game queue gets a fence of ours, signalled after each of its ExecuteCommandLists with a
// value reserved before the submit. A free records every queue's last reserved value its fence hasn't reached (a Snap),
// and the range waits until each fence has (as well as the frame / time minimum): the game's work submitted before the
// free is done, on every queue. A queue made after the free can't name the range; a released queue leaves the set, its
// fence kept by the snaps that name it. A removed device's fences read UINT64_MAX: everything passes.
struct Track {
    ID3D12Fence* f;                 // null: blind
    std::mutex mx;                  // a submit and its signal together, in value order
    std::atomic<uint64_t> sent{0};  // reserved for the latest submit
    std::atomic<uint64_t> seen{0};  // the fence's completed value when last read
    bool blind = false;             // a game queue whose submits we can't fence: counted in g_blind while it lives
    void* ecl = nullptr;            // its real ExecuteCommandLists at track() (PFN_Ecl), null: none of ours
    // blind until a submit of it reaches hk_ecl: its slot 10 at track() was a hooker's or an overlay copy's, which may
    // chain to ours (if one does, each of its submits does)
    std::atomic<bool> unconfirmed{false};
    // under mx: a failed signal's queue, held (it can't end meanwhile) for the pool thread to signal sent again, from
    // retry_at with a backoff doubling to 1 s
    ID3D12CommandQueue* retry = nullptr;
    uint64_t retry_at = 0, backoff = 0;
    explicit Track(ID3D12Fence* f) : f(f) {}
    ~Track() {
        if (f) f->Release();
    }
    bool done(uint64_t v) {
        if (seen.load(std::memory_order_relaxed) >= v) return true;
        const uint64_t c = f->GetCompletedValue();
        for (uint64_t o = seen.load(std::memory_order_relaxed); c > o && !seen.compare_exchange_weak(o, c);) {}
        return c >= v;
    }
};
using Snap = std::vector<std::pair<std::shared_ptr<Track>, uint64_t>>;
// Live game queues we can't fence (an ExecuteCommandLists slot that isn't our hook, no fence): while any lives nothing
// is placed and no freed range comes back, as their work could still name it.
static std::atomic<int> g_blind;
static std::atomic<int> g_retries;  // tracks with a failed signal
static std::mutex& g_tmx = *new std::mutex;
static std::unordered_map<void*, std::shared_ptr<Track>>& g_tracks = *new std::unordered_map<void*, std::shared_ptr<Track>>;  // under g_tmx, by queue
static Snap snap() {
    Snap s;
    std::lock_guard l(g_tmx);
    for (auto& [q, t] : g_tracks)
        if (const uint64_t v = t->sent.load(); t->seen.load(std::memory_order_relaxed) < v) s.push_back({t, v});
    return s;
}
// what the snap names is done on the GPU (the done entries dropped)
static bool gpu_done(Snap& s) {
    std::erase_if(s, [](auto& e) { return e.first->done(e.second); });
    return s.empty();
}
struct Pending { Pool* p; Heap* h; uint64_t at, size, frame, tick; Snap gpu; };
static std::deque<Pending>& g_pending = *new std::deque<Pending>;  // under g_pmx: freed ranges, oldest first
static std::vector<Pending>& g_dirty = *new std::vector<Pending>;  // under g_pmx: settled ranges of a re-zeroed pool, to zero
static size_t g_zpending;  // under g_pmx: g_pending's ranges of a re-zeroed pool
static std::atomic<uint64_t> g_rezeroed_gpu, g_rezeroed_cpu, g_fenced;  // bytes zeroed again; ranges waiting for our fence
static std::atomic<bool> g_ever;  // a resource was placed: residency calls are filtered
static std::atomic<uint64_t> g_placed, g_why[WHYS], g_filtered;
static std::atomic<HRESULT> g_last_hr;
// a create left committed, and why
static bool no(Why w) { return g_why[w].fetch_add(1, std::memory_order_relaxed), g_frame_why[w].fetch_add(1, std::memory_order_relaxed), false; }

// {6A2F7C14-93B5-4E0D-8C1A-5F3E9B7D2A40}: the private data of a placed resource, its range
static const GUID kPlaced = {0x6a2f7c14, 0x93b5, 0x4e0d, {0x8c, 0x1a, 0x5f, 0x3e, 0x9b, 0x7d, 0x2a, 0x40}};

// What an empty heap adds to its pool's free bytes: a zeroed one only its fresh tail.
static uint64_t avail(const Pool& p, const Heap& h) { return once(p) ? g_heap_bytes - h.fresh : g_heap_bytes; }

static bool take(Pool& p, uint64_t size, uint64_t align, Heap*& out, uint64_t& at) {
    if (once(p)) {  // sizes and the alignment are whole pages: the tail stays page aligned
        for (auto& h : p.heaps)
            if (h.fresh + size <= g_heap_bytes) return at = h.fresh, h.fresh += size, h.used += size, p.free_bytes -= size, out = &h, true;
        return false;
    }
    for (auto& h : p.heaps)
        for (auto it = h.free.begin(); it != h.free.end(); ++it) {
            const uint64_t off = it->first, len = it->second, s = (off + align - 1) / align * align;
            if (s + size > off + len) continue;
            h.free.erase(it);
            if (s > off) h.free[off] = s - off;
            if (s + size < off + len) h.free[s + size] = off + len - s - size;
            h.used += size, p.free_bytes -= size;
            out = &h, at = s;
            return true;
        }
    return false;
}
static void give(Pool& p, Heap& h, uint64_t at, uint64_t size) {
    if (once(p)) return void(h.used -= size);  // written once: never zeroed again
    auto it = h.free.emplace(at, size).first;
    if (auto n = std::next(it); n != h.free.end() && at + size == n->first) it->second += n->second, h.free.erase(n);
    if (it != h.free.begin())
        if (auto pr = std::prev(it); pr->first + pr->second == at) pr->second += it->second, h.free.erase(it);
    h.used -= size, p.free_bytes += size;
}
// Under g_pmx: the freed ranges that have settled go back to their heaps (a re-zeroed pool's to the pool thread first).
static void reap() {
    if (g_blind.load(std::memory_order_relaxed)) return;
    while (!g_pending.empty() && settled(g_pending.front().frame, g_pending.front().tick) && gpu_done(g_pending.front().gpu)) {
        auto& f = g_pending.front();
        if (rezero(*f.p)) g_dirty.push_back({f.p, f.h, f.at, f.size, f.frame, f.tick}), --g_zpending;
        else give(*f.p, *f.h, f.at, f.size);
        g_pending.pop_front();
    }
}

// traversal_dred=1: every placement, live or freed lately, to say what a page fault's address held.
struct Placement {
    const char* pool;
    int heap;
    uint64_t at, size, va;
    D3D12_RESOURCE_DIMENSION dim;
    DXGI_FORMAT fmt;
    D3D12_RESOURCE_FLAGS flags;
};
static bool g_dred;
struct Range;
static std::set<Range*>& g_live = *new std::set<Range*>;              // under g_pmx, with g_dred
static std::deque<Placement>& g_freed = *new std::deque<Placement>;  // under g_pmx, with g_dred: the last 256 freed

// Held only by the placed resource's private data: the runtime releases it as the resource is destroyed.
struct Range final : IUnknown {
    std::atomic<ULONG> refs{1};
    Pool* p;
    Heap* h;
    uint64_t at, size;
    Placement info{};
    Range(Pool* p, Heap* h, uint64_t at, uint64_t size) : p(p), h(h), at(at), size(size) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** pp) override {
        if (!pp) return E_POINTER;
        if (id != __uuidof(IUnknown)) return *pp = nullptr, E_NOINTERFACE;
        return *pp = this, AddRef(), S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG n = --refs;
        if (!n) {
            Snap gpu = snap();
            {
                std::lock_guard l(g_pmx);
                // still counted in the heap's use while it waits: a heap with a waiting range is never released
                g_pending.push_back({p, h, at, size, h->last_free = g_frames.load(std::memory_order_relaxed), h->last_tick = GetTickCount64(), std::move(gpu)});
                if (rezero(*p)) ++g_zpending;
                if (!g_reuse_frames) reap();
                if (g_dred && g_live.erase(this)) {
                    g_freed.push_back(info);
                    if (g_freed.size() > 256) g_freed.pop_front();
                }
            }
            g_pcv.notify_one();
            delete this;
        }
        return n;
    }
};

using PFN_CR = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, const D3D12_HEAP_PROPERTIES*, D3D12_HEAP_FLAGS, const D3D12_RESOURCE_DESC*,
                                           D3D12_RESOURCE_STATES, const D3D12_CLEAR_VALUE*, REFIID, void**);
using PFN_CR1 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device4*, const D3D12_HEAP_PROPERTIES*, D3D12_HEAP_FLAGS, const D3D12_RESOURCE_DESC*,
                                            D3D12_RESOURCE_STATES, const D3D12_CLEAR_VALUE*, ID3D12ProtectedResourceSession*, REFIID, void**);
using PFN_CR2 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device8*, const D3D12_HEAP_PROPERTIES*, D3D12_HEAP_FLAGS, const D3D12_RESOURCE_DESC1*,
                                            D3D12_RESOURCE_STATES, const D3D12_CLEAR_VALUE*, ID3D12ProtectedResourceSession*, REFIID, void**);
using PFN_CR3 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device10*, const D3D12_HEAP_PROPERTIES*, D3D12_HEAP_FLAGS, const D3D12_RESOURCE_DESC1*,
                                            D3D12_BARRIER_LAYOUT, const D3D12_CLEAR_VALUE*, ID3D12ProtectedResourceSession*, UINT32,
                                            const DXGI_FORMAT*, REFIID, void**);
using PFN_Placed = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, ID3D12Heap*, UINT64, const D3D12_RESOURCE_DESC*, D3D12_RESOURCE_STATES,
                                               const D3D12_CLEAR_VALUE*, REFIID, void**);
using PFN_Placed1 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device8*, ID3D12Heap*, UINT64, const D3D12_RESOURCE_DESC1*, D3D12_RESOURCE_STATES,
                                                const D3D12_CLEAR_VALUE*, REFIID, void**);
using PFN_Placed2 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device10*, ID3D12Heap*, UINT64, const D3D12_RESOURCE_DESC1*, D3D12_BARRIER_LAYOUT,
                                                const D3D12_CLEAR_VALUE*, UINT32, const DXGI_FORMAT*, REFIID, void**);
using PFN_Reserved = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, const D3D12_RESOURCE_DESC*, D3D12_RESOURCE_STATES, const D3D12_CLEAR_VALUE*, REFIID, void**);
using PFN_Reserved1 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device4*, const D3D12_RESOURCE_DESC*, D3D12_RESOURCE_STATES, const D3D12_CLEAR_VALUE*,
                                                  ID3D12ProtectedResourceSession*, REFIID, void**);
using PFN_Reserved2 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device10*, const D3D12_RESOURCE_DESC*, D3D12_BARRIER_LAYOUT, const D3D12_CLEAR_VALUE*,
                                                  ID3D12ProtectedResourceSession*, UINT32, const DXGI_FORMAT*, REFIID, void**);
using PFN_Heap = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, const D3D12_HEAP_DESC*, REFIID, void**);
using PFN_Heap1 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device4*, const D3D12_HEAP_DESC*, ID3D12ProtectedResourceSession*, REFIID, void**);
using PFN_Residency = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, UINT, ID3D12Pageable* const*);
using PFN_Enqueue = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device3*, D3D12_RESIDENCY_FLAGS, UINT, ID3D12Pageable* const*, ID3D12Fence*, UINT64);
using PFN_Priority = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device1*, UINT, ID3D12Pageable* const*, const D3D12_RESIDENCY_PRIORITY*);
// vtslots.cpp checks these against d3d12.h at compile time
enum { SLOT_CR = 27, SLOT_HEAP = 28, SLOT_PLACED = 29, SLOT_RESERVED = 30, SLOT_MAKERESIDENT = 34, SLOT_EVICT = 35, SLOT_ENQUEUE = 50,
       SLOT_PRIORITY = 46, SLOT_CR1 = 53, SLOT_HEAP1 = 54, SLOT_RESERVED1 = 55, SLOT_CR2 = 69, SLOT_PLACED1 = 70, SLOT_CR3 = 76, SLOT_PLACED2 = 77, SLOT_RESERVED2 = 78 };
static PFN_CR o_cr;
static PFN_CR1 o_cr1;
static PFN_CR2 o_cr2;
static PFN_CR3 o_cr3;
static PFN_Placed o_placed;
static PFN_Placed1 o_placed1;
static PFN_Placed2 o_placed2;
static PFN_Reserved o_reserved;
static PFN_Reserved1 o_reserved1;
static PFN_Reserved2 o_reserved2;
static PFN_Heap o_heap;
static PFN_Heap1 o_heap1;
static PFN_Residency o_resident, o_evict;
static PFN_Enqueue o_enqueue;
static PFN_Priority o_priority;
using PFN_Queue = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, const D3D12_COMMAND_QUEUE_DESC*, REFIID, void**);
using PFN_Queue1 = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device9*, const D3D12_COMMAND_QUEUE_DESC*, REFIID, REFIID, void**);
enum { SLOT_QUEUE = 8, SLOT_QUEUE1 = 75 };  // vtslots.cpp checks these
static PFN_Queue o_queue;
static PFN_Queue1 o_queue1;
static void pool_main();
static void pool_thread() {
    try {
        std::thread(pool_main).detach();
    } catch (...) {
        logf("traversal: no pool thread, every create stays committed");
    }
}
// At a game queue while nothing is placed: the pool's device, its heaps made ahead on the pool thread; a queue on
// another device later, the pool thread moves there.
static void adopt(ID3D12Device* dev) {
    ID3D12Device* drop;
    {
        std::lock_guard l(g_pmx);  // with moved(): g_dev and g_next_dev change together
        if (g_bound) return;
        if (!g_dev.load()) return dev->AddRef(), g_dev = dev, pool_thread();
        drop = g_next_dev.exchange(dev == g_dev.load() ? nullptr : (dev->AddRef(), dev));
    }
    if (drop) drop->Release();
    g_pcv.notify_one();
}
static bool g_copy_high;  // traversal_copy_high
static std::atomic<bool> g_high_refused;  // logged once
// create(desc): the game's queue, a COPY one at NORMAL made at HIGH under traversal_copy_high
using PFN_Ecl = void(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);
enum { SLOT_ECL = 10 };  // vtslots.cpp checks it
// ExecuteCommandLists' original per queue vtable: queues of another implementation (a frame generation's present queue,
// HelixSR's "AMD FSR PresentQueue") have their own slot 10. Only vtables in a loaded module's image are recorded and
// patched (they live for the process: nothing dangles); entries are only added, under g_tmx, each published before its
// slot points at hk_ecl. A copy an overlay made (other slots the same) is never patched or recorded: its queue's track
// keeps the real original of the vtable it copies.
struct EclVt {
    std::atomic<void**> vt;
    std::atomic<PFN_Ecl> orig;
};
static EclVt g_eclvt[8];
static std::atomic<int> g_neclvt;
// The recorded vtable's real original, or the one of the single recorded vtable vt copies (every other slot, 0-18 but
// 10, the same); null: neither, or two could be it.
static PFN_Ecl ecl_of(void** vt) {
    const int n = g_neclvt.load(std::memory_order_acquire);
    for (int i = 0; i < n; ++i)
        if (g_eclvt[i].vt.load(std::memory_order_relaxed) == vt) return g_eclvt[i].orig.load(std::memory_order_relaxed);
    PFN_Ecl o = nullptr;
    int same = 0;
    for (int i = 0; i < n; ++i) {
        void** r = g_eclvt[i].vt.load(std::memory_order_relaxed);
        bool all = true;
        for (int k = 0; k <= 18 && all; ++k) all = k == SLOT_ECL || r[k] == vt[k];
        if (all) o = g_eclvt[i].orig.load(std::memory_order_relaxed), ++same;
    }
    return same == 1 ? o : nullptr;
}
// An untracked queue's (ours, any other): a call into hk_ecl comes from a recorded vtable or a copy of one.
static PFN_Ecl ecl_orig(ID3D12CommandQueue* q) {
    if (const PFN_Ecl o = ecl_of(*(void***)q)) return o;
    logf("traversal: ExecuteCommandLists of queue %p (vtable %p) has no original of ours: stopping", (void*)q, (void*)*(void***)q);
    __fastfail(FAST_FAIL_INVALID_ARG);  // never a wrong original
    return nullptr;
}
static void STDMETHODCALLTYPE hk_ecl(ID3D12CommandQueue* q, UINT n, ID3D12CommandList* const* l);
// Under g_tmx: a vtable in a module image, not recorded and not a copy of a recorded one (ecl_of null), recorded and its
// slot 10 hooked; false when the table is full.
static bool patch_ecl(void** vt) {
    const int n = g_neclvt.load(std::memory_order_relaxed);
    if (n == 8 || vt[SLOT_ECL] == (void*)hk_ecl) return false;
    g_eclvt[n].vt.store(vt, std::memory_order_relaxed), g_eclvt[n].orig.store((PFN_Ecl)vt[SLOT_ECL], std::memory_order_relaxed);
    g_neclvt.store(n + 1, std::memory_order_release);
    DWORD old;
    if (!VirtualProtect(&vt[SLOT_ECL], sizeof(void*), PAGE_READWRITE, &old)) return true;  // recorded, its slot not ours: blind
    vt[SLOT_ECL] = (void*)hk_ecl;
    VirtualProtect(&vt[SLOT_ECL], sizeof(void*), old, &old);
    return true;
}
// The queue our hook is submitting to on this thread: a layer below that submits to it again goes straight through (a
// nested submit to another queue is fenced).
static thread_local void* t_ecl_q;
// The submit, under the queue's lock taken by hand: a structured exception out of it that the game survives (/EHsc runs
// no destructor for one, so no C++ object here: __finally, as present_in does) leaves neither the queue mark nor the
// lock behind; its reserved value goes unsignalled, and the queue's next submit covers it.
static void ecl_in(PFN_Ecl o, ID3D12CommandQueue* q, UINT n, ID3D12CommandList* const* l, std::mutex* m) {
    void* prev = t_ecl_q;
    t_ecl_q = q;
    __try {
        o(q, n, l);
    } __finally {
        t_ecl_q = prev;
        if (AbnormalTermination()) m->unlock();
    }
}
static void STDMETHODCALLTYPE hk_ecl(ID3D12CommandQueue* q, UINT n, ID3D12CommandList* const* l) {
    std::shared_ptr<Track> t;
    {
        std::lock_guard g(g_tmx);
        if (auto it = g_tracks.find(q); it != g_tracks.end()) t = it->second;
    }
    // a game queue's real one, kept at track() (a re-entry too: never an overlay's hook); else (our queues, any
    // untracked one) by its vtable
    const PFN_Ecl o_ecl = t && t->ecl ? (PFN_Ecl)t->ecl : ecl_orig(q);
    if (t_ecl_q == q) return o_ecl(q, n, l);
    if (!t || !t->f) return o_ecl(q, n, l);  // not a game queue's (ours, or one made with suballocate off), or blind
    if (t->unconfirmed.load(std::memory_order_relaxed)) {  // its first submit reached us: so does each, fenced from here
        bool now = false;
        {
            std::lock_guard g(g_tmx);
            if (t->unconfirmed.exchange(false) && t->blind) t->blind = false, --g_blind, now = true;
        }
        static std::atomic<bool> said;
        if (now && !said.exchange(true)) logf("traversal: game queue %p's submits reach our hook through another's: fenced", (void*)q);
    }
    // held across the submit: reserve, submit and signal in one order (reserved 5 and 6 on two threads, 6 signalled, a
    // free recording 6, then 5 signalled would take the fence back below the record)
    t->mx.lock();
    const uint64_t v = t->sent.load(std::memory_order_relaxed) + 1;
    t->sent.store(v);  // before the submit: a free meanwhile waits for it
    ecl_in(o_ecl, q, n, l, &t->mx);
    // failed: the pool thread signals again (a later submit's signal covers it too); a removed device's fence reads UINT64_MAX
    if (HRESULT hr = q->Signal(t->f, v); FAILED(hr)) {
        if (!t->retry && hr != DXGI_ERROR_DEVICE_REMOVED) q->AddRef(), t->retry = q, t->backoff = 10, t->retry_at = GetTickCount64() + 10, g_retries.fetch_add(1);
        static std::atomic<bool> said;
        if (!said.exchange(true)) logf("traversal: our fence's signal on game queue %p failed (0x%08x): signalled again by the pool thread", (void*)q, (unsigned)hr);
    } else if (t->retry)
        t->retry->Release(), t->retry = nullptr, g_retries.fetch_sub(1);  // the game holds the queue: not its last reference
    t->mx.unlock();
}
// {3B8E51D2-7C4A-4F69-9E1D-2A6C0F8B4E17}: a tracked queue's private data; its release (the queue's end) drops the queue
static const GUID kTracked = {0x3b8e51d2, 0x7c4a, 0x4f69, {0x9e, 0x1d, 0x2a, 0x6c, 0x0f, 0x8b, 0x4e, 0x17}};
struct Untrack final : IUnknown {
    std::atomic<ULONG> refs{1};
    void* q;
    const Track* mine;  // the queue's track: a queue made later at the same address has another
    Untrack(void* q, const Track* mine) : q(q), mine(mine) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** pp) override {
        if (!pp) return E_POINTER;
        if (id != __uuidof(IUnknown)) return *pp = nullptr, E_NOINTERFACE;
        return *pp = this, AddRef(), S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG n = --refs;
        if (!n) {
            std::shared_ptr<Track> t;  // its fence released outside g_tmx, when no free's record names it
            {
                std::lock_guard l(g_tmx);
                if (auto it = g_tracks.find(q); it != g_tracks.end() && it->second.get() == mine) {
                    if (it->second->blind) --g_blind;
                    t = std::move(it->second), g_tracks.erase(it);
                }
            }
            delete this;
        }
        return n;
    }
};
// A game queue, with suballocate on at the start: its fence, and ExecuteCommandLists hooked. A queue whose slot isn't our
// hook (another vtable: patch keeps one original) or without a fence is blind while it lives; one whose end we can't see
// (no private data) for the session.
static void track(ID3D12Device* dev, IUnknown* made) {
    ID3D12CommandQueue* q = nullptr;
    if (FAILED(made->QueryInterface(IID_PPV_ARGS(&q)))) return g_blind.fetch_add(1), logf("traversal: a game queue without ID3D12CommandQueue: nothing placed or reused from now on");
    ID3D12Fence* f = nullptr;
    const HRESULT fh = dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f));
    auto t = std::make_shared<Track>(SUCCEEDED(fh) ? f : nullptr);
    auto* u = new Untrack(q, t.get());
    void** vt = *(void***)q;
    HMODULE m;  // outside g_tmx: it takes the loader lock
    const bool image = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)vt, &m);
    {
        std::lock_guard l(g_tmx);
        if (t->f) {
            PFN_Ecl o = ecl_of(vt);  // recorded, or a copy of a recorded one: never patched again
            if (!o && image && patch_ecl(vt)) o = ecl_of(vt);
            t->ecl = (void*)o;
        }
        // slot 10 not ours: a hooker's or a copy's that may chain to us, blind until a submit reaches hk_ecl; no original
        // of ours (a copy made before our hook, a vtable outside any image, a ninth): blind while it lives
        if ((t->blind = !t->f || !t->ecl || vt[SLOT_ECL] != (void*)hk_ecl)) ++g_blind;
        t->unconfirmed = t->f && t->ecl && vt[SLOT_ECL] != (void*)hk_ecl;
        auto& at = g_tracks[q];
        if (at && at->blind) --g_blind;  // a stale track at this address, before its Untrack (which then leaves ours)
        at = t;
    }
    if (t->blind) {
        static std::atomic<bool> said;
        if (!said.exchange(true))
            logf("traversal: game queue %p can't be fenced (%s): nothing placed or reused while it lives", (void*)q,
                 !t->f ? std::format("no fence, 0x{:08x}", (unsigned)fh).c_str()
                 : t->ecl ? "its ExecuteCommandLists is another hooker's or an overlay copy's: fenced once a submit reaches ours"
                          : "no original of ours: a vtable copied before our hook, outside any module, or a ninth");
    }
    if (const HRESULT hr = q->SetPrivateDataInterface(kTracked, u); FAILED(hr))
        g_blind.fetch_add(1), logf("traversal: game queue %p's end can't be seen (0x%08x): nothing placed or reused from now on", (void*)q, (unsigned)hr);
    u->Release();  // the queue holds it; failed: gone, and its track with it (g_blind keeps the queue counted)
    q->Release();
}

template <class F> static HRESULT game_queue(ID3D12Device* dev, const D3D12_COMMAND_QUEUE_DESC* d, void** pp, F&& create) {
    if (t_own_queue) return create(d);
    HRESULT hr;
    if (g_copy_high && d && pp && d->Type == D3D12_COMMAND_LIST_TYPE_COPY && d->Priority == D3D12_COMMAND_QUEUE_PRIORITY_NORMAL) {
        D3D12_COMMAND_QUEUE_DESC hi = *d;
        hi.Priority = D3D12_COMMAND_QUEUE_PRIORITY_HIGH;
        if (SUCCEEDED(hr = create(&hi))) logf("traversal: a COPY queue of device %p made at HIGH priority (traversal_copy_high)", (void*)dev);
        else if (!g_high_refused.exchange(true))
            logf("traversal: a COPY queue at HIGH priority refused (0x%08x), made at NORMAL", (unsigned)hr);
        if (FAILED(hr)) hr = create(d);
    } else
        hr = create(d);
    if (SUCCEEDED(hr) && pp && *pp) {
        g_queue_dev = dev;
        if (g_heap_bytes) track(dev, (IUnknown*)*pp);  // suballocate on at the start
        if (g_on.load(std::memory_order_relaxed) && !g_bound.load()) adopt(dev);
    }
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_queue(ID3D12Device* dev, const D3D12_COMMAND_QUEUE_DESC* d, REFIID riid, void** pp) {
    return game_queue(dev, d, pp, [&](const D3D12_COMMAND_QUEUE_DESC* q) { return o_queue(dev, q, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_queue1(ID3D12Device9* dev, const D3D12_COMMAND_QUEUE_DESC* d, REFIID creator, REFIID riid, void** pp) {
    return game_queue(dev, d, pp, [&](const D3D12_COMMAND_QUEUE_DESC* q) { return o_queue1(dev, q, creator, riid, pp); });
}

static std::mutex& g_grow_mx = *new std::mutex;  // one heap made at a time
static std::atomic<int> g_heap_fails;
static std::atomic<uint64_t> g_sync_grows;
static std::atomic<IDXGIAdapter3*> g_adapter;  // set by pool_main before any grow

// Under g_grow_mx: one more heap for p, while the pool's cap and its segment's budget (usage plus a heap within 90%) allow.
// sync: a create found no room, and makes the heap on its own thread.
static uint64_t g_pool_peak;  // under g_pmx

// under g_pmx: for the pool thread to release, a heap and its alias buffer (re-zeroed pools), the alias first
static std::vector<std::pair<ID3D12Heap*, ID3D12Resource*>>& g_released = *new std::vector<std::pair<ID3D12Heap*, ID3D12Resource*>>;

// Under g_grow_mx: an empty heap of any pool, past the reuse delay, out of the pool to make room under max_pool_mb; the pool
// thread releases it.
static bool drop_empty(const Pool& for_pool) {
    ID3D12Heap* h = nullptr;
    const char* name = nullptr;
    {
        std::lock_guard l(g_pmx);
        for (int i = 0; i < POOLS && !h; ++i)
            for (auto it = g_pool[i].heaps.begin(); it != g_pool[i].heaps.end(); ++it)
                if (!it->used && settled(it->last_free, it->last_tick)) {
                    h = it->h, name = g_pool[i].name;
                    g_released.push_back({h, it->alias});  // not released on this game thread, nor under g_pmx
                    g_pool[i].free_bytes -= avail(g_pool[i], *it), g_pool_bytes -= g_heap_bytes, g_pool[i].heaps.erase(it);
                    break;
                }
    }
    if (!h) return false;
    g_pcv.notify_one();
    logf("traversal: an empty %s heap released for a %s create (pool at max_pool_mb)", name, for_pool.name);
    return true;
}

static uint64_t g_test_budget;  // traversal_test_budget_mb: the local budget as if it were this (0: as it is)
// The segment's usage and budget; false when they can't be read.
static bool budget(IDXGIAdapter3* a, DXGI_MEMORY_SEGMENT_GROUP seg, DXGI_QUERY_VIDEO_MEMORY_INFO& mi) {
    if (!a || FAILED(a->QueryVideoMemoryInfo(0, seg, &mi)) || !mi.Budget) return false;
    if (g_test_budget && seg == DXGI_MEMORY_SEGMENT_GROUP_LOCAL) mi.Budget = g_test_budget;
    return true;
}

enum Grow { G_MADE, G_CAP, G_BUDGET, G_FAILED, G_NONE };  // after G_MADE, in the order of W_ROOM_CAP ..
static Grow grow(Pool& p, bool sync) {
    IDXGIAdapter3* a = g_adapter.load();
    DXGI_QUERY_VIDEO_MEMORY_INFO mi = {};
    if (g_heap_fails >= 3) return G_FAILED;
    if (!budget(a, p.seg, mi)) return G_NONE;
    if (mi.CurrentUsage + g_heap_bytes > mi.Budget / 100 * 90) return G_BUDGET;
    {
        std::lock_guard l(g_pmx);
        if (g_pool_bytes + g_heap_bytes > g_max_pool) return G_CAP;
    }
    D3D12_HEAP_DESC hd = {g_heap_bytes, {p.type}, 0, p.flags};
    ID3D12Heap* h = nullptr;
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    HRESULT hr = o_heap(g_dev.load(), &hd, IID_PPV_ARGS(&h));
    QueryPerformanceCounter(&t1);
    if (FAILED(hr)) return logf("traversal: a %s heap failed (0x%08x)%s", p.name, (unsigned)hr, ++g_heap_fails == 3 ? ", no more heaps" : ""), G_FAILED;
    static int ids;
    const int id = ++ids;
    h->SetName(std::format(L"SCSKiller traversal {} heap {}", widen(p.name), id).c_str());  // the name DRED's allocation list shows
    ID3D12Resource* alias = nullptr;
    void* cpu = nullptr;
    if (rezero(p)) {  // made on the pool thread only: a create that finds a zeroed pool without room stays committed
        const bool up = p.type == D3D12_HEAP_TYPE_UPLOAD;
        const D3D12_RESOURCE_DESC bd = {D3D12_RESOURCE_DIMENSION_BUFFER, 0, g_heap_bytes, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1, 0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR};
        const D3D12_RANGE none = {};
        hr = o_placed(g_dev.load(), h, 0, &bd, up ? D3D12_RESOURCE_STATE_GENERIC_READ : D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&alias));
        if (SUCCEEDED(hr) && up) hr = alias->Map(0, &none, &cpu);
        if (FAILED(hr)) {
            if (alias) alias->Release();
            h->Release();
            return logf("traversal: a %s heap's alias buffer failed (0x%08x)%s", p.name, (unsigned)hr, ++g_heap_fails == 3 ? ", no more heaps" : ""), G_FAILED;
        }
    }
    size_t count;
    uint64_t pool_mb;
    {
        std::lock_guard l(g_pmx);
        p.heaps.push_back({h, {}, 0, id, 0, 0});
        p.heaps.back().alias = alias, p.heaps.back().cpu = (uint8_t*)cpu;
        if (!once(p)) p.heaps.back().free[0] = g_heap_bytes;
        p.free_bytes += g_heap_bytes, g_pool_bytes += g_heap_bytes;
        g_pool_peak = std::max(g_pool_peak, g_pool_bytes);
        count = p.heaps.size(), pool_mb = g_pool_bytes >> 20;
    }
    if (sync) g_sync_grows.fetch_add(1, std::memory_order_relaxed);
    logf("traversal: %s heap %zu (id %d) made in %.1f ms%s, pool %llu MB, %s usage %llu of %llu MB, qpc %lld", p.name, count, id, (t1.QuadPart - t0.QuadPart) * 1000.0 / g_freq,
         sync ? " by a create that found no room" : "", pool_mb, p.seg == DXGI_MEMORY_SEGMENT_GROUP_LOCAL ? "local" : "non-local", mi.CurrentUsage >> 20, mi.Budget >> 20,
         t0.QuadPart);
    return G_MADE;
}

// Game descs a layer changed past what the pool takes (W_GROWN), by key(): committed from then on, without a placed try.
static std::unordered_set<uint64_t>& g_grown = *new std::unordered_set<uint64_t>;  // under g_pmx
static uint64_t key(const D3D12_RESOURCE_DESC& d) {  // FNV-1a over the fields (not the bytes: the padding is the game's)
    uint64_t k = 1469598103934665603ull;
    for (uint64_t v : {(uint64_t)d.Dimension, d.Alignment, d.Width, (uint64_t)d.Height, (uint64_t)d.DepthOrArraySize, (uint64_t)d.MipLevels,
                       (uint64_t)d.Format, (uint64_t)d.SampleDesc.Count, (uint64_t)d.SampleDesc.Quality, (uint64_t)d.Layout, (uint64_t)d.Flags})
        k = (k ^ v) * 1099511628211ull;
    return k;
}
// MipLevels 0 asks for the whole chain, which GetDesc gives as a count
static UINT16 mips(const D3D12_RESOURCE_DESC& d) {
    if (d.MipLevels || d.Dimension == D3D12_RESOURCE_DIMENSION_BUFFER) return d.MipLevels;
    UINT16 n = 0;
    for (uint64_t m = std::max({d.Width, (uint64_t)d.Height, d.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE3D ? (uint64_t)d.DepthOrArraySize : 1}); m; m >>= 1) ++n;
    return n;
}

// True when the create was placed (hr its result); false: the caller makes the committed one.
static bool place(ID3D12Device* dev, const D3D12_HEAP_PROPERTIES* hp, D3D12_HEAP_FLAGS hf, const D3D12_RESOURCE_DESC* d,
                  D3D12_RESOURCE_STATES st, const D3D12_CLEAR_VALUE* cv, REFIID riid, void** pp, HRESULT& hr) {
    if (!g_on.load(std::memory_order_relaxed) || !pp || !hp || !d || t_depth != 1) return false;  // pp null: the game only validates the desc
    if (g_blind.load(std::memory_order_relaxed)) return no(W_BLIND);
    const bool buffer = d->Dimension == D3D12_RESOURCE_DIMENSION_BUFFER, cpu = hp->Type == D3D12_HEAP_TYPE_UPLOAD || hp->Type == D3D12_HEAP_TYPE_READBACK;
    const bool zeroed = !(hf & D3D12_HEAP_FLAG_CREATE_NOT_ZEROED);  // memory the game reads as zero
    int pi = hp->Type == D3D12_HEAP_TYPE_DEFAULT ? (buffer ? P_BUFFERS : P_TEXTURES) : hp->Type == D3D12_HEAP_TYPE_UPLOAD ? P_UPLOAD
             : hp->Type == D3D12_HEAP_TYPE_READBACK ? P_READBACK : -1;
    if (pi == P_BUFFERS && zeroed && g_zeroed.on) pi = P_ZEROED;
    if (pi == P_TEXTURES && zeroed && g_pool[P_ZTEX].on && !(d->Flags & D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS)) pi = P_ZTEX;
    if (pi == P_UPLOAD && zeroed && g_pool[P_ZUP].on) pi = P_ZUP;
    if (pi < 0 || !g_pool[pi].on) return no(W_HEAP);
    if (hp->CPUPageProperty != D3D12_CPU_PAGE_PROPERTY_UNKNOWN || hp->MemoryPoolPreference != D3D12_MEMORY_POOL_UNKNOWN || hp->CreationNodeMask > 1 ||
        hp->VisibleNodeMask > 1)
        return no(W_NODE);
    if (zeroed && !zero(g_pool[pi]))  // a reused range isn't zeroed
        return no(pi == P_TEXTURES ? W_ZEROED_TEX : pi == P_UPLOAD ? W_ZEROED_UPLOAD : pi == P_READBACK ? W_ZEROED_READBACK : W_ZEROED);
    if (hf & ~D3D12_HEAP_FLAG_CREATE_NOT_ZEROED) return no(W_HEAP_FLAGS);
    if (d->Dimension == D3D12_RESOURCE_DIMENSION_UNKNOWN) return no(W_DIM);
    if (cv) return no(W_CLEAR);
    if (cpu && (!buffer || d->Flags)) return no(W_CPU);
    if ((d->Flags & ~(D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS | D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE)) || d->SampleDesc.Count > 1) return no(W_FLAGS);
    if ((d->Flags & D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS) && !(buffer ? g_uav_buf : g_uav_tex)) return no(buffer ? W_UAV_BUF : W_UAV_TEX);
    // the DXR spec: a placed acceleration structure buffer over the VA of a buffer that isn't one, or the reverse, is an error
    if (st & D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE) return no(W_ACCEL);
    if (dev != g_dev.load()) return no(g_dev.load() ? W_DEVICE : W_QUEUE);  // adopted at the game's first queue (game_queue)
    const D3D12_RESOURCE_ALLOCATION_INFO ai = dev->GetResourceAllocationInfo(0, 1, d);
    if (ai.SizeInBytes == UINT64_MAX) return no(W_ERROR);
    if (ai.SizeInBytes > g_max_res) return no(W_SIZE);
    if (ai.Alignment > kPage) return no(W_ALIGN);  // the heaps are 64 KB aligned
    Pool& pool = g_pool[pi];
    const uint64_t size = (ai.SizeInBytes + kPage - 1) / kPage * kPage;
    Heap* h = nullptr;
    uint64_t at = 0;
    bool got = false, low = false, grown;
    const uint64_t k = key(*d);
    // under g_pmx: the pool may have moved since the check above
    auto grab = [&] { return dev == g_dev.load() && take(pool, size, kPage, h, at) && (g_bound = true); };
    {
        std::lock_guard l(g_pmx);
        if (!(grown = g_grown.contains(k))) {
            reap();
            got = grab();
            low = pool.free_bytes < spare_bytes(pool);
        }
    }
    if (grown) return no(W_GROWN);
    if (low) g_pcv.notify_one();
    // the OS zeroing a heap here would cost the create more than its own committed one: the pool thread makes it
    if (!got && zero(pool)) return no(W_ZEROED_ROOM);
    // a burst outran the pool thread: one heap made here when there's still no room; while a heap is being made, committed
    // (a heap made under paging pressure can take far longer than the game's own create)
    if (!got) {
        std::unique_lock g(g_grow_mx, std::try_to_lock);
        if (!g) return no(W_ROOM_BUSY);
        {
            std::lock_guard l(g_pmx);
            got = grab();
        }
        if (!got) {
            Grow r = grow(pool, true);
            if (r == G_CAP && drop_empty(pool)) r = grow(pool, true);
            if (r != G_MADE) return no(Why(W_ROOM_CAP + r - G_CAP));
            std::lock_guard l(g_pmx);
            got = grab();
        }
    }
    if (!got) return no(W_ROOM);
    auto* range = new Range(&pool, h, at, size);
    ID3D12Resource* r = nullptr;
    HRESULT e = o_placed(dev, h->h, at, d, st, cv, IID_PPV_ARGS(&r));
    // a layer's CreatePlacedResource can change the desc (ReShade: an add-on's create_resource, RenoDX's format upgrades),
    // and the checks above saw the game's: what was made has to pass them too, with no flag the game's lacks (a UAV past
    // uav_*=0 or in the zeroed textures pool, an acceleration structure's neighbour), and fit the range, or it reaches the next
    D3D12_RESOURCE_DESC rd = *d;
    if (SUCCEEDED(e)) {
        rd = r->GetDesc();
        if (rd.Format != d->Format || rd.Width != d->Width || rd.Height != d->Height || rd.DepthOrArraySize != d->DepthOrArraySize ||
            rd.MipLevels != mips(*d) || rd.Flags != d->Flags || rd.Layout != d->Layout || rd.SampleDesc.Count != d->SampleDesc.Count ||
            rd.SampleDesc.Quality != d->SampleDesc.Quality || (rd.Alignment != d->Alignment && rd.Alignment != ai.Alignment) || rd.Dimension != d->Dimension)
            if (const D3D12_RESOURCE_ALLOCATION_INFO mi = dev->GetResourceAllocationInfo(0, 1, &rd);
                (rd.Flags & ~d->Flags) || rd.Dimension != d->Dimension || rd.SampleDesc.Count > 1 || mi.Alignment > kPage || mi.SizeInBytes > size) {
                static std::once_flag said;
                std::call_once(said, [&] {
                    logf("traversal: a layer changed a placed create's desc past the pool's checks (format %d to %d, flags 0x%x to 0x%x, %llu bytes "
                         "alignment %llu for a range of %llu): committed instead, and that desc from now on",
                         (int)d->Format, (int)rd.Format, (unsigned)d->Flags, (unsigned)rd.Flags, mi.SizeInBytes, mi.Alignment, size);
                });
                r->Release();
                // a once pool's range, never written, back to the fresh tail when nothing was taken after it; else freed as usual
                bool back = false;
                {
                    std::lock_guard l(g_pmx);
                    if (g_grown.size() < 65536) g_grown.insert(k);
                    if (once(pool) && h->fresh == at + size) back = true, h->fresh = at, h->used -= size, pool.free_bytes += size;
                }
                if (back) delete range;
                else range->Release();
                return no(W_GROWN);
            }
    }
    if (SUCCEEDED(e)) e = r->SetPrivateDataInterface(kPlaced, range);
    if (SUCCEEDED(e) && g_dred) {
        const uint64_t va = buffer ? r->GetGPUVirtualAddress() : 0;
        range->info = {pool.name, h->id, at, size, va, rd.Dimension, rd.Format, rd.Flags};  // what was made
        {
            std::lock_guard l(g_pmx);
            if (va && !h->va) h->va = va - at;
            g_live.insert(range);
        }
        logf("traversal: placed on device %p: %s heap %d offset 0x%llx size %llu, dimension %d format %d flags 0x%x state 0x%x alignment %llu, va 0x%llx",
             (void*)dev, pool.name, h->id, at, size, (int)rd.Dimension, (int)rd.Format, (unsigned)rd.Flags, (unsigned)st, (uint64_t)rd.Alignment, va);
    }
    if (FAILED(e)) {
        if (r) r->Release();
        range->Release();
        g_last_hr = e;
        return no(W_ERROR);
    }
    range->Release();  // the resource holds it now: its final Release frees the range
    e = r->QueryInterface(riid, pp);
    r->Release();
    if (FAILED(e)) return g_last_hr = e, no(W_ERROR);
    {
        std::lock_guard l(g_pmx);
        ++pool.placed;
    }
    g_ever = true, hr = e;
    g_placed.fetch_add(1, std::memory_order_relaxed), g_frame_placed.fetch_add(1, std::memory_order_relaxed);
    return true;
}

static HRESULT STDMETHODCALLTYPE hk_cr(ID3D12Device* dev, const D3D12_HEAP_PROPERTIES* hp, D3D12_HEAP_FLAGS hf, const D3D12_RESOURCE_DESC* d,
                                       D3D12_RESOURCE_STATES st, const D3D12_CLEAR_VALUE* cv, REFIID riid, void** pp) {
    return timed(CR, [&] {
        HRESULT hr;
        return place(dev, hp, hf, d, st, cv, riid, pp, hr) ? hr : o_cr(dev, hp, hf, d, st, cv, riid, pp);
    });
}
static HRESULT STDMETHODCALLTYPE hk_cr1(ID3D12Device4* dev, const D3D12_HEAP_PROPERTIES* hp, D3D12_HEAP_FLAGS hf, const D3D12_RESOURCE_DESC* d,
                                        D3D12_RESOURCE_STATES st, const D3D12_CLEAR_VALUE* cv, ID3D12ProtectedResourceSession* ps, REFIID riid, void** pp) {
    return timed(CR1, [&] {
        HRESULT hr;
        if (ps && g_on && pp) no(W_SESSION);
        return !ps && place(dev, hp, hf, d, st, cv, riid, pp, hr) ? hr : o_cr1(dev, hp, hf, d, st, cv, ps, riid, pp);
    });
}
static HRESULT STDMETHODCALLTYPE hk_cr2(ID3D12Device8* dev, const D3D12_HEAP_PROPERTIES* hp, D3D12_HEAP_FLAGS hf, const D3D12_RESOURCE_DESC1* d1,
                                        D3D12_RESOURCE_STATES st, const D3D12_CLEAR_VALUE* cv, ID3D12ProtectedResourceSession* ps, REFIID riid, void** pp) {
    return timed(CR2, [&] {
        HRESULT hr;
        // D3D12_RESOURCE_DESC1 is D3D12_RESOURCE_DESC and then the sampler feedback mip region, only feedback textures set
        const bool plain = d1 && !d1->SamplerFeedbackMipRegion.Width && !d1->SamplerFeedbackMipRegion.Height && !d1->SamplerFeedbackMipRegion.Depth;
        if (ps && g_on && pp) no(W_SESSION);
        return !ps && plain && place(dev, hp, hf, (const D3D12_RESOURCE_DESC*)d1, st, cv, riid, pp, hr) ? hr : o_cr2(dev, hp, hf, d1, st, cv, ps, riid, pp);
    });
}
static HRESULT STDMETHODCALLTYPE hk_cr3(ID3D12Device10* dev, const D3D12_HEAP_PROPERTIES* hp, D3D12_HEAP_FLAGS hf, const D3D12_RESOURCE_DESC1* d,
                                        D3D12_BARRIER_LAYOUT lay, const D3D12_CLEAR_VALUE* cv, ID3D12ProtectedResourceSession* ps, UINT32 nf,
                                        const DXGI_FORMAT* f, REFIID riid, void** pp) {
    return timed(CR3, [&] { return o_cr3(dev, hp, hf, d, lay, cv, ps, nf, f, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_placed(ID3D12Device* dev, ID3D12Heap* h, UINT64 at, const D3D12_RESOURCE_DESC* d, D3D12_RESOURCE_STATES st,
                                           const D3D12_CLEAR_VALUE* cv, REFIID riid, void** pp) {
    return timed(PLACED, [&] { return o_placed(dev, h, at, d, st, cv, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_placed1(ID3D12Device8* dev, ID3D12Heap* h, UINT64 at, const D3D12_RESOURCE_DESC1* d, D3D12_RESOURCE_STATES st,
                                            const D3D12_CLEAR_VALUE* cv, REFIID riid, void** pp) {
    return timed(PLACED1, [&] { return o_placed1(dev, h, at, d, st, cv, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_placed2(ID3D12Device10* dev, ID3D12Heap* h, UINT64 at, const D3D12_RESOURCE_DESC1* d, D3D12_BARRIER_LAYOUT lay,
                                            const D3D12_CLEAR_VALUE* cv, UINT32 nf, const DXGI_FORMAT* f, REFIID riid, void** pp) {
    return timed(PLACED2, [&] { return o_placed2(dev, h, at, d, lay, cv, nf, f, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_reserved(ID3D12Device* dev, const D3D12_RESOURCE_DESC* d, D3D12_RESOURCE_STATES st, const D3D12_CLEAR_VALUE* cv,
                                             REFIID riid, void** pp) {
    return timed(RESERVED, [&] { return o_reserved(dev, d, st, cv, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_reserved1(ID3D12Device4* dev, const D3D12_RESOURCE_DESC* d, D3D12_RESOURCE_STATES st, const D3D12_CLEAR_VALUE* cv,
                                              ID3D12ProtectedResourceSession* ps, REFIID riid, void** pp) {
    return timed(RESERVED1, [&] { return o_reserved1(dev, d, st, cv, ps, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_reserved2(ID3D12Device10* dev, const D3D12_RESOURCE_DESC* d, D3D12_BARRIER_LAYOUT lay, const D3D12_CLEAR_VALUE* cv,
                                              ID3D12ProtectedResourceSession* ps, UINT32 nf, const DXGI_FORMAT* f, REFIID riid, void** pp) {
    return timed(RESERVED2, [&] { return o_reserved2(dev, d, lay, cv, ps, nf, f, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_heap(ID3D12Device* dev, const D3D12_HEAP_DESC* d, REFIID riid, void** pp) {
    return timed(HEAP, [&] { return o_heap(dev, d, riid, pp); });
}
static HRESULT STDMETHODCALLTYPE hk_heap1(ID3D12Device4* dev, const D3D12_HEAP_DESC* d, ID3D12ProtectedResourceSession* ps, REFIID riid, void** pp) {
    return timed(HEAP1, [&] { return o_heap1(dev, d, ps, riid, pp); });
}

// A placed resource's residency is its heap's, which other resources share: the game's MakeResident / Evict of one
// leaves it out. heaps (EnqueueMakeResident): each one left out stands as its heap instead, once, so the call always
// reaches the runtime, which signals the game's fence in order with its other residency work (a CPU signal of ours could
// run ahead of an earlier enqueue's on the same fence, and be rewound by it). A heap of ours is resident already; the
// extra count on it is never evicted, as the game's Evicts of placed resources are left out.
template <class F> static HRESULT unplaced(UINT n, ID3D12Pageable* const* o, F&& call, bool heaps = false) {
    if (!g_ever.load(std::memory_order_relaxed) || !o) return call(n, o);
    std::vector<ID3D12Pageable*> keep;
    keep.reserve(n);
    UINT out = 0;
    for (UINT i = 0; i < n; ++i) {
        if (UINT size = 0; !o[i] || FAILED(o[i]->GetPrivateData(kPlaced, &size, nullptr)) || !size) {
            keep.push_back(o[i]);
            continue;
        }
        ++out;
        if (!heaps) continue;
        IUnknown* u = nullptr;
        UINT size = sizeof u;
        // the range's heap lives while the resource does (its range counts in the heap's use)
        ID3D12Pageable* h = SUCCEEDED(o[i]->GetPrivateData(kPlaced, &size, &u)) && u ? static_cast<Range*>(u)->h->h : o[i];
        if (u) u->Release();
        if (std::find(keep.begin(), keep.end(), h) == keep.end()) keep.push_back(h);
    }
    if (!out) return call(n, o);
    g_filtered.fetch_add(out, std::memory_order_relaxed);
    return call((UINT)keep.size(), keep.data());
}
static HRESULT STDMETHODCALLTYPE hk_resident(ID3D12Device* dev, UINT n, ID3D12Pageable* const* o) {
    return timed(MAKE_RESIDENT, [&] { return unplaced(n, o, [&](UINT k, ID3D12Pageable* const* p) { return k || !n ? o_resident(dev, k, p) : S_OK; }); });
}
static HRESULT STDMETHODCALLTYPE hk_evict(ID3D12Device* dev, UINT n, ID3D12Pageable* const* o) {
    return timed(EVICT, [&] { return unplaced(n, o, [&](UINT k, ID3D12Pageable* const* p) { return k || !n ? o_evict(dev, k, p) : S_OK; }); });
}
// 865: a placed resource's residency priority is its heap's, set by the heap, never by the resource (INVALID_CALL removes
// the device): placed resources left out, with their priorities; none left of a non-empty call, S_OK.
static HRESULT STDMETHODCALLTYPE hk_priority(ID3D12Device1* dev, UINT n, ID3D12Pageable* const* o, const D3D12_RESIDENCY_PRIORITY* pr) {
    if (!g_ever.load(std::memory_order_relaxed) || !o || !pr) return o_priority(dev, n, o, pr);
    std::vector<ID3D12Pageable*> keep;
    std::vector<D3D12_RESIDENCY_PRIORITY> kp;
    for (UINT i = 0; i < n; ++i)
        if (UINT size = 0; !o[i] || FAILED(o[i]->GetPrivateData(kPlaced, &size, nullptr)) || !size) keep.push_back(o[i]), kp.push_back(pr[i]);
    if (keep.size() == n) return o_priority(dev, n, o, pr);
    g_filtered.fetch_add(n - keep.size(), std::memory_order_relaxed);
    static std::atomic<bool> said;
    if (!said.exchange(true)) logf("traversal: SetResidencyPriority named placed resources: left out (865)");
    return keep.empty() ? S_OK : o_priority(dev, (UINT)keep.size(), keep.data(), kp.data());
}
static HRESULT STDMETHODCALLTYPE hk_enqueue(ID3D12Device3* dev, D3D12_RESIDENCY_FLAGS fl, UINT n, ID3D12Pageable* const* o, ID3D12Fence* f, UINT64 v) {
    return timed(ENQUEUE_RESIDENT, [&] {
        // never a CPU signal of the game's fence: always the runtime's call, placed resources as their heaps
        return unplaced(n, o, [&](UINT k, ID3D12Pageable* const* p) { return o_enqueue(dev, fl, k, p, f, v); }, true);
    });
}

// A traversal_ key's value from the lines admission verified inside sig= (g_signed, set before any device), def without
// its line: never scskiller.ini as it is now, which a hand edit could change after the check.
static int key(const wchar_t* k, int def) {
    const std::wstring& t = ::g_signed.traversal;
    const std::wstring want = std::wstring(k) + L"=";
    for (size_t at = 0; at < t.size();) {
        const size_t end = t.find(L"\r\n", at);
        if (!t.compare(at, want.size(), want)) return (int)wcstol(t.c_str() + at + want.size(), nullptr, 10);
        if (end == std::wstring::npos) break;
        at = end + 2;
    }
    return def;
}

// traversal_dred=1, before the first device: DRED's breadcrumbs and page faults, which a device takes at its creation.
static void dred_settings(void* get_debug_interface) {
    if (!get_debug_interface || key(L"traversal_suballocate", 0) != 1 || key(L"traversal_dred", 0) != 1) return;
    ID3D12DeviceRemovedExtendedDataSettings* s = nullptr;
    const HRESULT hr = ((decltype(&D3D12GetDebugInterface))get_debug_interface)(IID_PPV_ARGS(&s));
    if (SUCCEEDED(hr))
        s->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON), s->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON), s->Release();
    g_dred = SUCCEEDED(hr);
    logf("traversal: DRED %s (0x%08x), placements logged", g_dred ? "breadcrumbs and page faults on" : "unavailable", (unsigned)hr);
}

static std::string dred_name(const char* a, const wchar_t* w) { return a ? a : w ? utf8(w) : "(unnamed)"; }

// What DRED kept of the removal: the command lists it stopped in, with their last ops (D3D12_AUTO_BREADCRUMB_OP numbers),
// the page fault's address and the allocations there, and what of ours was at that address.
static void dred_dump(ID3D12Device* dev) {
    ID3D12DeviceRemovedExtendedData* d = nullptr;
    if (FAILED(dev->QueryInterface(IID_PPV_ARGS(&d)))) return logf("traversal: DRED: the device has no DRED interface");
    D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT bc = {};
    if (HRESULT hr = d->GetAutoBreadcrumbsOutput(&bc); FAILED(hr)) logf("traversal: DRED: no breadcrumbs (0x%08x)", (unsigned)hr);
    int lists = 0;
    for (auto* n = bc.pHeadAutoBreadcrumbNode; n && lists < 16; n = n->pNext) {
        const UINT done = n->pLastBreadcrumbValue ? *n->pLastBreadcrumbValue : 0;
        if (done >= n->BreadcrumbCount) continue;  // finished before the removal
        std::string ops;
        for (UINT i = done > 4 ? done - 4 : 0; i < n->BreadcrumbCount && i < done + 4; ++i)
            ops += std::format("{}{}{}", ops.empty() ? "" : " ", i == done ? ">" : "", (int)n->pCommandHistory[i]);
        logf("traversal: DRED: list %s on queue %s stopped after %u of %u ops; ops %u on (> the first not done): %s",
             dred_name(n->pCommandListDebugNameA, n->pCommandListDebugNameW).c_str(), dred_name(n->pCommandQueueDebugNameA, n->pCommandQueueDebugNameW).c_str(),
             done, n->BreadcrumbCount, done > 4 ? done - 4 : 0, ops.c_str());
        ++lists;
    }
    if (!lists) logf("traversal: DRED: no command list stopped part way");
    D3D12_DRED_PAGE_FAULT_OUTPUT pf = {};
    if (HRESULT hr = d->GetPageFaultAllocationOutput(&pf); FAILED(hr)) {
        d->Release();
        return logf("traversal: DRED: no page fault output (0x%08x)", (unsigned)hr);
    }
    d->Release();
    const uint64_t va = pf.PageFaultVA;
    logf("traversal: DRED: page fault at va 0x%llx%s", va, va ? "" : " (none)");
    int shown = 0;
    for (auto* a = pf.pHeadExistingAllocationNode; a && shown < 32; a = a->pNext, ++shown)
        logf("traversal: DRED: allocation there: %s, type %d", dred_name(a->ObjectNameA, a->ObjectNameW).c_str(), (int)a->AllocationType);
    shown = 0;
    for (auto* a = pf.pHeadRecentFreedAllocationNode; a && shown < 32; a = a->pNext, ++shown)
        logf("traversal: DRED: freed lately there: %s, type %d", dred_name(a->ObjectNameA, a->ObjectNameW).c_str(), (int)a->AllocationType);
    if (!va) return;
    // texture heaps have no address of their own here: only buffers' heaps (from a buffer placed in them) are known
    std::lock_guard l(g_pmx);
    bool ours = false;
    for (int i = 0; i < POOLS; ++i)
        for (auto& h : g_pool[i].heaps) {
            if (!h.va || va < h.va || va >= h.va + g_heap_bytes) continue;
            ours = true;
            const uint64_t at = va - h.va;
            logf("traversal: DRED: the fault is in our %s heap %d, offset 0x%llx", g_pool[i].name, h.id, at);
            for (Range* r : g_live)
                if (r->h == &h && at >= r->at && at < r->at + r->size)
                    logf("traversal: DRED: live there: placed offset 0x%llx size %llu, dimension %d format %d flags 0x%x", r->at, r->size, (int)r->info.dim,
                         (int)r->info.fmt, (unsigned)r->info.flags);
            for (auto& f : g_freed)
                if (f.heap == h.id && at >= f.at && at < f.at + f.size)
                    logf("traversal: DRED: freed there lately: offset 0x%llx size %llu, dimension %d format %d flags 0x%x", f.at, f.size, (int)f.dim, (int)f.fmt,
                         (unsigned)f.flags);
        }
    if (!ours) logf("traversal: DRED: the fault isn't in a buffer heap of ours (texture heaps can't be told)");
}

// A heap's Release on the pool thread, in ms; g_last_release: the QPC it started at (PresentMon's clock).
static int64_t g_last_release;
static double timed_release(ID3D12Heap* h) {
    LARGE_INTEGER a, b;
    QueryPerformanceCounter(&a), h->Release(), QueryPerformanceCounter(&b);
    return g_last_release = a.QuadPart, (b.QuadPart - a.QuadPart) * 1000.0 / g_freq;
}

// On the pool thread: a later game queue's device while nothing is placed, so every heap is empty: they're released and
// the pool moves there (true: a pool thread for it started).
static bool moved() {
    ID3D12Device *next, *old = nullptr;
    std::vector<ID3D12Heap*> heaps;
    IDXGIAdapter3* a = nullptr;
    {
        std::lock_guard g(g_grow_mx);  // no heap being made meanwhile
        std::lock_guard l(g_pmx);
        if ((next = g_next_dev.exchange(nullptr)) && !g_bound) {
            for (int i = 0; i < POOLS; ++i) {
                for (auto& h : g_pool[i].heaps) heaps.push_back(unalias(h));
                g_pool[i].heaps.clear(), g_pool[i].free_bytes = 0;
            }
            g_pool_bytes = 0, old = g_dev.exchange(next), a = g_adapter.exchange(nullptr);  // no grow until the new one's set
        }
    }
    if (!old) {
        if (next) next->Release();
        return false;
    }
    for (auto* h : heaps) h->Release();
    if (a) a->Release();
    old->Release();
    logf("traversal: pool moved from device %p to %p, the game's latest queue's (nothing placed yet), %zu empty heaps released", (void*)old, (void*)next,
         heaps.size());
    return pool_thread(), true;
}

// traversal_zeroed_reuse, on the pool thread: our COPY queue, and a zero buffer copied into the zeroed buffer pool's freed
// ranges. Buffers promote from COMMON to COPY_SOURCE / COPY_DEST on a COPY queue and decay back at the end of each
// ExecuteCommandLists, so no barrier: no aliasing one either, which would deactivate the game's live buffers in the
// other ranges of the heap; buffers inherit what was written through another buffer on their memory. One batch in flight.
static const uint64_t kZeroSrc = 4ull << 20;
struct Zeroer {
    ID3D12CommandQueue* q = nullptr;
    ID3D12CommandAllocator* ca = nullptr;
    ID3D12GraphicsCommandList* cl = nullptr;
    ID3D12Fence* f = nullptr;
    ID3D12Resource* src = nullptr;
    uint64_t value = 0;
    std::vector<Pending> flight;  // copies sent, given back once the fence is at value
    bool failed = false;          // ranges to zero from then on stay in use: never handed out unzeroed
    ~Zeroer() {
        for (IUnknown* u : {(IUnknown*)src, (IUnknown*)f, (IUnknown*)cl, (IUnknown*)ca, (IUnknown*)q})
            if (u) u->Release();
    }
    HRESULT make(ID3D12Device* dev) {
        D3D12_COMMAND_QUEUE_DESC qd = {D3D12_COMMAND_LIST_TYPE_COPY};
        t_own_queue = true;  // not the game's: no adoption, no HIGH priority
        HRESULT hr = dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q));
        t_own_queue = false;
        if (SUCCEEDED(hr)) hr = dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COPY, IID_PPV_ARGS(&ca));
        if (SUCCEEDED(hr)) hr = dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COPY, ca, nullptr, IID_PPV_ARGS(&cl));
        if (SUCCEEDED(hr)) hr = cl->Close();
        if (SUCCEEDED(hr)) hr = dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f));
        const D3D12_HEAP_PROPERTIES hp = {D3D12_HEAP_TYPE_DEFAULT};
        const D3D12_RESOURCE_DESC bd = {D3D12_RESOURCE_DIMENSION_BUFFER, 0, kZeroSrc, 1, 1, 1, DXGI_FORMAT_UNKNOWN, {1, 0}, D3D12_TEXTURE_LAYOUT_ROW_MAJOR};
        // committed without CREATE_NOT_ZEROED: zero, and never written; o_cr, not the hook, which would place it in our own pool
        if (SUCCEEDED(hr)) hr = o_cr(dev, &hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&src));
        return hr;
    }
};
// On the pool thread: a failed signal of ours signalled again, under the queue's lock, once its backoff is past. The
// queue let go of on a removed device (its fence reads UINT64_MAX), and every one at the thread's end (drop): none keeps
// a removed device alive.
static void retry_signals(bool drop = false) {
    if (!g_retries.load(std::memory_order_relaxed)) return;
    std::vector<std::shared_ptr<Track>> ts;
    {
        std::lock_guard l(g_tmx);
        for (auto& [q, t] : g_tracks) ts.push_back(t);
    }
    const uint64_t now = GetTickCount64();
    for (auto& t : ts) {
        ID3D12CommandQueue* done = nullptr;
        {
            std::lock_guard g(t->mx);
            if (!t->retry || (!drop && now < t->retry_at)) continue;
            if (drop) done = t->retry;
            else if (const HRESULT hr = t->retry->Signal(t->f, t->sent.load()); SUCCEEDED(hr) || hr == DXGI_ERROR_DEVICE_REMOVED) done = t->retry;
            if (done) t->retry = nullptr, g_retries.fetch_sub(1);
            else t->backoff = std::min<uint64_t>(t->backoff * 2, 1000), t->retry_at = now + t->backoff;
        }
        if (done) done->Release();  // may be the queue's last: its end takes g_tmx, not held here
    }
}

// On the pool thread: settled ranges of the re-zeroed pools zeroed, outside g_pmx, and given back to their heaps once zero.
// Until then they count in their heap's use, so a heap with one is never released.
static void zero_freed(Zeroer& z) {
    std::vector<Pending> cpu, gpu, done;
    const bool past = z.flight.empty() || z.f->GetCompletedValue() >= z.value;  // after the device removal check
    {
        std::lock_guard l(g_pmx);
        reap();
        if (past) done.swap(z.flight);
        std::vector<Pending> all;
        all.swap(g_dirty);
        for (auto& r : all) (r.h->cpu ? cpu : z.flight.empty() ? gpu : g_dirty).push_back(r);
        for (auto& r : done) give(*r.p, *r.h, r.at, r.size), g_rezeroed_gpu.fetch_add(r.size, std::memory_order_relaxed);
    }
    for (auto& r : cpu) memset(r.h->cpu + r.at, 0, r.size), g_rezeroed_cpu.fetch_add(r.size, std::memory_order_relaxed);
    if (!cpu.empty()) _mm_sfence();  // UPLOAD memory is write-combined: the zeros out before a range is handed out
    if (!gpu.empty() && !z.failed) {
        HRESULT hr = z.ca->Reset();  // the GPU is past the last batch
        if (SUCCEEDED(hr)) hr = z.cl->Reset(z.ca, nullptr);
        if (SUCCEEDED(hr)) {
            for (auto& r : gpu)
                for (uint64_t o = 0; o < r.size; o += kZeroSrc) z.cl->CopyBufferRegion(r.h->alias, r.at + o, z.src, 0, std::min(kZeroSrc, r.size - o));
            hr = z.cl->Close();
        }
        if (SUCCEEDED(hr)) {
            ID3D12CommandList* l = z.cl;
            z.q->ExecuteCommandLists(1, &l);
            hr = z.q->Signal(z.f, ++z.value);
        }
        if (FAILED(hr)) z.failed = true, logf("traversal: zeroing freed ranges failed (0x%08x): those of the zeroed buffers pool stay in use from now on", (unsigned)hr);
        else z.flight.swap(gpu);
    }
    g_fenced.store(z.flight.size(), std::memory_order_relaxed);
    if (cpu.empty()) return;
    std::lock_guard l(g_pmx);
    for (auto& r : cpu) give(*r.p, *r.h, r.at, r.size);
}

static void pool_main() {
    ID3D12Device* dev = g_dev;
    struct Drop {
        ~Drop() { retry_signals(true); }
    } drop_retries;  // at every return: a removal, a move
    IDXGIAdapter3* a = nullptr;
    if (IDXGIFactory4* f; SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&f)))) f->EnumAdapterByLuid(dev->GetAdapterLuid(), IID_PPV_ARGS(&a)), f->Release();
    D3D12_FEATURE_DATA_D3D12_OPTIONS o = {};
    dev->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &o, sizeof o);
    D3D12_FEATURE_DATA_ARCHITECTURE arch = {};
    dev->CheckFeatureSupport(D3D12_FEATURE_ARCHITECTURE, &arch, sizeof arch);
    // one memory: its budget is the local one
    g_pool[P_UPLOAD].seg = g_pool[P_READBACK].seg = g_pool[P_ZUP].seg = arch.UMA ? DXGI_MEMORY_SEGMENT_GROUP_LOCAL : DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL;
    logf("traversal: pool on device %p, resource heap tier %d%s, %s", (void*)dev, (int)o.ResourceHeapTier, arch.UMA ? ", UMA" : "",
         a ? "budget read" : "no budget (no DXGI adapter): no heaps");
    if (!a) return;
    Zeroer z;  // before the first heap: g_zreuse_buf may turn off
    if (g_zreuse_buf)
        if (HRESULT hr = z.make(dev); FAILED(hr))
            g_zreuse_buf = false, logf("traversal: no COPY queue to zero freed ranges with (0x%08x): zeroed DEFAULT buffer ranges used once", (unsigned)hr);
    g_adapter = a;  // after the segments are set: a create's grow reads them
    const std::wstring ini = g_dir + L"scskiller.ini";
    bool on = g_on;
    uint64_t read_at = 0, pools_at = 0;
    for (bool told = false, by_time = false, zwake = false;;) {
        if (g_next_dev.load() && moved()) return;
        std::vector<std::pair<ID3D12Heap*, ID3D12Resource*>> released;
        {
            std::lock_guard l(g_pmx);
            released.swap(g_released);
        }
        for (auto [h, alias] : released) {
            if (alias) alias->Release();
            const double ms = timed_release(h);
            logf("traversal: a heap released in %.1f ms (pool at max_pool_mb), qpc %lld", ms, g_last_release);
        }
        if (!by_time && g_by_time)
            by_time = true, logf("traversal: no frame seen for 500 ms (or none yet): freed ranges reused after %d ms meanwhile", g_reuse_ms);
        if (HRESULT r = dev->GetDeviceRemovedReason(); FAILED(r)) {
            logf("traversal: device removed (0x%08x), no more heaps", (unsigned)r);
            if (g_dred) dred_dump(dev);
            return;
        }
        // an ini being replaced (missing for a moment) keeps the state
        // once a second, not at every wake: a create while its pool is under the spare wakes this thread each time
        // only off: this read isn't verified, so on again, or any other key, takes a new session (admission's check)
        if (GetTickCount64() >= read_at + 1000)
            read_at = GetTickCount64(),
            on = on && (GetFileAttributesW(ini.c_str()) == INVALID_FILE_ATTRIBUTES || GetPrivateProfileIntW(L"scskiller", L"traversal_suballocate", 0, ini.c_str()) == 1);
        if (on != g_on) g_on = on, logf("traversal: suballocate off (scskiller.ini), empty heaps released; on again takes a new session");
        if (!told && g_filtered) told = true, logf("traversal: residency calls named placed resources: left out, their heaps stay resident");
        // a wake only for zeroed ranges (traversal_zeroed_reuse): the budget and the pools at most every 100 ms
        const bool skip = zwake && GetTickCount64() < pools_at + 100;
        if (!skip) pools_at = GetTickCount64();
        DXGI_QUERY_VIDEO_MEMORY_INFO m[2] = {};
        bool known[2] = {};
        for (int s = 0; s < 2 && !skip; ++s) known[s] = budget(a, (DXGI_MEMORY_SEGMENT_GROUP)s, m[s]);
        bool made = false, dropped = false;
        for (int i = 0; i < POOLS && !made && !skip; ++i) {
            Pool& p = g_pool[i];
            const DXGI_QUERY_VIDEO_MEMORY_INFO& mi = m[p.seg];
            const bool high = known[p.seg] && mi.CurrentUsage > mi.Budget / 100 * 95;
            bool want;
            std::vector<std::pair<ID3D12Heap*, bool>> drops;  // and spent
            const char* why = !on ? "suballocate off" : high ? "usage over 95% of the budget" : "more than the spare free";
            {
                std::lock_guard l(g_pmx);
                reap();
                want = on && p.on && known[p.seg] && mi.CurrentUsage + g_heap_bytes <= mi.Budget / 100 * 90 && p.free_bytes < spare_bytes(p) &&
                       g_pool_bytes + g_heap_bytes <= g_max_pool;
                // off: every empty heap; over 95%: one a second; else one once a whole heap more than the spare is free (no
                // make / release churn at the line); an empty zeroed heap more than half used once, always
                for (auto it = p.heaps.begin(); it != p.heaps.end();) {
                    const bool spent = once(p) && it->fresh > g_heap_bytes / 2;
                    if (it->used || !settled(it->last_free, it->last_tick) ||
                        !(spent || (!want && (!on || (high && !dropped) || p.free_bytes >= spare_bytes(p) + 2 * g_heap_bytes)))) {
                        ++it;
                        continue;
                    }
                    dropped |= on && high && !spent;
                    drops.push_back({unalias(*it), spent}), p.free_bytes -= avail(p, *it), g_pool_bytes -= g_heap_bytes, it = p.heaps.erase(it);
                    if (on && !spent) break;
                }
            }
            for (auto [h, spent] : drops) {
                const double ms = timed_release(h);
                logf("traversal: an empty %s heap released in %.1f ms (%s), qpc %lld", p.name, ms, spent ? "its zeroed memory used once" : why, g_last_release);
            }
            if (!want) continue;
            std::lock_guard g(g_grow_mx);
            made = grow(p, false) == G_MADE;
        }
        retry_signals();
        if (g_zreuse_buf || g_zreuse_up) zero_freed(z);
        if (made) continue;
        std::unique_lock l(g_pmx);
        // zeroed ranges settling, to zero or in flight, or a signal to retry: every 5 ms (frames don't wake this thread); else
        // to the next ini read
        const bool busy = !z.flight.empty() || !g_dirty.empty() || g_zpending || g_retries.load(std::memory_order_relaxed);
        zwake = g_pcv.wait_for(l, std::chrono::milliseconds(busy ? 5 : std::clamp((int64_t)(read_at + 1000 - GetTickCount64()), (int64_t)1, (int64_t)1000))) ==
                    std::cv_status::timeout && busy;
    }
}

static std::string thread_name(DWORD tid) {
    static std::map<DWORD, std::string> names;
    if (auto it = names.find(tid); it != names.end()) return it->second;
    std::string s = std::to_string(tid);
    using PFN_Desc = HRESULT(WINAPI*)(HANDLE, PWSTR*);  // Windows 10 1607 and later
    static auto desc = (PFN_Desc)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetThreadDescription");
    if (HANDLE h = desc ? OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, tid) : nullptr) {
        PWSTR w = nullptr;
        if (SUCCEEDED(desc(h, &w)) && w && *w) s = utf8(w) + " " + s;
        if (w) LocalFree(w);
        CloseHandle(h);
    }
    return names[tid] = s;
}

static double ms(uint64_t ticks) { return ticks * 1000.0 / g_freq; }

// Once a second (frame_writer): the queued frames, at most 10 a second, and every 30 s the totals when they changed.
static void tick() {
    if (!g_active.load(std::memory_order_relaxed)) return;
    std::vector<FrameRec> q;
    uint64_t over;
    {
        std::lock_guard l(g_qmx);
        q.swap(g_q), over = g_q_over, g_q_over = 0;
    }
    for (size_t i = 0; i < q.size() && i < 10; ++i) {
        uint64_t n = 0, t = 0, rn = 0, rt = 0;
        std::string who;
        for (auto& f : q[i].threads) {
            n += f.n, t += f.ticks, rn += f.rn, rt += f.rticks;
            if (who.size() < 400) who += std::format("; {} {:.1f} ms in {} creates, {:.1f} ms in {} residency calls", thread_name(f.tid), ms(f.ticks), f.n, ms(f.rticks), f.rn);
        }
        std::string why;
        for (auto [w, k] : q[i].why) why += std::format("{}{} {}", why.empty() ? ", committed: " : ", ", kWhy[w], k);
        logf("traversal: frame at %.1f s: %.1f ms in %llu creates, %.1f ms in %llu residency calls, %llu placed by the pool%s%s", q[i].t_ms / 1000,
             ms(t), n, ms(rt), rn, q[i].placed, why.c_str(), who.c_str());
    }
    if (q.size() > 10 || over) logf("traversal: %llu more frames over %d ms in the last second", q.size() - std::min<size_t>(q.size(), 10) + over, g_probe_ms);
    static int ticks;
    static uint64_t last;
    if (++ticks % 30) return;
    uint64_t sum = g_placed + g_filtered;
    for (auto& k : g_kind_n) sum += k;
    for (auto& w : g_why) sum += w;
    if (sum == last) return;
    last = sum;
    std::string calls, why;
    for (int k = 0; k < KINDS; ++k)
        if (uint64_t n = g_kind_n[k]) calls += std::format("{}{} {} in {:.1f} ms", calls.empty() ? "" : ", ", kKind[k], n, ms(g_kind_ticks[k]));
    logf("traversal: since start: %s", calls.empty() ? "no calls" : calls.c_str());
    if (!g_heap_bytes) return;  // suballocate was off at the start
    for (int w = 0; w < WHYS; ++w)
        if (uint64_t n = g_why[w]) why += std::format("{}{} {}", why.empty() ? "" : ", ", kWhy[w], n);
    std::string pools;
    size_t waiting = 0;
    uint64_t waiting_bytes = 0;
    {
        std::lock_guard l(g_pmx);
        waiting = g_pending.size();
        for (auto& f : g_pending) waiting_bytes += f.size;
        for (int i = 0; i < POOLS; ++i) {
            if (!g_pool[i].on) continue;
            uint64_t used = 0;
            for (auto& h : g_pool[i].heaps) used += h.used;
            pools += std::format("{}{} {} placed, {} heaps, {} MB in use", pools.empty() ? "" : "; ", g_pool[i].name, g_pool[i].placed, g_pool[i].heaps.size(), used >> 20);
        }
        pools += std::format("; {} MB of heaps, at most {} MB so far, max_pool_mb {}", g_pool_bytes >> 20, g_pool_peak >> 20, g_max_pool >> 20);
    }
    const std::string rz = g_zreuse_buf || g_zreuse_up ? std::format(", {} MB zeroed again on the GPU and {} MB by the CPU, {} ranges waiting for our fence",
                                                                     g_rezeroed_gpu.load() >> 20, g_rezeroed_cpu.load() >> 20, g_fenced.load())
                                                       : "";
    size_t queues;
    {
        std::lock_guard l(g_tmx);
        queues = g_tracks.size();
    }
    logf("traversal: pool: %llu placed (%s), %llu heaps made by a create that found no room, %zu ranges (%llu MB) waiting for reuse (and %zu game queues' fences)%s, %llu residency "
         "objects left out; committed instead: %s%s",
         (uint64_t)g_placed, pools.c_str(), (uint64_t)g_sync_grows, waiting, waiting_bytes >> 20, queues, rz.c_str(), (uint64_t)g_filtered, why.empty() ? "none" : why.c_str(),
         g_last_hr ? std::format(", last failure 0x{:08x}", (unsigned)g_last_hr.load()).c_str() : "");
}

// At each hooked device (under g_mx), not in a warm.
static void install(ID3D12Device* dev) {
    static std::once_flag once;
    std::call_once(once, [] {
        if (key(L"traversal_suballocate", 0) != 1) return;  // off: nothing installed, nothing logged
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        g_freq = f.QuadPart;
        g_probe_ms = std::max(1, key(L"traversal_probe_ms", 5));
        g_probe_ticks = g_probe_ms * g_freq / 1000;
        if ((g_copy_high = key(L"traversal_copy_high", 0) == 1)) logf("traversal: COPY queues at HIGH priority (traversal_copy_high)");
        g_heap_bytes = (uint64_t)std::clamp(key(L"traversal_heap_mb", 128), 16, 1024) << 20;
        g_spare = std::clamp(key(L"traversal_spare_heaps", 2), 1, 8);
        g_max_pool = (uint64_t)std::max(key(L"traversal_max_pool_mb", 2048), 0) << 20;
        g_max_res = std::min((uint64_t)std::clamp(key(L"traversal_max_resource_mb", 16), 1, 1024) << 20, g_heap_bytes);
        g_uav_tex = key(L"traversal_uav_textures", 0) == 1, g_uav_buf = key(L"traversal_uav_buffers", 0) == 1;
        g_reuse_frames = std::clamp(key(L"traversal_reuse_frames", 3), 0, 60), g_reuse_ms = std::clamp(key(L"traversal_reuse_ms", 50), 0, 1000);
        g_pool[P_BUFFERS].on = key(L"traversal_buffers", 1) == 1, g_pool[P_TEXTURES].on = key(L"traversal_textures", 1) == 1;
        g_pool[P_UPLOAD].on = key(L"traversal_upload", 1) == 1, g_pool[P_READBACK].on = key(L"traversal_readback", 1) == 1;
        g_zeroed.on = key(L"traversal_zeroed", 0) == 1, g_pool[P_ZTEX].on = key(L"traversal_zeroed_textures", 0) == 1;
        g_pool[P_ZUP].on = key(L"traversal_zeroed_upload", g_zeroed.on) == 1;
        const bool zreuse = key(L"traversal_zeroed_reuse", g_zeroed.on || g_pool[P_ZUP].on) == 1;  // a zeroed buffer pool on: reuse unless set to 0
        g_zreuse_buf = zreuse && g_zeroed.on, g_zreuse_up = zreuse && g_pool[P_ZUP].on;
        g_test_budget = (uint64_t)std::max(key(L"traversal_test_budget_mb", 0), 0) << 20;
        g_on = true;
        auto onoff = [](int i) { return g_pool[i].on ? "on" : "off"; };
        logf("traversal: probe on (%d ms), suballocate on: %llu MB heaps, %d spare per DEFAULT pool; DEFAULT buffers %s, textures %s%s, UPLOAD %s, "
             "READBACK %s, zeroed DEFAULT buffers %s, zeroed textures %s, zeroed UPLOAD buffers %s, zeroed buffer ranges reused %s; UAV buffers %s; ranges reused after %d frames (%d ms without frames); pool up to %llu MB and 90%% "
             "of the budget%s, resources up to %llu MB",
             g_probe_ms, g_heap_bytes >> 20, g_spare, onoff(P_BUFFERS), onoff(P_TEXTURES), g_uav_tex ? " (UAV too)" : "", onoff(P_UPLOAD),
             onoff(P_READBACK), onoff(P_ZEROED), onoff(P_ZTEX), onoff(P_ZUP), g_zreuse_buf || g_zreuse_up ? "on (zeroed again)" : "off", g_uav_buf ? "on" : "off", g_reuse_frames, g_reuse_ms, g_max_pool >> 20,
             g_test_budget ? std::format(" (the local budget taken as {} MB: traversal_test_budget_mb)", g_test_budget >> 20).c_str() : "", g_max_res >> 20);
        g_active = true;
    });
    if (!g_active.load()) return;
    void** vt = *(void***)dev;
    patch(vt, SLOT_CR, (void*)hk_cr, o_cr);
    patch(vt, SLOT_HEAP, (void*)hk_heap, o_heap);
    patch(vt, SLOT_PLACED, (void*)hk_placed, o_placed);
    patch(vt, SLOT_RESERVED, (void*)hk_reserved, o_reserved);
    patch(vt, SLOT_MAKERESIDENT, (void*)hk_resident, o_resident);
    patch(vt, SLOT_EVICT, (void*)hk_evict, o_evict);
    patch(vt, SLOT_QUEUE, (void*)hk_queue, o_queue);
    if (ID3D12Device9* d9; SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d9)))) patch(*(void***)d9, SLOT_QUEUE1, (void*)hk_queue1, o_queue1), d9->Release();
    if (ID3D12Device3* d3; SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d3)))) patch(*(void***)d3, SLOT_ENQUEUE, (void*)hk_enqueue, o_enqueue), d3->Release();
    if (ID3D12Device1* d1; SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d1)))) patch(*(void***)d1, SLOT_PRIORITY, (void*)hk_priority, o_priority), d1->Release();
    if (ID3D12Device4* d4; SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d4)))) {
        void** v4 = *(void***)d4;
        patch(v4, SLOT_CR1, (void*)hk_cr1, o_cr1), patch(v4, SLOT_HEAP1, (void*)hk_heap1, o_heap1), patch(v4, SLOT_RESERVED1, (void*)hk_reserved1, o_reserved1);
        d4->Release();
    }
    if (ID3D12Device8* d8; SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d8))))
        patch(*(void***)d8, SLOT_CR2, (void*)hk_cr2, o_cr2), patch(*(void***)d8, SLOT_PLACED1, (void*)hk_placed1, o_placed1), d8->Release();
    if (ID3D12Device10* d10; SUCCEEDED(dev->QueryInterface(IID_PPV_ARGS(&d10)))) {
        void** v10 = *(void***)d10;
        patch(v10, SLOT_CR3, (void*)hk_cr3, o_cr3), patch(v10, SLOT_PLACED2, (void*)hk_placed2, o_placed2), patch(v10, SLOT_RESERVED2, (void*)hk_reserved2, o_reserved2);
        d10->Release();
    }
}

}  // namespace trav

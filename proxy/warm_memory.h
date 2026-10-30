// The warm's workers under its memory budget (SCSKiller_WarmMemory), one supervisor tick: shared with `selftest warmmemory`.
#pragma once
#include <algorithm>

struct WarmMemory {
    double budget;  // MB
    int threads, allowed;
    double changed = 0;  // ms of the last change
    double kept = 0;     // MB over the budget that fewer workers didn't free
    double at = 0;       // private memory at the last drop
    double over = -1;    // ms since memory has been over the limit, -1 = under it
    char last = 0;
};

// 'd' one worker fewer, 'u' one more, 'k' memory counted as kept (and the drop before it undone), 0 nothing. mb: the
// process's private memory, held: MB of kept state objects a release measured, added to the budget. Over the limit a worker
// goes every 5 s, well under it one comes back every 30 s. Memory a drop doesn't lower within 5 s, or one worker's over the
// limit for 30 s, is the driver's (Release frees ~0 MB of a state object on NVIDIA), not the workers': the limit moves to
// 0.3x the budget above it, so the 0.2x climb gap is open again. A PC low on memory only keeps workers from coming back:
// fewer workers don't free memory this process doesn't hold.
inline char warm_memory_tick(WarmMemory& m, double mb, double held, bool low, double now) {
    double limit = m.budget + std::max(held, m.kept);
    if (mb <= limit) m.over = -1;
    else if (m.over < 0) m.over = now;
    bool useless = m.last == 'd' && m.over >= 0 && m.over <= m.changed && now - m.changed > 5000 && mb >= m.at;  // over since the drop
    if (mb > limit && (useless || (m.allowed == 1 && m.over >= 0 && now - std::max(m.over, m.changed) > 30000))) {
        m.kept = mb - 0.7 * m.budget, m.over = -1, m.last = 'k', m.changed = now;
        if (useless && !low) ++m.allowed;
        return 'k';
    }
    if (mb > limit && m.allowed > 1 && now - m.changed > 5000) return --m.allowed, m.at = mb, m.last = 'd', m.changed = now, 'd';
    if (mb < limit - 0.2 * m.budget && !low && m.allowed < m.threads && now - m.changed > 30000) return ++m.allowed, m.last = 'u', m.changed = now, 'u';
    return 0;
}

// A fresh process frees what the driver keeps per pipeline (NVIDIA: all of it until exit): the warm ends and a new process
// goes on from its first unfinished item once this process grew by a third of the PC's RAM since its start, or once the PC is
// low on memory while it grew by a third of its budget and is still above where it was 5-10 min before (a process that
// stopped growing never restarts). Measured: Counter-Strike 2's D3D11 warm on AMD, 48 GB shared with the GPU, grows 0.55 GB
// per 1,000 items to 27.6 GB private / 35.7 GB working set with 6 GB free at ~48k of 140,906 items; Marvel Rivals on 32 GB,
// 7.7 -> 29 GB private and 0.4-4 GB free over 9 h.
struct WarmRestart {
    double mark_mb = -1, mark_t = 0, was_mb = -1;
};

inline bool warm_restart_tick(WarmRestart& r, double mb, double start_mb, double budget, double ram_mb, bool low, double now) {
    if (r.mark_mb < 0 || now - r.mark_t >= 300000) r.was_mb = now - r.mark_t < 600000 ? r.mark_mb : -1, r.mark_mb = mb, r.mark_t = now;  // older: a pause
    double grown = mb - start_mb;
    return (ram_mb > 0 && grown >= ram_mb / 3) || (low && grown >= budget / 3 && r.was_mb >= 0 && mb > r.was_mb);
}

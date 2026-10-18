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

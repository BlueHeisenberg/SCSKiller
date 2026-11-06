#pragma once
// scskiller_gen.mem: "<section name or \\?\ path> <bytes> <blobs>\n". The plan's shader blobs in the app's shared memory (Windows)
// or a tmpfs file (Linux, under Wine), 'B' records as in scskiller_gen.db (GenRam.cs). The parent checks them before a child
// starts, the child as it loads them.
#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

struct GenMem {
    char name[128] = "";
    unsigned long long used = 0, blobs = 0;
};

static bool gen_mem_read(const std::wstring& path, GenMem& m) {
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) return false;
    char end = 0;
    const bool whole = fscanf_s(f, "%127s %llu %llu%c", m.name, (unsigned)sizeof m.name, &m.used, &m.blobs, &end, 1u) == 4 && end == '\n' && m.used && m.blobs;
    fclose(f);
    return whole;
}

static const char* gen_mem_map(const GenMem& m) {
    HANDLE s;
    if (!strncmp(m.name, "\\\\?\\", 4)) {
        HANDLE f = CreateFileA(m.name, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (f == INVALID_HANDLE_VALUE) return nullptr;
        s = CreateFileMappingA(f, nullptr, PAGE_READONLY, 0, 0, nullptr);
        CloseHandle(f);  // the mapping keeps the file
    } else s = OpenFileMappingA(FILE_MAP_READ, FALSE, m.name);
    if (!s) return nullptr;
    auto base = (const char*)MapViewOfFile(s, FILE_MAP_READ, 0, 0, (SIZE_T)m.used);
    CloseHandle(s);  // the view keeps it
    return base;
}

// each(sha1, bytes, size) per blob. False unless the records fill the bytes exactly and number the blobs.
template <class Each>
static bool gen_mem_walk(const char* base, const GenMem& m, Each each) {
    unsigned long long at = 0, n = 0;
    while (at < m.used) {
        uint32_t len;
        if (m.used - at < 25) return false;
        memcpy(&len, base + at + 1, 4);
        if (base[at] != 'B' || len < 20 || m.used - at - 5 < len) return false;
        each(base + at + 5, base + at + 25, len - 20);
        at += 5ull + len, ++n;
    }
    return n == m.blobs;
}

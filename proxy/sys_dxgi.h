// Shared by proxy.cpp and selftest.cpp: System32's dxgi.dll and the module an address lives in. A game folder's
// dxgi.dll (OptiScaler, ReShade) has the same name, so GetModuleHandleW(L"dxgi.dll") may be it.
#pragma once
#include <windows.h>
#include <string>

// null when the loader hands back another file for the full path (DLL redirection: a .local folder beside the exe)
inline HMODULE system_dxgi() {
    wchar_t p[MAX_PATH], got[MAX_PATH];
    UINT n = GetSystemDirectoryW(p, MAX_PATH);
    if (!n || n >= MAX_PATH) return nullptr;
    const std::wstring path = std::wstring(p, n) + L"\\dxgi.dll";
    HMODULE m = LoadLibraryW(path.c_str());
    return m && GetModuleFileNameW(m, got, MAX_PATH) && !_wcsicmp(got, path.c_str()) ? m : nullptr;
}

// null: not in a loaded module (a heap copy of a vtable)
inline HMODULE module_of(const void* p) {
    HMODULE m = nullptr;
    if (p) GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)p, &m);
    return m;
}

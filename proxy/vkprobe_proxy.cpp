// `selftest vkproxy`'s vulkan-1.dll (built as vkprobe_proxy.dll, copied beside a test exe as vulkan-1.dll): every export
// jumps to System32's loader. With vkprobe.ini beside it, it first adds a layer to its own process through the loader's
// variables, as a recorder would for a game it doesn't launch. Test only, never published.
#define NOMINMAX
#include <windows.h>
#include <string>
#include "vk_names.h"

extern "C" void* vk_real[];  // vk_stubs.asm jumps through it, in vk_exports.txt's order
static HMODULE g_real;

// A value that fills the buffer may be cut: none then.
static std::wstring ini_value(const std::wstring& ini, const wchar_t* key) {
    std::wstring v(32768, L'\0');
    DWORD n = GetPrivateProfileStringW(L"vk", key, L"", v.data(), (DWORD)v.size(), ini.c_str());
    v.resize(n < v.size() - 1 ? n : 0);
    return v;
}

// A list variable keeps what the process already had: the layer goes in front of it.
static void prepend(const wchar_t* name, const std::wstring& value) {
    std::wstring old(GetEnvironmentVariableW(name, nullptr, 0), L'\0');
    old.resize(old.empty() ? 0 : GetEnvironmentVariableW(name, old.data(), (DWORD)old.size()));
    SetEnvironmentVariableW(name, (old.empty() ? value : value + L";" + old).c_str());
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_DETACH) {
        if (!reserved && g_real) FreeLibrary(g_real);  // unloaded, not exiting: the real loader goes with it
        return TRUE;
    }
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    std::wstring ini(32768, L'\0');
    DWORD n = GetModuleFileNameW(self, ini.data(), (DWORD)ini.size());
    ini.resize(n && n < ini.size() ? n : 0);  // a cut path names another folder: no ini then
    if (!ini.empty()) ini = ini.substr(0, ini.find_last_of(L'\\') + 1) + L"vkprobe.ini";
    // marker=<file>: this process loaded the copy. One line: the exe, then only the graphics and Steam cache variables as
    // the process started with them (the whole environment can hold tokens).
    if (std::wstring marker = ini.empty() ? L"" : ini_value(ini, L"marker"); !marker.empty()) {
        std::wstring line(32768, L'\0');
        line.resize(GetModuleFileNameW(nullptr, line.data(), (DWORD)line.size()));
        if (wchar_t* env = GetEnvironmentStringsW()) {
            for (const wchar_t* e = env; *e; e += wcslen(e) + 1) {
                std::wstring v = e, name = v.substr(0, v.find(L'=', 1));  // a variable's name only: values are never matched
                bool keep = name.find(L"FOSSILIZE") != std::wstring::npos || !_wcsicmp(name.c_str(), L"SteamAppId") || !_wcsicmp(name.c_str(), L"SteamGameId");
                for (const wchar_t* p : {L"VK_", L"ENABLE_VK_", L"DISABLE_VK_", L"__GL_", L"AMD_VK_"}) keep |= !_wcsnicmp(name.c_str(), p, wcslen(p));
                if (keep) line += L"\t" + v;
            }
            FreeEnvironmentStringsW(env);
        }
        line += L"\r\n";
        // <marker>.<pid>, always new: no other handle (or oplock) on it can hold up this DllMain
        HANDLE f = CreateFileW((marker + L"." + std::to_wstring(GetCurrentProcessId())).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, 0, nullptr);
        if (f != INVALID_HANDLE_VALUE) {
            DWORD w;
            WriteFile(f, line.data(), (DWORD)(line.size() * sizeof(wchar_t)), &w, nullptr);
            CloseHandle(f);
        }
    }
    // before the loader is loaded: it reads these when the app enumerates or creates an instance. VK_LAYER_PATH, when the
    // process has one, makes the loader ignore VK_ADD_LAYER_PATH.
    if (std::wstring path = ini.empty() ? L"" : ini_value(ini, L"layer_path"), layer = ini.empty() ? L"" : ini_value(ini, L"layer");
        !path.empty() && !layer.empty()) {
        prepend(GetEnvironmentVariableW(L"VK_LAYER_PATH", nullptr, 0) ? L"VK_LAYER_PATH" : L"VK_ADD_LAYER_PATH", path);
        prepend(L"VK_INSTANCE_LAYERS", layer);
        if (std::wstring var = ini_value(ini, L"dump_var"); !var.empty()) SetEnvironmentVariableW(var.c_str(), ini_value(ini, L"dump").c_str());
    }
    // disable=<name>: an implicit layer's disable variable, set in this process only (two copies of Steam's layer)
    if (std::wstring off = ini.empty() ? L"" : ini_value(ini, L"disable"); !off.empty()) SetEnvironmentVariableW(off.c_str(), L"1");
    wchar_t sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    g_real = LoadLibraryW((std::wstring(sys) + L"\\vulkan-1.dll").c_str());
    if (g_real == self) FreeLibrary(g_real), g_real = nullptr;  // DLL redirection (.local) gave this dll back: its exports would call themselves
    if (!g_real) return FALSE;
    // an export the real loader lacks would be a jump to nothing: refuse to load instead
    for (size_t i = 0; i < sizeof kVkNames / sizeof *kVkNames; ++i)
        if (!(vk_real[i] = (void*)GetProcAddress(g_real, kVkNames[i]))) return FreeLibrary(g_real), g_real = nullptr, FALSE;
    return TRUE;
}

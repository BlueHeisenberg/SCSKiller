#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d9.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

struct ShaderBlob {
    bool vertex;
    std::vector<std::uint8_t> bytes;
};

class Reader {
public:
    explicit Reader(std::vector<std::uint8_t> data) : data_(std::move(data)) {}

    std::uint8_t u8() {
        need(1);
        return data_[pos_++];
    }

    std::uint16_t u16() {
        need(2);
        const auto v = static_cast<std::uint16_t>(data_[pos_])
            | static_cast<std::uint16_t>(data_[pos_ + 1] << 8);
        pos_ += 2;
        return v;
    }

    std::uint32_t u32() {
        need(4);
        const auto v = static_cast<std::uint32_t>(data_[pos_])
            | (static_cast<std::uint32_t>(data_[pos_ + 1]) << 8)
            | (static_cast<std::uint32_t>(data_[pos_ + 2]) << 16)
            | (static_cast<std::uint32_t>(data_[pos_ + 3]) << 24);
        pos_ += 4;
        return v;
    }

    void skip(std::size_t n) {
        need(n);
        pos_ += n;
    }

    std::vector<std::uint8_t> take(std::size_t n) {
        need(n);
        std::vector<std::uint8_t> out(data_.begin() + static_cast<std::ptrdiff_t>(pos_),
                                      data_.begin() + static_cast<std::ptrdiff_t>(pos_ + n));
        pos_ += n;
        return out;
    }

private:
    void need(std::size_t n) const {
        if (n > data_.size() - pos_) throw std::runtime_error("truncated FXC");
    }

    std::vector<std::uint8_t> data_;
    std::size_t pos_ = 0;
};

static std::vector<std::uint8_t> read_file(const fs::path& path) {
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f) throw std::runtime_error("couldn't open FXC");
    const auto end = f.tellg();
    if (end < 0) throw std::runtime_error("couldn't size FXC");
    std::vector<std::uint8_t> data(static_cast<std::size_t>(end));
    f.seekg(0);
    if (!data.empty() && !f.read(reinterpret_cast<char*>(data.data()), static_cast<std::streamsize>(data.size())))
        throw std::runtime_error("couldn't read FXC");
    return data;
}

static std::vector<std::uint8_t> read_program(Reader& r) {
    const auto parameters = r.u8();
    for (std::uint8_t i = 0; i < parameters; ++i) {
        r.skip(4);
        r.skip(r.u8());
    }
    const auto size = r.u16();
    r.skip(2);
    return r.take(size);
}

static std::vector<ShaderBlob> read_fxc(const fs::path& path) {
    Reader r(read_file(path));
    if (r.u32() != 0x61786772u) throw std::runtime_error("not a RAGE FXC");

    std::vector<ShaderBlob> out;
    const auto vs = r.u8();
    out.reserve(vs + 16);
    for (std::uint8_t i = 0; i < vs; ++i) {
        auto bytes = read_program(r);
        if (!bytes.empty()) out.push_back({true, std::move(bytes)});
    }

    const auto ps = r.u8();
    out.reserve(out.size() + ps);
    for (std::uint8_t i = 0; i < ps; ++i) {
        auto bytes = read_program(r);
        if (!bytes.empty()) out.push_back({false, std::move(bytes)});
    }
    return out;
}

static std::string key_of(const ShaderBlob& shader) {
    std::string key;
    key.reserve(shader.bytes.size() + 1);
    key.push_back(shader.vertex ? 'V' : 'P');
    key.append(reinterpret_cast<const char*>(shader.bytes.data()), shader.bytes.size());
    return key;
}

static std::uint32_t token(const ShaderBlob& shader) {
    if (shader.bytes.size() < 4) return 0;
    return static_cast<std::uint32_t>(shader.bytes[0])
        | (static_cast<std::uint32_t>(shader.bytes[1]) << 8)
        | (static_cast<std::uint32_t>(shader.bytes[2]) << 16)
        | (static_cast<std::uint32_t>(shader.bytes[3]) << 24);
}

static std::string utf8(const std::wstring& s) {
    if (s.empty()) return {};
    const auto n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string out(static_cast<std::size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), out.data(), n, nullptr, nullptr);
    return out;
}

static std::wstring module_path(HMODULE module) {
    std::wstring path(32768, L'\0');
    const auto n = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (n == 0 || n >= path.size()) return {};
    path.resize(n);
    return path;
}

using Direct3DCreate9Fn = IDirect3D9* (WINAPI*)(UINT);

int wmain(int argc, wchar_t** argv) {
    if (argc != 2 || !fs::is_directory(argv[1])) {
        std::cerr << "usage: scskiller_gta4_warm.exe <GTAIV folder>\n";
        return 2;
    }

    const fs::path root = fs::absolute(argv[1]);
    std::unordered_set<std::string> seen;
    std::vector<ShaderBlob> shaders;
    std::size_t fxc_count = 0;
    std::size_t bad = 0;

    for (const auto& dir : {root / L"common" / L"shaders", root / L"update" / L"common" / L"shaders"}) {
        std::error_code ec;
        if (!fs::is_directory(dir, ec)) continue;
        for (fs::recursive_directory_iterator it(dir, fs::directory_options::skip_permission_denied, ec), end; it != end; it.increment(ec)) {
            if (ec) { ec.clear(); continue; }
            if (!it->is_regular_file(ec) || _wcsicmp(it->path().extension().c_str(), L".fxc") != 0) continue;
            ++fxc_count;
            try {
                for (auto& shader : read_fxc(it->path())) {
                    auto key = key_of(shader);
                    if (seen.insert(std::move(key)).second) shaders.push_back(std::move(shader));
                }
            } catch (const std::exception& e) {
                ++bad;
                std::cerr << "fxc-error " << utf8(it->path().filename().wstring()) << ": " << e.what() << "\n";
            }
        }
    }

    const auto vs = static_cast<std::size_t>(std::count_if(shaders.begin(), shaders.end(), [](const ShaderBlob& s) { return s.vertex; }));
    const auto ps = shaders.size() - vs;
    std::cout << "fxc=" << fxc_count << " bad=" << bad << " unique=" << shaders.size() << " vs=" << vs << " ps=" << ps << "\n";

    const auto invalid = static_cast<std::size_t>(std::count_if(shaders.begin(), shaders.end(), [](const ShaderBlob& s) {
        return token(s) != (s.vertex ? 0xFFFE0300u : 0xFFFF0300u);
    }));
    if (invalid != 0 || shaders.empty()) {
        std::cerr << "invalid shader tokens=" << invalid << "\n";
        return 3;
    }

    const auto dxvk_path = root / L"vulkan.dll";
    if (!fs::is_regular_file(dxvk_path)) {
        std::cerr << "DXVK not found\n";
        return 5;
    }

    HMODULE dxvk = LoadLibraryW(dxvk_path.c_str());
    if (!dxvk) {
        std::cerr << "couldn't load DXVK, win32=" << GetLastError() << "\n";
        return 6;
    }
    const auto free_dxvk = [&] { FreeLibrary(dxvk); };

    auto create9 = reinterpret_cast<Direct3DCreate9Fn>(GetProcAddress(dxvk, "Direct3DCreate9"));
    if (!create9) {
        std::cerr << "DXVK has no Direct3DCreate9 export\n";
        free_dxvk();
        return 7;
    }
    std::cout << "dxvk=" << utf8(module_path(dxvk)) << "\n";

    IDirect3D9* d3d = create9(D3D_SDK_VERSION);
    if (!d3d) {
        std::cerr << "Direct3DCreate9 failed\n";
        free_dxvk();
        return 8;
    }

    const HWND hwnd = GetDesktopWindow();
    D3DPRESENT_PARAMETERS pp{};
    pp.BackBufferWidth = 1;
    pp.BackBufferHeight = 1;
    pp.BackBufferFormat = D3DFMT_UNKNOWN;
    pp.BackBufferCount = 1;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.hDeviceWindow = hwnd;
    pp.Windowed = TRUE;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;

    IDirect3DDevice9* device = nullptr;
    auto flags = D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE;
    HRESULT hr = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, hwnd, flags, &pp, &device);
    if (FAILED(hr)) {
        flags = D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE;
        hr = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, hwnd, flags, &pp, &device);
    }
    if (FAILED(hr) || !device) {
        std::cerr << "CreateDevice failed, hr=0x" << std::hex << static_cast<unsigned long>(hr) << std::dec << "\n";
        d3d->Release();
        free_dxvk();
        return 9;
    }

    std::size_t done = 0;
    std::size_t failed = 0;
    const auto started = std::chrono::steady_clock::now();
    for (const auto& shader : shaders) {
        HRESULT create_hr = E_FAIL;
        if (shader.vertex) {
            IDirect3DVertexShader9* object = nullptr;
            create_hr = device->CreateVertexShader(reinterpret_cast<const DWORD*>(shader.bytes.data()), &object);
            if (object) object->Release();
        } else {
            IDirect3DPixelShader9* object = nullptr;
            create_hr = device->CreatePixelShader(reinterpret_cast<const DWORD*>(shader.bytes.data()), &object);
            if (object) object->Release();
        }
        if (FAILED(create_hr)) {
            ++failed;
            if (failed <= 20) std::cerr << "create-error " << (shader.vertex ? "VS" : "PS") << " hr=0x"
                                        << std::hex << static_cast<unsigned long>(create_hr) << std::dec << "\n";
        }
        ++done;
        if (done % 100 == 0 || done == shaders.size())
            std::cout << "progress=" << done << "/" << shaders.size() << " failed=" << failed << "\n";
    }

    const auto elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    std::cout << "done=" << done << " failed=" << failed << " seconds=" << std::fixed << std::setprecision(2) << elapsed << "\n";

    device->Release();
    d3d->Release();
    free_dxvk();
    return failed == 0 ? 0 : 4;
}

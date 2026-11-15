// The D3D11 shader creates under the signed shaders=1, for a game whose files hold no shader a reader can read: the
// recording is the only place its bytes come from. Each is a bare 'B': the planner reads the stage from the bytecode.
// Included after record_shader and patch. The device's vtable is the runtime's, shared and fixed.
enum { D11_VS = 12, D11_GS = 13, D11_SO = 14, D11_PS = 15, D11_HS = 16, D11_DS = 17, D11_CS = 18 };  // vtslots.cpp checks them against the headers at compile time

using PFN_Sh11 = HRESULT(STDMETHODCALLTYPE*)(void*, const void*, SIZE_T, void*, void**);
using PFN_So11 = HRESULT(STDMETHODCALLTYPE*)(void*, const void*, SIZE_T, const void*, UINT, const UINT*, UINT, UINT, void*, void**);
template <int Slot> static PFN_Sh11 o_sh11;
static PFN_So11 o_so11;

template <int Slot> static HRESULT STDMETHODCALLTYPE hk_sh11(void* d, const void* code, SIZE_T n, void* linkage, void** pp) {
    const HRESULT hr = o_sh11<Slot>(d, code, n, linkage, pp);
    if (SUCCEEDED(hr) && pp && *pp) record_shader(code, n);
    return hr;
}
static HRESULT STDMETHODCALLTYPE hk_so11(void* d, const void* code, SIZE_T n, const void* decl, UINT entries, const UINT* strides, UINT nstrides, UINT stream, void* linkage, void** pp) {
    const HRESULT hr = o_so11(d, code, n, decl, entries, strides, nstrides, stream, linkage, pp);
    if (SUCCEEDED(hr) && pp && *pp) record_shader(code, n);
    return hr;
}

template <int... Slots> static void patch_sh11(void** vt) { (patch(vt, Slots, (void*)&hk_sh11<Slots>, o_sh11<Slots>), ...); }
static void hook_shaders11(void** vt) {
    patch_sh11<D11_VS, D11_GS, D11_PS, D11_HS, D11_DS, D11_CS>(vt);
    patch(vt, D11_SO, (void*)&hk_so11, o_so11);
}

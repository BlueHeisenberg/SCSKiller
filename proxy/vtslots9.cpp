// Compile-time check of the D3D9 vtable slots d3d9rec.cpp patches, as vtslots.cpp does for D3D12. Linked into nothing.
#define CINTERFACE
#include <windows.h>
#include <d3d9.h>
#include <cstddef>

#define SLOT(I, M, N) static_assert(offsetof(I##Vtbl, M) == (N) * sizeof(void*), #I "::" #M " is not at vtable slot " #N);
SLOT(IDirect3D9, CreateDevice, 16)
SLOT(IDirect3D9Ex, CreateDeviceEx, 20)
SLOT(IDirect3DDevice9, Reset, 16)
SLOT(IDirect3DDevice9, SetRenderTarget, 37)
SLOT(IDirect3DDevice9, SetDepthStencilSurface, 39)
SLOT(IDirect3DDevice9, SetRenderState, 57)
SLOT(IDirect3DDevice9, CreateStateBlock, 59)
SLOT(IDirect3DDevice9, BeginStateBlock, 60)
SLOT(IDirect3DDevice9, EndStateBlock, 61)
SLOT(IDirect3DDevice9, SetTexture, 65)
SLOT(IDirect3DDevice9, SetSamplerState, 69)
SLOT(IDirect3DDevice9, DrawPrimitive, 81)
SLOT(IDirect3DDevice9, DrawIndexedPrimitive, 82)
SLOT(IDirect3DDevice9, DrawPrimitiveUP, 83)
SLOT(IDirect3DDevice9, DrawIndexedPrimitiveUP, 84)
SLOT(IDirect3DDevice9, SetVertexDeclaration, 87)
SLOT(IDirect3DDevice9, SetFVF, 89)
SLOT(IDirect3DDevice9, CreateVertexShader, 91)
SLOT(IDirect3DDevice9, SetVertexShader, 92)
SLOT(IDirect3DDevice9, CreatePixelShader, 106)
SLOT(IDirect3DDevice9, SetPixelShader, 107)
SLOT(IDirect3DDevice9Ex, ResetEx, 132)
SLOT(IDirect3DStateBlock9, Apply, 5)
SLOT(IDirect3DStateBlock9, Capture, 4)
#define METHODS(I, N) static_assert(sizeof(I##Vtbl) == (N) * sizeof(void*), #I " doesn't have " #N " methods");
METHODS(IDirect3D9, 17)
METHODS(IDirect3D9Ex, 22)
METHODS(IDirect3DDevice9, 119)
METHODS(IDirect3DDevice9Ex, 134)
METHODS(IDirect3DStateBlock9, 6)

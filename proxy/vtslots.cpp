// Compile-time check of the D3D12, D3D11 and DXGI vtable slots proxy.cpp patches: the C interface's Vtbl structs
// list the methods in COM ABI order, so a slot is the method's offset in its Vtbl. Nothing here is linked into anything.
#define CINTERFACE
#include <windows.h>
#include <d3d12.h>
#define D3D11_NO_HELPERS  // its C++ helpers need the C++ interfaces
#include <d3d11_3.h>
#include <dxgi1_4.h>
#include <cstddef>

#define SLOT(I, M, N) static_assert(offsetof(I##Vtbl, M) == (N) * sizeof(void*), #I "::" #M " is not at vtable slot " #N);
SLOT(ID3D12Device, CreateGraphicsPipelineState, 10)
SLOT(ID3D12Device, CreateComputePipelineState, 11)
SLOT(ID3D12Device, CreateRootSignature, 16)
SLOT(ID3D12Device1, CreatePipelineLibrary, 44)
SLOT(ID3D12Device2, CreatePipelineState, 47)
SLOT(ID3D12Device5, CreateStateObject, 62)
SLOT(ID3D12Device7, AddToStateObject, 66)
SLOT(ID3D12PipelineLibrary, LoadGraphicsPipeline, 9)
SLOT(ID3D12PipelineLibrary, LoadComputePipeline, 10)
SLOT(ID3D12PipelineLibrary1, LoadPipeline, 13)
SLOT(ID3D12DeviceFactory, CreateDevice, 9)
SLOT(ID3D12SDKConfiguration1, CreateDeviceFactory, 4)
SLOT(IDXGISwapChain, Present, 8)
SLOT(IDXGISwapChain1, Present1, 22)
SLOT(IDXGISwapChain3, SetColorSpace1, 38)
SLOT(IDXGIFactory, CreateSwapChain, 10)
SLOT(IDXGIFactory2, CreateSwapChainForHwnd, 15)
SLOT(IDXGIFactory2, CreateSwapChainForCoreWindow, 16)
SLOT(IDXGIFactory2, CreateSwapChainForComposition, 24)
SLOT(ID3D11Device, CreateInputLayout, 11)
static_assert(sizeof(D3D11_INPUT_ELEMENT_DESC) == sizeof(D3D12_INPUT_ELEMENT_DESC) && offsetof(D3D11_INPUT_ELEMENT_DESC, InstanceDataStepRate) ==
              offsetof(D3D12_INPUT_ELEMENT_DESC, InstanceDataStepRate), "the D3D11 recorder writes a D3D11 layout as a D3D12 one");

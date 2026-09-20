#define ONEVPL_EXPERIMENTAL
#include <windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <vpl/mfxdispatcher.h>
#include <vpl/mfxvideo.h>
#include <vpl/mfxmemory.h>

#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

template <typename T>
T RequireExport(HMODULE module, const char* name) {
    auto value = GetProcAddress(module, name);
    if (!value) throw std::runtime_error(std::string("Missing libvpl export: ") + name);
    return reinterpret_cast<T>(value);
}

void CheckHresult(HRESULT result, const char* operation) {
    if (FAILED(result)) throw std::runtime_error(std::string(operation) + " HRESULT=" + std::to_string(result));
}

void CheckStatus(mfxStatus result, const char* operation) {
    if (result < MFX_ERR_NONE) throw std::runtime_error(std::string(operation) + " mfxStatus=" + std::to_string(result));
}

int main(int argc, char** argv) {
    IDXGIFactory1* factory = nullptr;
    IDXGIAdapter1* adapter = nullptr;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    ID3D11Multithread* multithread = nullptr;
    ID3D11Texture2D* texture = nullptr;
    HMODULE vpl = nullptr;
    mfxLoader loader = nullptr;
    mfxSession session = nullptr;
    mfxFrameSurface1* imported = nullptr;
    mfxFrameSurface1* internalSurface = nullptr;
    mfxSurfaceD3D11Tex2D* exported = nullptr;

    try {
        const bool sharedOnly = argc == 2 && std::string(argv[1]) == "--shared-only";
        const bool exportShared = argc == 2 && std::string(argv[1]) == "--export-shared";
        if (argc > 1 && !sharedOnly && !exportShared)
            throw std::runtime_error("Usage: NativeVplProbe [--shared-only|--export-shared]");
        CheckHresult(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "CreateDXGIFactory1");
        DXGI_ADAPTER_DESC1 description{};
        for (UINT index = 0; factory->EnumAdapters1(index, &adapter) != DXGI_ERROR_NOT_FOUND; index++) {
            adapter->GetDesc1(&description);
            if (wcsstr(description.Description, L"Intel")) break;
            adapter->Release();
            adapter = nullptr;
        }
        if (!adapter) throw std::runtime_error("No Intel DXGI adapter found.");
        D3D_FEATURE_LEVEL actual{};
        const D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        CheckHresult(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT, requested, ARRAYSIZE(requested),
            D3D11_SDK_VERSION, &device, &actual, &context), "D3D11CreateDevice");
        CheckHresult(device->QueryInterface(IID_PPV_ARGS(&multithread)), "Query ID3D11Multithread");
        multithread->SetMultithreadProtected(TRUE);
        D3D11_TEXTURE2D_DESC textureDescription{};
        textureDescription.Width = 2880;
        textureDescription.Height = 1824;
        textureDescription.MipLevels = 1;
        textureDescription.ArraySize = 1;
        textureDescription.Format = DXGI_FORMAT_NV12;
        textureDescription.SampleDesc.Count = 1;
        textureDescription.Usage = D3D11_USAGE_DEFAULT;
        textureDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
        textureDescription.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
        CheckHresult(device->CreateTexture2D(&textureDescription, nullptr, &texture), "Create shared NV12 texture");

        vpl = LoadLibraryW(L"libvpl.dll");
        if (!vpl) throw std::runtime_error("LoadLibrary(libvpl.dll) failed.");
        auto MFXLoadFn = RequireExport<decltype(&MFXLoad)>(vpl, "MFXLoad");
        auto MFXUnloadFn = RequireExport<decltype(&MFXUnload)>(vpl, "MFXUnload");
        auto MFXCreateConfigFn = RequireExport<decltype(&MFXCreateConfig)>(vpl, "MFXCreateConfig");
        auto MFXSetConfigFilterPropertyFn = RequireExport<decltype(&MFXSetConfigFilterProperty)>(vpl, "MFXSetConfigFilterProperty");
        auto MFXCreateSessionFn = RequireExport<decltype(&MFXCreateSession)>(vpl, "MFXCreateSession");
        auto MFXCloseFn = RequireExport<decltype(&MFXClose)>(vpl, "MFXClose");
        auto MFXVideoCORE_SetHandleFn = RequireExport<decltype(&MFXVideoCORE_SetHandle)>(vpl, "MFXVideoCORE_SetHandle");
        auto MFXVideoCORE_GetHandleFn = RequireExport<decltype(&MFXVideoCORE_GetHandle)>(vpl, "MFXVideoCORE_GetHandle");
        auto MFXVideoENCODE_QueryFn = RequireExport<decltype(&MFXVideoENCODE_Query)>(vpl, "MFXVideoENCODE_Query");
        auto MFXVideoENCODE_InitFn = RequireExport<decltype(&MFXVideoENCODE_Init)>(vpl, "MFXVideoENCODE_Init");
        auto MFXVideoENCODE_CloseFn = RequireExport<decltype(&MFXVideoENCODE_Close)>(vpl, "MFXVideoENCODE_Close");
        auto MFXMemory_GetSurfaceForEncodeFn = RequireExport<decltype(&MFXMemory_GetSurfaceForEncode)>(vpl, "MFXMemory_GetSurfaceForEncode");

        loader = MFXLoadFn();
        if (!loader) throw std::runtime_error("MFXLoad failed.");
        auto config = MFXCreateConfigFn(loader);
        if (!config) throw std::runtime_error("MFXCreateConfig hardware filter failed.");
        mfxVariant property{};
        property.Type = MFX_VARIANT_TYPE_U32;
        property.Data.U32 = MFX_IMPL_TYPE_HARDWARE;
        CheckStatus(MFXSetConfigFilterPropertyFn(config, reinterpret_cast<mfxU8*>(const_cast<char*>("mfxImplDescription.Impl")), property), "Filter hardware implementation");
        config = MFXCreateConfigFn(loader);
        if (!config) throw std::runtime_error("MFXCreateConfig D3D11 filter failed.");
        property.Data.U32 = MFX_ACCEL_MODE_VIA_D3D11;
        CheckStatus(MFXSetConfigFilterPropertyFn(config, reinterpret_cast<mfxU8*>(const_cast<char*>("mfxImplDescription.AccelerationMode")), property), "Filter D3D11 acceleration");
        config = MFXCreateConfigFn(loader);
        if (!config) throw std::runtime_error("MFXCreateConfig H.264 filter failed.");
        property.Data.U32 = MFX_CODEC_AVC;
        CheckStatus(MFXSetConfigFilterPropertyFn(config, reinterpret_cast<mfxU8*>(const_cast<char*>("mfxImplDescription.mfxEncoderDescription.encoder.CodecID")), property), "Filter H.264 encoder");
        config = MFXCreateConfigFn(loader);
        if (!config) throw std::runtime_error("MFXCreateConfig API filter failed.");
        mfxVersion requiredVersion{};
        requiredVersion.Major = 2;
        requiredVersion.Minor = 10;
        property.Data.U32 = requiredVersion.Version;
        CheckStatus(MFXSetConfigFilterPropertyFn(config, reinterpret_cast<mfxU8*>(const_cast<char*>("mfxImplDescription.ApiVersion.Version")), property), "Filter oneVPL 2.10");
        CheckStatus(MFXCreateSessionFn(loader, 0, &session), "MFXCreateSession");
        CheckStatus(MFXVideoCORE_SetHandleFn(session, MFX_HANDLE_D3D11_DEVICE, device), "Set D3D11 device");

        mfxVideoParam parameters{};
        parameters.mfx.CodecId = MFX_CODEC_AVC;
        parameters.mfx.CodecProfile = MFX_PROFILE_AVC_HIGH;
        parameters.mfx.TargetUsage = MFX_TARGETUSAGE_BEST_SPEED;
        parameters.mfx.GopPicSize = 300;
        parameters.mfx.GopRefDist = 1;
        parameters.mfx.RateControlMethod = MFX_RATECONTROL_CQP;
        parameters.mfx.QPI = parameters.mfx.QPP = parameters.mfx.QPB = 26;
        parameters.mfx.FrameInfo.FourCC = MFX_FOURCC_NV12;
        parameters.mfx.FrameInfo.ChromaFormat = MFX_CHROMAFORMAT_YUV420;
        parameters.mfx.FrameInfo.PicStruct = MFX_PICSTRUCT_PROGRESSIVE;
        parameters.mfx.FrameInfo.FrameRateExtN = 30;
        parameters.mfx.FrameInfo.FrameRateExtD = 1;
        parameters.mfx.FrameInfo.Width = 2880;
        parameters.mfx.FrameInfo.Height = 1824;
        parameters.mfx.FrameInfo.CropW = 2880;
        parameters.mfx.FrameInfo.CropH = 1800;
        parameters.AsyncDepth = 1;
        parameters.IOPattern = MFX_IOPATTERN_IN_VIDEO_MEMORY;
        CheckStatus(MFXVideoENCODE_QueryFn(session, &parameters, &parameters), "MFXVideoENCODE_Query");
        CheckStatus(MFXVideoENCODE_InitFn(session, &parameters), "MFXVideoENCODE_Init");
        if (exportShared) {
            CheckStatus(MFXMemory_GetSurfaceForEncodeFn(session, &internalSurface), "Get oneVPL encode surface");
            mfxSurfaceHeader exportRequest{};
            exportRequest.SurfaceType = MFX_SURFACE_TYPE_D3D11_TEX2D;
            exportRequest.SurfaceFlags = MFX_SURFACE_FLAG_EXPORT_SHARED;
            exportRequest.StructSize = sizeof(mfxSurfaceD3D11Tex2D);
            mfxSurfaceHeader* exportedHeader = nullptr;
            CheckStatus(internalSurface->FrameInterface->Export(internalSurface, exportRequest, &exportedHeader), "Export oneVPL encode surface as D3D11 texture");
            exported = reinterpret_cast<mfxSurfaceD3D11Tex2D*>(exportedHeader);
            if (!exported->texture2D) throw std::runtime_error("Exported D3D11 texture is null.");
            std::cout << "adapter=Intel; oneVPL-export=success; actualExportFlags="
                << exported->SurfaceInterface.Header.SurfaceFlags << "; surface=2880x1824 NV12" << std::endl;
            exported->SurfaceInterface.Release(&exported->SurfaceInterface);
            exported = nullptr;
            internalSurface->FrameInterface->Release(internalSurface);
            internalSurface = nullptr;
            MFXVideoENCODE_CloseFn(session);
            MFXCloseFn(session);
            session = nullptr;
            MFXUnloadFn(loader);
            loader = nullptr;
            texture->Release(); multithread->Release(); context->Release(); device->Release(); adapter->Release(); factory->Release(); FreeLibrary(vpl);
            return 0;
        }
        mfxMemoryInterface* memory = nullptr;
        CheckStatus(MFXVideoCORE_GetHandleFn(session, MFX_HANDLE_MEMORY_INTERFACE, reinterpret_cast<mfxHDL*>(&memory)), "Get oneVPL memory interface");
        if (!memory || !memory->ImportFrameSurface) throw std::runtime_error("oneVPL memory interface has no import function.");

        mfxSurfaceD3D11Tex2D external{};
        external.SurfaceInterface.Header.SurfaceType = MFX_SURFACE_TYPE_D3D11_TEX2D;
        external.SurfaceInterface.Header.SurfaceFlags = sharedOnly ? MFX_SURFACE_FLAG_IMPORT_SHARED :
            MFX_SURFACE_FLAG_IMPORT_SHARED | MFX_SURFACE_FLAG_IMPORT_COPY;
        external.SurfaceInterface.Header.StructSize = sizeof(external);
        external.texture2D = texture;
        CheckStatus(memory->ImportFrameSurface(memory, MFX_SURFACE_COMPONENT_ENCODE, &external.SurfaceInterface.Header, &imported), "Import shared D3D11 NV12 texture");
        std::cout << "adapter=Intel; featureLevel=" << std::hex << actual << std::dec
            << "; oneVPL-import=success; requested=" << (sharedOnly ? "shared" : "shared-or-copy")
            << "; actualImportFlags=" << external.SurfaceInterface.Header.SurfaceFlags
            << "; surface=2880x1824 NV12" << std::endl;

        imported->FrameInterface->Release(imported);
        imported = nullptr;
        MFXVideoENCODE_CloseFn(session);
        MFXCloseFn(session);
        session = nullptr;
        MFXUnloadFn(loader);
        loader = nullptr;
        texture->Release(); multithread->Release(); context->Release(); device->Release(); adapter->Release(); factory->Release(); FreeLibrary(vpl);
        return 0;
    }
    catch (const std::exception& error) {
        std::cerr << "NativeVplProbe failed: " << error.what() << std::endl;
        if (imported) imported->FrameInterface->Release(imported);
        if (exported) exported->SurfaceInterface.Release(&exported->SurfaceInterface);
        if (internalSurface) internalSurface->FrameInterface->Release(internalSurface);
        if (session) {
            auto close = vpl ? reinterpret_cast<decltype(&MFXClose)>(GetProcAddress(vpl, "MFXClose")) : nullptr;
            if (close) close(session);
        }
        if (loader && vpl) {
            auto unload = reinterpret_cast<decltype(&MFXUnload)>(GetProcAddress(vpl, "MFXUnload"));
            if (unload) unload(loader);
        }
        if (texture) texture->Release();
        if (multithread) multithread->Release();
        if (context) context->Release();
        if (device) device->Release();
        if (adapter) adapter->Release();
        if (factory) factory->Release();
        if (vpl) FreeLibrary(vpl);
        return 2;
    }
}

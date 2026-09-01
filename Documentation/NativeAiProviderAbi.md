# Native AI Provider ABI

MediaFlux native providers negotiate `AiProviderSdkVersion` before initialization. A provider must reject an unsupported major SDK version without allocating resources.

The native ABI must define opaque provider and model handles. The caller owns request memory unless the request marks it borrowed; output buffers are provider-owned until the caller releases the returned image. Every allocation has one matching release operation.

Providers receive cancellation and progress callbacks for each request, return structured errors rather than process termination, and send concise diagnostics through the host logging callback. Shutdown and `ReleaseResources` are idempotent and must release all provider-owned memory before returning.

## ABI version 1.0

The Windows ABI uses a versioned `MediaFlux.NativeAiProvider.dll`, C linkage, and the `cdecl` calling convention. Exported names are `MfAi_GetVersion`, `MfAi_Initialize`, `MfAi_Shutdown`, `MfAi_GetCapabilities`, `MfAi_CreateProvider`, `MfAi_DestroyProvider`, `MfAi_CancelOperation`, `MfAi_ReleaseResources`, `MfAi_GetLastError`, `MfAi_RegisterLogger`, `MfAi_RegisterProgressCallback`, and `MfAi_ProcessImage`.

`MfAi_Initialize` accepts the requested major/minor version and returns the negotiated version. Major versions must match and the provider minor version must be at least the requested minor version. Version rejection happens before provider allocation.

Provider values are opaque handles created and destroyed by the matching exports. `MfAi_GetLastError` uses a two-call UTF-8 buffer-size protocol. Logger and progress callback functions and their caller-owned context pointers remain valid until the provider is destroyed or the callback is replaced. No C++ exception may cross the ABI boundary.

ABI 1.0 exposes a stub provider only. `MfAi_ProcessImage` returns `MF_AI_NOT_IMPLEMENTED`; it does not allocate output memory or execute inference.

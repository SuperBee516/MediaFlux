#include "MediaFlux.NativeAiProvider.h"

#include <atomic>
#include <cstring>
#include <mutex>
#include <new>
#include <string>

namespace
{
    constexpr MfAiVersion kVersion{1, 0, 0};
    std::atomic_bool g_initialized{false};

    struct Provider
    {
        std::mutex gate;
        std::atomic_bool cancelled{false};
        std::string last_error;
        MfAiLogger logger{};
        void* logger_context{};
        MfAiProgress progress{};
        void* progress_context{};
    };

    Provider* provider_from(void* handle) noexcept { return static_cast<Provider*>(handle); }

    void set_error(Provider* provider, const char* message) noexcept
    {
        if (provider == nullptr) return;
        try { std::scoped_lock lock(provider->gate); provider->last_error = message == nullptr ? "Unknown native provider error." : message; }
        catch (...) { }
    }

    void log(Provider* provider, std::int32_t level, const char* message) noexcept
    {
        if (provider == nullptr || provider->logger == nullptr) return;
        try { provider->logger(level, message, provider->logger_context); }
        catch (...) { }
    }
}

MfAiStatus MF_AI_CALL MfAi_GetVersion(MfAiVersion* version)
{
    if (version == nullptr) return MF_AI_INVALID_ARGUMENT;
    *version = kVersion; return MF_AI_OK;
}

MfAiStatus MF_AI_CALL MfAi_Initialize(const MfAiVersion* requested, MfAiVersion* negotiated)
{
    if (requested == nullptr || negotiated == nullptr) return MF_AI_INVALID_ARGUMENT;
    *negotiated = kVersion;
    if (requested->major != kVersion.major || requested->minor > kVersion.minor) return MF_AI_SDK_MISMATCH;
    g_initialized.store(true, std::memory_order_release); return MF_AI_OK;
}

MfAiStatus MF_AI_CALL MfAi_Shutdown() { g_initialized.store(false, std::memory_order_release); return MF_AI_OK; }

MfAiStatus MF_AI_CALL MfAi_GetCapabilities(MfAiCapabilities* capabilities)
{
    if (capabilities == nullptr || capabilities->struct_size < sizeof(MfAiCapabilities)) return MF_AI_INVALID_ARGUMENT;
    capabilities->flags = MF_AI_CAP_CANCELLATION | MF_AI_CAP_LOGGING | MF_AI_CAP_PROGRESS | MF_AI_CAP_STUB_PROVIDER; return MF_AI_OK;
}

MfAiStatus MF_AI_CALL MfAi_CreateProvider(void** provider)
{
    if (provider == nullptr) return MF_AI_INVALID_ARGUMENT;
    *provider = nullptr;
    if (!g_initialized.load(std::memory_order_acquire)) return MF_AI_INTERNAL_ERROR;
    try { *provider = new Provider(); return MF_AI_OK; }
    catch (const std::bad_alloc&) { return MF_AI_INTERNAL_ERROR; }
    catch (...) { return MF_AI_INTERNAL_ERROR; }
}

MfAiStatus MF_AI_CALL MfAi_DestroyProvider(void* provider)
{
    if (provider == nullptr) return MF_AI_INVALID_HANDLE;
    try { delete provider_from(provider); return MF_AI_OK; }
    catch (...) { return MF_AI_INTERNAL_ERROR; }
}

MfAiStatus MF_AI_CALL MfAi_CancelOperation(void* provider)
{
    Provider* value = provider_from(provider); if (value == nullptr) return MF_AI_INVALID_HANDLE;
    value->cancelled.store(true, std::memory_order_release); log(value, 1, "Operation cancellation requested."); return MF_AI_OK;
}

MfAiStatus MF_AI_CALL MfAi_ReleaseResources(void* provider)
{
    Provider* value = provider_from(provider); if (value == nullptr) return MF_AI_INVALID_HANDLE;
    value->cancelled.store(false, std::memory_order_release); log(value, 0, "Provider resources released."); return MF_AI_OK;
}

MfAiStatus MF_AI_CALL MfAi_GetLastError(void* provider, char* buffer, std::size_t* buffer_size)
{
    Provider* value = provider_from(provider); if (value == nullptr || buffer_size == nullptr) return MF_AI_INVALID_ARGUMENT;
    std::scoped_lock lock(value->gate); const std::size_t required = value->last_error.size() + 1;
    if (buffer == nullptr || *buffer_size < required) { *buffer_size = required; return buffer == nullptr ? MF_AI_OK : MF_AI_INVALID_ARGUMENT; }
    std::memcpy(buffer, value->last_error.c_str(), required); *buffer_size = required; return MF_AI_OK;
}

MfAiStatus MF_AI_CALL MfAi_RegisterLogger(void* provider, MfAiLogger callback, void* context)
{
    Provider* value = provider_from(provider); if (value == nullptr) return MF_AI_INVALID_HANDLE;
    std::scoped_lock lock(value->gate); value->logger = callback; value->logger_context = context; return MF_AI_OK;
}

MfAiStatus MF_AI_CALL MfAi_RegisterProgressCallback(void* provider, MfAiProgress callback, void* context)
{
    Provider* value = provider_from(provider); if (value == nullptr) return MF_AI_INVALID_HANDLE;
    std::scoped_lock lock(value->gate); value->progress = callback; value->progress_context = context; return MF_AI_OK;
}

MfAiStatus MF_AI_CALL MfAi_ProcessImage(void* provider, const MfAiImage*, MfAiImage*)
{
    Provider* value = provider_from(provider); if (value == nullptr) return MF_AI_INVALID_HANDLE;
    if (value->cancelled.load(std::memory_order_acquire)) { set_error(value, "Operation was cancelled."); return MF_AI_CANCELLED; }
    set_error(value, "ProcessImage is not implemented by the stub provider."); log(value, 2, "ProcessImage returned NotImplemented."); return MF_AI_NOT_IMPLEMENTED;
}

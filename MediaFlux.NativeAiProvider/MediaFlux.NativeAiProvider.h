#pragma once

#include <cstddef>
#include <cstdint>

#if defined(_WIN32)
#define MF_AI_EXPORT extern "C" __declspec(dllexport)
#define MF_AI_CALL __cdecl
#else
#define MF_AI_EXPORT extern "C"
#define MF_AI_CALL
#endif

struct MfAiVersion { std::uint32_t major; std::uint32_t minor; std::uint32_t patch; };
struct MfAiCapabilities { std::uint32_t struct_size; std::uint64_t flags; };
struct MfAiImage { std::uint32_t struct_size; std::int32_t width; std::int32_t height; std::int32_t stride; std::int32_t pixel_format; const void* data; std::size_t data_size; };

enum MfAiStatus : std::int32_t { MF_AI_OK = 0, MF_AI_NOT_IMPLEMENTED = 1, MF_AI_INVALID_ARGUMENT = 2, MF_AI_SDK_MISMATCH = 3, MF_AI_INVALID_HANDLE = 4, MF_AI_CANCELLED = 5, MF_AI_INTERNAL_ERROR = 6 };
enum MfAiCapability : std::uint64_t { MF_AI_CAP_CANCELLATION = 1ull << 0, MF_AI_CAP_LOGGING = 1ull << 1, MF_AI_CAP_PROGRESS = 1ull << 2, MF_AI_CAP_STUB_PROVIDER = 1ull << 3 };

using MfAiLogger = void(MF_AI_CALL*)(std::int32_t level, const char* message, void* context);
using MfAiProgress = void(MF_AI_CALL*)(double fraction, const char* stage, void* context);

MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_GetVersion(MfAiVersion* version);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_Initialize(const MfAiVersion* requested, MfAiVersion* negotiated);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_Shutdown();
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_GetCapabilities(MfAiCapabilities* capabilities);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_CreateProvider(void** provider);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_DestroyProvider(void* provider);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_CancelOperation(void* provider);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_ReleaseResources(void* provider);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_GetLastError(void* provider, char* buffer, std::size_t* buffer_size);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_RegisterLogger(void* provider, MfAiLogger callback, void* context);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_RegisterProgressCallback(void* provider, MfAiProgress callback, void* context);
MF_AI_EXPORT MfAiStatus MF_AI_CALL MfAi_ProcessImage(void* provider, const MfAiImage* input, MfAiImage* output);

#pragma once
#include <windows.h>

#ifdef RBLX_EXPORT
#define RBLX_API __declspec(dllexport)
#else
#define RBLX_API __declspec(dllimport)
#endif

extern "C" {
    // Public-open API surface only. No memory/process/runtime primitives.
    RBLX_API bool __stdcall Initialize();
    RBLX_API void __stdcall Shutdown();
    RBLX_API bool __stdcall IsRuntimeAvailable();
    RBLX_API int __stdcall GetLastErrorText(char* buffer, int bufLen);
}

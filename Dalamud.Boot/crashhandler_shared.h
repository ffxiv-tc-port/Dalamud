#pragma once

#include <cinttypes>
#include <string_view>

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#define CUSTOM_EXCEPTION_EXTERNAL_EVENT 0x12345679

// Raised from the ReportEventW hook, CUSTOM_EXCEPTION_EXTERNAL_EVENT carries ExceptionInformation[0] = the CLR's
// event log ID; every other raiser passes no parameters.
constexpr DWORD ClrEventIdUnmanagedFailFast = 1023; // "terminated due to an internal error in the .NET Runtime"
constexpr uint32_t ClrExitCodeExecutionEngine = 0x80131506; // COR_E_EXECUTIONENGINE, e.g. a corrupted GC heap

inline DWORD get_external_event_clr_event_id(const EXCEPTION_RECORD& record)
{
    if (record.ExceptionCode != CUSTOM_EXCEPTION_EXTERNAL_EVENT
        || record.NumberParameters < 1
        || record.NumberParameters > EXCEPTION_MAXIMUM_PARAMETERS)
        return 0;
    return static_cast<DWORD>(record.ExceptionInformation[0]);
}

// Exit code from the event 1023 text ("... with exit code 0x80131506."); 0 when absent or malformed.
inline uint32_t parse_clr_fatal_exit_code(const std::wstring_view text)
{
    constexpr std::wstring_view marker = L"with exit code 0x";
    const auto pos = text.rfind(marker);
    if (pos == std::wstring_view::npos)
        return 0;

    uint32_t value = 0;
    size_t digits = 0;
    for (auto i = pos + marker.size(); i < text.size(); ++i)
    {
        const auto c = text[i];
        uint32_t nibble;
        if (c >= L'0' && c <= L'9')
            nibble = c - L'0';
        else if (c >= L'a' && c <= L'f')
            nibble = c - L'a' + 10;
        else if (c >= L'A' && c <= L'F')
            nibble = c - L'A' + 10;
        else
            break;

        if (++digits > 8)
            return 0;
        value = (value << 4) | nibble;
    }
    return value;
}

// Only a CLR internal error gets the large dump that includes the GC heap.
inline bool wants_gc_heap_dump(const EXCEPTION_RECORD& record, const std::wstring_view eventText)
{
    return get_external_event_clr_event_id(record) == ClrEventIdUnmanagedFailFast
        && parse_clr_fatal_exit_code(eventText) == ClrExitCodeExecutionEngine;
}

struct exception_info
{
    LPEXCEPTION_POINTERS pExceptionPointers;
    EXCEPTION_POINTERS ExceptionPointers;
    EXCEPTION_RECORD ExceptionRecord;
    CONTEXT ContextRecord;
    uint64_t nLifetime;
    HANDLE hThreadHandle;
    HANDLE hEventHandle;
    DWORD dwStackTraceLength;
    DWORD dwTroubleshootingPackDataLength;
};

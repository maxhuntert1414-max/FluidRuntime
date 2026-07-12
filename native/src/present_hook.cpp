#include <windows.h>
#include <dxgi.h>

#include <atomic>
#include <cstdint>
#include <mutex>

namespace {

using PresentFunction = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);

constexpr size_t kPresentVtableIndex = 8;
std::mutex g_hook_mutex;
std::atomic<std::uint64_t> g_present_count{0};
std::atomic<unsigned long> g_active_present_calls{0};
std::atomic<PresentFunction> g_original_present{nullptr};
void** g_present_slot{};

class ActivePresentCall {
public:
    ActivePresentCall() {
        g_active_present_calls.fetch_add(1, std::memory_order_acquire);
    }
    ~ActivePresentCall() {
        g_active_present_calls.fetch_sub(1, std::memory_order_release);
    }

    ActivePresentCall(const ActivePresentCall&) = delete;
    ActivePresentCall& operator=(const ActivePresentCall&) = delete;
};

bool write_pointer(void** slot, void* value, void* rollback_value) {
    DWORD old_protection{};
    if (!VirtualProtect(
            slot,
            sizeof(void*),
            PAGE_EXECUTE_READWRITE,
            &old_protection)) {
        return false;
    }

    *slot = value;
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));

    DWORD ignored{};
    if (VirtualProtect(slot, sizeof(void*), old_protection, &ignored) != FALSE) {
        return true;
    }

    const auto restore_error = GetLastError();
    *slot = rollback_value;
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    VirtualProtect(slot, sizeof(void*), old_protection, &ignored);
    SetLastError(restore_error);
    return false;
}

HRESULT STDMETHODCALLTYPE hooked_present(
    IDXGISwapChain* swap_chain,
    UINT sync_interval,
    UINT flags) {
    const ActivePresentCall active_call;
    g_present_count.fetch_add(1, std::memory_order_relaxed);
    const auto original = g_original_present.load(std::memory_order_acquire);
    if (original == nullptr) {
        return E_UNEXPECTED;
    }
    return original(swap_chain, sync_interval, flags);
}

} // namespace

extern "C" __declspec(dllexport) HRESULT WINAPI FluidHookAttach(
    IDXGISwapChain* swap_chain) {
    if (swap_chain == nullptr) {
        return E_POINTER;
    }

    const std::lock_guard lock(g_hook_mutex);
    if (g_present_slot != nullptr) {
        return HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS);
    }

    auto*** object = reinterpret_cast<void***>(swap_chain);
    auto** present_slot = &(*object)[kPresentVtableIndex];
    const auto original = reinterpret_cast<PresentFunction>(*present_slot);
    if (original == nullptr || original == hooked_present) {
        return E_UNEXPECTED;
    }

    g_original_present.store(original, std::memory_order_release);
    g_present_slot = present_slot;
    g_present_count.store(0, std::memory_order_relaxed);
    if (!write_pointer(
            present_slot,
            reinterpret_cast<void*>(hooked_present),
            reinterpret_cast<void*>(original))) {
        g_original_present.store(nullptr, std::memory_order_release);
        g_present_slot = nullptr;
        return HRESULT_FROM_WIN32(GetLastError());
    }

    return *present_slot == reinterpret_cast<void*>(hooked_present)
        ? S_OK
        : E_FAIL;
}

extern "C" __declspec(dllexport) HRESULT WINAPI FluidHookDetach() {
    const std::lock_guard lock(g_hook_mutex);
    const auto original = g_original_present.load(std::memory_order_acquire);
    if (g_present_slot == nullptr || original == nullptr) {
        return S_FALSE;
    }

    if (*g_present_slot != reinterpret_cast<void*>(hooked_present) &&
        *g_present_slot != reinterpret_cast<void*>(original)) {
        return E_UNEXPECTED;
    }

    auto** slot = g_present_slot;
    if (*slot == reinterpret_cast<void*>(hooked_present) &&
        !write_pointer(
            slot,
            reinterpret_cast<void*>(original),
            reinterpret_cast<void*>(hooked_present))) {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    const auto restored = *slot == reinterpret_cast<void*>(original);
    constexpr unsigned long detach_wait_limit_ms = 5000;
    unsigned long waited_ms = 0;
    while (restored &&
           g_active_present_calls.load(std::memory_order_acquire) != 0 &&
           waited_ms < detach_wait_limit_ms) {
        Sleep(1);
        ++waited_ms;
    }
    if (g_active_present_calls.load(std::memory_order_acquire) != 0) {
        return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    }

    g_present_slot = nullptr;
    g_original_present.store(nullptr, std::memory_order_release);
    return restored ? S_OK : E_FAIL;
}

extern "C" __declspec(dllexport) std::uint64_t WINAPI FluidHookPresentCount() {
    return g_present_count.load(std::memory_order_relaxed);
}

extern "C" __declspec(dllexport) BOOL WINAPI FluidHookIsAttached() {
    const std::lock_guard lock(g_hook_mutex);
    return g_present_slot != nullptr ? TRUE : FALSE;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) {
    return TRUE;
}

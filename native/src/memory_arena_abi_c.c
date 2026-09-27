#include "fluid_memory_arena.h"
#include <stddef.h>

_Static_assert(sizeof(fma_options) == 24, "options ABI");
_Static_assert(sizeof(fma_span) == 24, "span ABI");
_Static_assert(sizeof(fma_peer) == 56, "peer ABI");
_Static_assert(sizeof(fma_reader_hello) == 16, "hello ABI");
_Static_assert(sizeof(fma_shared_read) == 64, "descriptor ABI");
_Static_assert(offsetof(fma_shared_read, mapping_handle) == 40, "handle ABI");
_Static_assert(sizeof(fma_metrics) == 128, "metrics ABI");

/* Compiled as C and linked across the real DLL, not only checked by C++. */
int fluid_memory_c_smoke(void) {
    fma_metrics metrics = {0};
    return fma_get_metrics(0, &metrics, sizeof(metrics)) == FMA_INVALID_HANDLE;
}

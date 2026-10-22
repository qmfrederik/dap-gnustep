#include <stddef.h>

__declspec( dllexport ) char* objc_dap_get_description(void* obj);
__declspec( dllexport ) char* objc_dap_get_variable(void* obj);
__declspec( dllexport ) char* objc_dap_get_array_elements(void* obj, size_t start, size_t count);

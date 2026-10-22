#pragma once
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    const char* name;
    const char* type;
    const char* value;
    int64_t variables_reference; // 0 when the variable has no children
    int64_t indexed_variables;   // number of indexed children; 0 when unknown
} objc_dap_variable;

// Returns a malloc'd JSON DAP 'variables' response body, or NULL; the caller must free() it.
char* objc_dap_make_variables_response(const objc_dap_variable* variables, size_t count);

#ifdef __cplusplus
}
#endif

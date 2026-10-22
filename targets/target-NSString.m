#include <Foundation/Foundation.h>
#include <objc/runtime.h>
#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <unistd.h>
#endif
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "objc_dap.h"
#include "objc_dap_variables.h"

int main()
{
    NSString* str = @"Hello, World!";
    NSDictionary* dict = @{@"key": @"value"};
    NSArray* array = @[@"item1", @"item2"];

    NSLog(@"%@", str);
    char* str_description = objc_dap_get_description(str);
    char* dict_description = objc_dap_get_description(dict);
    char* array_description = objc_dap_get_description(array);
    char* str_variable = objc_dap_get_variable(str);
    printf("%s\n", str_variable ? str_variable : "(null)");
    free(str_variable);
    char* array_variable = objc_dap_get_variable(array);
    printf("%s\n", array_variable ? array_variable : "(null)");
    free(array_variable);
    char* array_elements = objc_dap_get_array_elements(array, 0, 0);
    printf("%s\n", array_elements ? array_elements : "(null)");
    free(array_elements);
    free(array_description);
    free(dict_description);
    free(str_description);
    return 0;
}

#ifdef _WIN32
static BOOL is_readable(const void* p)
{
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(p, &mbi, sizeof mbi))
        return NO;
    return mbi.State == MEM_COMMIT && !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD));
}
#else
// write() from p into a pipe fails with EFAULT instead of faulting if p is unreadable.
static BOOL is_readable(const void* p)
{
    static int fds[2] = { -1, -1 };
    if (fds[0] < 0) {
        if (pipe(fds) != 0) {
            fds[0] = fds[1] = -1;
            return NO;
        }
        fcntl(fds[0], F_SETFL, O_NONBLOCK);
        fcntl(fds[1], F_SETFL, O_NONBLOCK);
    }
    char c;
    if (write(fds[1], p, 1) != 1)
        return NO;
    return read(fds[0], &c, 1) == 1;
}
#endif

static BOOL is_registered_class(Class cls)
{
    int n = objc_getClassList(NULL, 0);
    if (n <= 0)
        return NO;
    Class* list = malloc(n * sizeof *list);
    if (!list)
        return NO;
    n = objc_getClassList(list, n);
    BOOL found = NO;
    for (int i = 0; i < n && !found; i++)
        found = (list[i] == cls);
    free(list);
    return found;
}

// Heuristic: small objects are tagged pointers; otherwise isa must be a registered class.
static BOOL is_objc_object(void* obj)
{
    if (!obj)
        return NO;
    if ((uintptr_t)obj & OBJC_SMALL_OBJECT_MASK)
        return YES;
    if (!is_readable(obj) || !is_readable((char*)obj + sizeof(void*) - 1))
        return NO;
    Class cls = object_getClass((id)obj);
    return cls && is_readable(cls) && is_registered_class(cls);
}

// Returns a malloc'd UTF-8 copy of -description, or NULL; the caller must free() it.
char* objc_dap_get_description(void* obj)
{
    if (!is_objc_object(obj))
        return NULL;
    char* result = NULL;
    @autoreleasepool {
        const char* utf8 = [[(id)obj description] UTF8String];
        if (utf8) {
            size_t len = strlen(utf8) + 1;
            result = malloc(len);
            if (result)
                memcpy(result, utf8, len);
        }
    }
    return result;
}

typedef struct {
    char name[32];
    char summary[32];
} element_text;

// Fills v from obj. v->value may point into an autoreleased string, so use v within the caller's pool.
static void describe_object(objc_dap_variable* v, element_text* text, id obj)
{
    v->type = class_getName(object_getClass(obj));
    if ([obj isKindOfClass:[NSArray class]]) {
        NSUInteger count = [(NSArray*)obj count];
        snprintf(text->summary, sizeof text->summary, "%llu items", (unsigned long long)count);
        v->value = text->summary;
        if (count) {
            v->variables_reference = (int64_t)(uintptr_t)obj;
            v->indexed_variables = (int64_t)count;
        }
    } else {
        const char* utf8 = [[obj description] UTF8String];
        v->value = utf8 ? utf8 : "";
    }
}

// Returns a malloc'd JSON DAP 'variables' response body describing obj, or NULL; the caller must free() it.
// An array is reported with variablesReference set to its address; pass that to objc_dap_get_array_elements().
char* objc_dap_get_variable(void* obj)
{
    if (!is_objc_object(obj))
        return NULL;
    char* result = NULL;
    @autoreleasepool {
        element_text text;
        objc_dap_variable variable = {0};
        snprintf(text.name, sizeof text.name, "0x%llx", (unsigned long long)(uintptr_t)obj);
        variable.name = text.name;
        describe_object(&variable, &text, (id)obj);
        result = objc_dap_make_variables_response(&variable, 1);
    }
    return result;
}

// Returns a malloc'd JSON DAP 'variables' response body listing elements [start, start+count) of an NSArray
// (count 0 means all), or NULL if obj is not an array; the caller must free() it.
char* objc_dap_get_array_elements(void* obj, size_t start, size_t count)
{
    if (!is_objc_object(obj) || ![(id)obj isKindOfClass:[NSArray class]])
        return NULL;
    char* result = NULL;
    @autoreleasepool {
        NSArray* array = (NSArray*)obj;
        size_t total = [array count];
        if (start > total)
            start = total;
        size_t n = total - start;
        if (count && count < n)
            n = count;
        objc_dap_variable* variables = calloc(n ? n : 1, sizeof *variables);
        element_text* texts = calloc(n ? n : 1, sizeof *texts);
        if (variables && texts) {
            for (size_t i = 0; i < n; i++) {
                snprintf(texts[i].name, sizeof texts[i].name, "[%llu]", (unsigned long long)(start + i));
                variables[i].name = texts[i].name;
                describe_object(&variables[i], &texts[i], [array objectAtIndex:start + i]);
            }
            result = objc_dap_make_variables_response(variables, n);
        }
        free(texts);
        free(variables);
    }
    return result;
}
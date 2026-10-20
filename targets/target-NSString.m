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

int main()
{
    NSString* str = @"Hello, World!";
    NSDictionary* dict = @{@"key": @"value"};
    NSArray* array = @[@"item1", @"item2"];

    NSLog(@"%@", str);
    char* str_description = objc_dap_get_description(str);
    char* dict_description = objc_dap_get_description(dict);
    char* array_description = objc_dap_get_description(array);
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
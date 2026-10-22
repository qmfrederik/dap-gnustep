cmake_minimum_required(VERSION 3.10)

project(Tutorial)

# The Conan toolchain only sets the DLL CRT for Release; the Release-built deps need it for RelWithDebInfo too.
set(CMAKE_MSVC_RUNTIME_LIBRARY "$<$<CONFIG:Release,RelWithDebInfo>:MultiThreadedDLL>")

find_package(libobjc2)
find_package(gnustep-base)
find_package(cppdap)
find_package(nlohmann_json)

add_executable(target-NSString target-NSString.m objc_dap_variables.cpp)
target_link_libraries(target-NSString gnustep-base::gnustep-base libobjc2::libobjc2 cppdap::cppdap nlohmann_json::nlohmann_json)

if(WIN32)
    add_custom_command(TARGET target-NSString POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${gnustep-base_BIN_DIRS_RELEASE}/gnustep-base-1_31.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${libffi_BIN_DIRS_RELEASE}/libffi.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${libxslt_BIN_DIRS_RELEASE}/libxslt.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${libxml2_BIN_DIRS_RELEASE}/libxml2.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${libiconv_BIN_DIRS_RELEASE}/iconv-2.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${libcurl_BIN_DIRS_RELEASE}/libcurl.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${openssl_BIN_DIRS_RELEASE}/libcrypto-3-x64.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${openssl_BIN_DIRS_RELEASE}/libssl-3-x64.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${zlib_BIN_DIRS_RELEASE}/zlib1.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${libobjc2_BIN_DIRS_RELEASE}/objc.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${libdispatch_BIN_DIRS_RELEASE}/dispatch.dll" "${CMAKE_CURRENT_BINARY_DIR}/"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${libdispatch_BIN_DIRS_RELEASE}/blocksRuntime.dll" "${CMAKE_CURRENT_BINARY_DIR}/")
endif(WIN32)
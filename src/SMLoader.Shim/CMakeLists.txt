cmake_minimum_required(VERSION 3.20)
project(SMLoaderShim LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(SMLoader.Shim SHARED
    dllmain.cpp
    iat_hook.cpp
    clr_host.cpp
    log.cpp
)

set_target_properties(SMLoader.Shim PROPERTIES
    OUTPUT_NAME "SMLoader.Shim"
    PREFIX ""
)

target_compile_definitions(SMLoader.Shim PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(SMLoader.Shim PRIVATE psapi)

if (MSVC)
    target_compile_options(SMLoader.Shim PRIVATE /W4 /permissive-)
endif()

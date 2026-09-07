cmake_minimum_required(VERSION 3.20)
project(SMLoaderShim LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(SMLoader.Shim SHARED
    dllmain.cpp
    iat_hook.cpp
    clr_host.cpp
    log.cpp
    native_plugins.cpp
)

set_target_properties(SMLoader.Shim PROPERTIES
    OUTPUT_NAME "SMLoader.Shim"
    PREFIX ""
)

target_compile_definitions(SMLoader.Shim PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(SMLoader.Shim PRIVATE psapi)

if (MSVC)
    # This DLL is injected into someone else's process, so it gets the
    # mitigations a normally-loaded binary would have had applied for it.
    #
    # /WX is deliberately absent: a compiler upgrade that adds one warning
    # would otherwise stop a player being able to build the loader at all.
    #
    # /Qspectre is absent for the same reason - it needs the Spectre-mitigated
    # runtime libraries, which are a separate VS Installer component, and
    # MSB8040 without them is a hard build failure. Add it back with that
    # component installed if the threat model ever warrants it.
    target_compile_options(SMLoader.Shim PRIVATE
        /W4 /permissive- /GS /guard:cf /sdl
        $<$<CONFIG:Release>:/O2 /Oi /Zi>)

    target_link_options(SMLoader.Shim PRIVATE
        /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf
        # /Zi above only emits debug info; /DEBUG is what writes the PDB, and a
        # PDB is the difference between a user's crash dump being actionable and
        # being a list of hex addresses. /OPT:REF /OPT:ICF undo the size cost
        # /DEBUG would otherwise bring back.
        $<$<CONFIG:Release>:/DEBUG /OPT:REF /OPT:ICF>)

    # Static CRT. Without it the VC++ 2015-2022 redistributable is a silent
    # deployment prerequisite: LoadLibraryW returns 0 in the target process and
    # the launcher can only report "the target process refused to load ...",
    # with nothing anywhere saying why.
    set_property(TARGET SMLoader.Shim PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

# owl.exe: OWL for Windows, with an ESP32 running owl-radio (firmware/) as its
# radio and a Wintun adapter as awdl0. Upstream's protocol code (src/), core.c
# and owl.c build unchanged; io.c, netutils.c and libev are replaced by the
# files here, and compat/ supplies the POSIX headers they include.
#
#   tools/build-owl.ps1 fetches OWL at the pinned commit with patches/ and
#   Wintun, then builds this with MinGW (GCC).
cmake_minimum_required(VERSION 3.16)
project(owl-windows C)

set(OWL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../upstream/owl" CACHE PATH "OWL source, patched")
set(WINTUN_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../upstream/wintun" CACHE PATH "Wintun SDK (include/, bin/)")
set(PROTO_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../firmware/main")

if (NOT EXISTS "${OWL_DIR}/src/state.c")
    message(FATAL_ERROR "no OWL source at ${OWL_DIR}; run tools/build-owl.ps1")
endif ()

file(GLOB OWL_SRC "${OWL_DIR}/src/*.c")

add_executable(owl
        ${OWL_SRC}
        "${OWL_DIR}/radiotap/radiotap.c"
        "${OWL_DIR}/daemon/core.c"
        "${OWL_DIR}/daemon/owl.c"
        io_esp.c
        netutils_win.c
        ev_win.c
        compat.c)

target_include_directories(owl PRIVATE
        compat
        "${CMAKE_CURRENT_SOURCE_DIR}"
        "${OWL_DIR}/src"
        "${OWL_DIR}/radiotap"
        "${WINTUN_DIR}/include"
        "${PROTO_DIR}")

target_compile_definitions(owl PRIVATE _DEFAULT_SOURCE RADIOTAP_SUPPORT_OVERRIDES _WIN32_WINNT=0x0A00)
target_compile_options(owl PRIVATE
        -include "${CMAKE_CURRENT_SOURCE_DIR}/compat/owl_win.h"
        # daemon/io.h would shadow the CRT's <io.h>, which <unistd.h> includes.
        "-iquote${OWL_DIR}/daemon"
        # GCC's packed-struct layout, which OWL's frame structs are written for;
        # MinGW otherwise lays bitfields out as MSVC does.
        -mno-ms-bitfields
        -Wall -Wextra -Wno-unused-parameter -Wno-format
        # radiotap.c casts pointers to unsigned long for alignment arithmetic,
        # which is 32 bits on Windows; the low bits are all it needs.
        -Wno-pointer-to-int-cast -Wno-address-of-packed-member -Wno-unknown-pragmas)
target_link_libraries(owl PRIVATE ws2_32 iphlpapi winmm -static)

# wintun.dll goes beside owl.exe.
if (CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|aarch64")
    set(WINTUN_ARCH arm64)
else ()
    set(WINTUN_ARCH amd64)
endif ()
add_custom_command(TARGET owl POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${WINTUN_DIR}/bin/${WINTUN_ARCH}/wintun.dll" "$<TARGET_FILE_DIR:owl>")

cmake_minimum_required(VERSION 3.20)
# Windows: every native part. Linux (gcc or clang): the Vulkan recorder layer, libVkLayer_scskiller.so, its selftest, and the
# Vulkan warm, scskiller_warm and fossilize-replay.
if(WIN32)
  project(SCSKiller LANGUAGES CXX ASM_MASM RC)
else()
  project(SCSKiller LANGUAGES C CXX)
endif()
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")  # static CRT: nothing extra to ship next to the game
# __FILE__ (Fossilize's logging, CHECK) must not put the build machine's folders into the binaries
if(MSVC)
  add_compile_options("$<$<COMPILE_LANGUAGE:C,CXX>:/d1trimfile:${CMAKE_CURRENT_BINARY_DIR}/;/d1trimfile:${CMAKE_CURRENT_SOURCE_DIR}/>")
else()
  add_compile_options(-ffile-prefix-map=${CMAKE_CURRENT_BINARY_DIR}/= -ffile-prefix-map=${CMAKE_CURRENT_SOURCE_DIR}/=)
endif()

# Version resource (version.rc) of d3d12.dll and scskiller_warm.exe; build/publish.ps1 passes the tag's version
set(SCSK_VERSION "0.0.0-internal.0" CACHE STRING "SemVer X.Y.Z[-pre] in the native version resource")
if(NOT SCSK_VERSION MATCHES "^([0-9]+)\\.([0-9]+)\\.([0-9]+)(-[0-9A-Za-z.-]+)?$")
  message(FATAL_ERROR "SCSK_VERSION is not X.Y.Z[-pre]: ${SCSK_VERSION}")
endif()
set(SCSK_VERSION_MAJOR ${CMAKE_MATCH_1})
set(SCSK_VERSION_MINOR ${CMAKE_MATCH_2})
set(SCSK_VERSION_PATCH ${CMAKE_MATCH_3})
configure_file(version.h.in version.h @ONLY)

if(WIN32)

# d3d9.dll: the DirectX 9 recorder, x64 and x86 (most DirectX 9 games are 32-bit). A Win32 build (-A Win32) makes only it.
if(CMAKE_SIZEOF_VOID_P EQUAL 4)
  add_library(scskiller_d3d9 SHARED d3d9rec.cpp vtslots9.cpp d3d9.def version.rc)
else()
  add_library(scskiller_d3d9 SHARED d3d9rec.cpp vtslots9.cpp d3d9stubs.asm d3d9.def version.rc)
endif()
set_target_properties(scskiller_d3d9 PROPERTIES OUTPUT_NAME d3d9)
target_compile_definitions(scskiller_d3d9 PRIVATE SCSK_RC_D3D9)
target_include_directories(scskiller_d3d9 PRIVATE ${CMAKE_CURRENT_BINARY_DIR})
target_link_options(scskiller_d3d9 PRIVATE /MAP /DELAYLOAD:shell32.dll /DELAYLOAD:ole32.dll)  # only admission at the first device uses them
target_link_libraries(scskiller_d3d9 delayimp)
if(CMAKE_SIZEOF_VOID_P EQUAL 4)
  return()
endif()

add_library(scskiller SHARED proxy.cpp warm11.cpp vtslots.cpp stubs.asm d3d12.def version.rc)
set_target_properties(scskiller PROPERTIES OUTPUT_NAME d3d12)
# /MAP: function RVAs for reading crash stacks. Delay-load what only the D3D11 warm uses (in a game it is never loaded) and
# what only admission at the first device uses (the recorder folder).
target_link_options(scskiller PRIVATE /MAP /DELAYLOAD:d3d11.dll /DELAYLOAD:d3dcompiler_47.dll /DELAYLOAD:dxgi.dll /DELAYLOAD:shell32.dll /DELAYLOAD:ole32.dll)
target_link_libraries(scskiller delayimp)
# Fault injection for the tests: never in a tagged release, whose version publish.ps1 sets
if(SCSK_VERSION MATCHES "^0[.]0[.]0-internal")
  target_compile_definitions(scskiller PRIVATE SCSK_TEST_HOOKS)
endif()

# scskiller_ags.dll: the same recorder in front of AMD's AGS, installed as the game's amd_ags_x64.dll where AGS makes the
# device (the app's GameFiles.AgsMakesTheDevice). The game's own AGS is renamed amd_ags_x64_scskiller.dll: Wrap_AgsCreateDevice is
# ours, every other export in ags_exports.txt goes to it through an import library made from the same list.
file(STRINGS ags_exports.txt AGS_NAMES)
set(AGS_DEF ${CMAKE_CURRENT_BINARY_DIR}/ags_wrap.def)
set(AGS_REAL_DEF ${CMAKE_CURRENT_BINARY_DIR}/amd_ags_real.def)
set(AGS_REAL_LIB ${CMAKE_CURRENT_BINARY_DIR}/amd_ags_real.lib)
set(AGS_DEF_TEXT "EXPORTS
    agsDriverExtensionsDX12_CreateDevice = Wrap_AgsCreateDevice
    SCSKiller_StartWarm
")
set(AGS_REAL_DEF_TEXT "LIBRARY amd_ags_x64_scskiller.dll
EXPORTS
")
foreach(n ${AGS_NAMES})
  string(APPEND AGS_REAL_DEF_TEXT "    ${n}
")
  if(NOT n STREQUAL "agsDriverExtensionsDX12_CreateDevice")
    string(APPEND AGS_DEF_TEXT "    ${n}
")
  endif()
endforeach()
file(WRITE ${AGS_DEF} "${AGS_DEF_TEXT}")
file(WRITE ${AGS_REAL_DEF} "${AGS_REAL_DEF_TEXT}")
add_custom_command(OUTPUT ${AGS_REAL_LIB} COMMAND ${CMAKE_AR} /nologo /DEF:${AGS_REAL_DEF} /MACHINE:X64 /OUT:${AGS_REAL_LIB}
                   DEPENDS ${AGS_REAL_DEF} ags_exports.txt VERBATIM)
add_custom_target(amd_ags_real_lib DEPENDS ${AGS_REAL_LIB})
add_library(scskiller_ags SHARED proxy.cpp warm11.cpp vtslots.cpp stubs.asm ${AGS_DEF} version.rc)
add_dependencies(scskiller_ags amd_ags_real_lib)
target_compile_definitions(scskiller_ags PRIVATE SCSK_AGS_WRAP)
target_include_directories(scskiller_ags PRIVATE ${CMAKE_CURRENT_BINARY_DIR})
target_link_options(scskiller_ags PRIVATE /MAP /DELAYLOAD:d3d11.dll /DELAYLOAD:d3dcompiler_47.dll /DELAYLOAD:dxgi.dll /DELAYLOAD:shell32.dll /DELAYLOAD:ole32.dll)
target_link_libraries(scskiller_ags ${AGS_REAL_LIB} delayimp)

# The Streamline preloader (preload.cpp) under each name it goes in as, in Release\preload\: never beside an exe of ours.
# No CRT: its DllMain runs under the loader lock at the game's start and does nothing else.
foreach(host winmm version dinput8)
  add_library(preload_${host} SHARED preload.cpp preload.asm preload_${host}.def version.rc)
  target_compile_definitions(preload_${host} PRIVATE SCSK_RC_PRELOAD)
  target_include_directories(preload_${host} PRIVATE ${CMAKE_CURRENT_BINARY_DIR})
  target_compile_options(preload_${host} PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/GS->)
  target_link_options(preload_${host} PRIVATE /NODEFAULTLIB /ENTRY:DllMain)
  target_link_libraries(preload_${host} kernel32)
  set_target_properties(preload_${host} PROPERTIES OUTPUT_NAME ${host}
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_CURRENT_BINARY_DIR}/Release/preload RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_CURRENT_BINARY_DIR}/Debug/preload
    ARCHIVE_OUTPUT_DIRECTORY_RELEASE ${CMAKE_CURRENT_BINARY_DIR}/Release/preload ARCHIVE_OUTPUT_DIRECTORY_DEBUG ${CMAKE_CURRENT_BINARY_DIR}/Debug/preload)
endforeach()

add_executable(selftest selftest.cpp)
target_link_libraries(selftest d3dcompiler)
target_include_directories(selftest PRIVATE third_party/vulkan)  # headers only; vulkan-1.dll is loaded at run time
add_dependencies(selftest scskiller fakenext vkprobe_proxy)

# A mod's d3d12.dll for `selftest chain` (scskiller.ini next=); test only, never published
add_library(fakenext SHARED fakenext.cpp fakenext.asm)

# `selftest vkproxy`'s vulkan-1.dll: a jump per export of vk_exports.txt (System32's loader) through vk_real; test only,
# never published. Not named vulkan-1.dll here: a tool beside it that loads the loader by name would get it.
file(STRINGS vk_exports.txt VK_NAMES)
set(VK_ASM "PUBLIC vk_real\n.data\nvk_real QWORD ")
set(VK_DEF "LIBRARY vulkan-1\nEXPORTS\n")
set(VK_H "#pragma once\nstatic const char* const kVkNames[] = {\n")
list(LENGTH VK_NAMES VK_COUNT)
string(APPEND VK_ASM "${VK_COUNT} DUP(0)\n.code\n")
set(i 0)
foreach(n ${VK_NAMES})
  math(EXPR off "${i} * 8")
  string(APPEND VK_ASM "s_${n} PROC\n    jmp QWORD PTR [vk_real + ${off}]\ns_${n} ENDP\n")
  string(APPEND VK_DEF "    ${n} = s_${n}\n")
  string(APPEND VK_H "    \"${n}\",\n")
  math(EXPR i "${i} + 1")
endforeach()
string(APPEND VK_ASM "END\n")
string(APPEND VK_H "};\n")
file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/vk_stubs.asm "${VK_ASM}")
file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/vk_proxy.def "${VK_DEF}")
file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/vk_names.h "${VK_H}")
add_library(vkprobe_proxy SHARED vkprobe_proxy.cpp ${CMAKE_CURRENT_BINARY_DIR}/vk_stubs.asm ${CMAKE_CURRENT_BINARY_DIR}/vk_proxy.def)
target_include_directories(vkprobe_proxy PRIVATE ${CMAKE_CURRENT_BINARY_DIR})

add_executable(scskiller_warm warm.cpp version.rc)
# The same exe on the segment heap (warm.manifest), in segheap\ under the same file name: the warmer runs it on NVIDIA
add_executable(scskiller_warm_segheap warm.cpp version.rc warm.manifest)
set_target_properties(scskiller_warm_segheap PROPERTIES OUTPUT_NAME scskiller_warm
  RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_CURRENT_BINARY_DIR}/Release/segheap RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_CURRENT_BINARY_DIR}/Debug/segheap
  # both exes export the warm-host marker, so each writes a scskiller_warm.exp/.lib; keep them apart for parallel builds
  ARCHIVE_OUTPUT_DIRECTORY_RELEASE ${CMAKE_CURRENT_BINARY_DIR}/Release/segheap ARCHIVE_OUTPUT_DIRECTORY_DEBUG ${CMAKE_CURRENT_BINARY_DIR}/Debug/segheap)
target_include_directories(scskiller PRIVATE ${CMAKE_CURRENT_BINARY_DIR})
foreach(t scskiller_warm scskiller_warm_segheap)
  target_compile_definitions(${t} PRIVATE SCSK_RC_WARM)
  target_include_directories(${t} PRIVATE ${CMAKE_CURRENT_BINARY_DIR})
  add_dependencies(${t} scskiller)
endforeach()

# AMD GPU Services (GPUOpen AGS SDK 6.3.1, MIT): scskiller_warm loads it at run time (--ags). AMD's signed release DLL,
# pinned by hash; without it (offline) warms of AGS games fall back to a plain device.
set(AGS_DLL ${CMAKE_CURRENT_BINARY_DIR}/amd_ags_x64.dll)
set(AGS_SHA256 482B241EA855FDD59AAA7687C385CE4165B5340857E6A0E6D15BA27ECB65EE70)
if(EXISTS ${AGS_DLL})
  file(SHA256 ${AGS_DLL} AGS_HAVE)
  string(TOUPPER "${AGS_HAVE}" AGS_HAVE)
endif()
if(NOT AGS_HAVE STREQUAL AGS_SHA256)
  # no EXPECTED_HASH: with it a failed download is a configure error whatever STATUS says; the hash is checked below
  file(DOWNLOAD https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/AGS_SDK/v6.3.1/ags_lib/lib/amd_ags_x64.dll ${AGS_DLL}.part
       TLS_VERIFY ON STATUS AGS_STATUS)
  list(GET AGS_STATUS 0 AGS_CODE)
  if(AGS_CODE EQUAL 0)
    file(SHA256 ${AGS_DLL}.part AGS_GOT)
    string(TOUPPER "${AGS_GOT}" AGS_GOT)
    if(NOT AGS_GOT STREQUAL AGS_SHA256)
      set(AGS_STATUS "SHA-256 ${AGS_GOT}, not ${AGS_SHA256}")
    endif()
  endif()
  if(AGS_CODE EQUAL 0 AND AGS_GOT STREQUAL AGS_SHA256)
    file(RENAME ${AGS_DLL}.part ${AGS_DLL})
  else()
    file(REMOVE ${AGS_DLL}.part ${AGS_DLL})
    message(WARNING "amd_ags_x64.dll not downloaded: ${AGS_STATUS}")
  endif()
endif()
if(EXISTS ${AGS_DLL})
  add_custom_command(TARGET scskiller_warm POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different ${AGS_DLL} $<TARGET_FILE_DIR:scskiller_warm>)
endif()

endif()

# fossilize-replay.exe, the Vulkan warm's replayer (scskiller_warm --vulkan): Fossilize (MIT) at a pinned commit with the
# submodules the replayer needs, each source archive checked against its SHA-256 (a mismatch stops the configure), then
# third_party/fossilize/scskiller.patch (the warm's stop, the lists of created pipelines and the game's own device).
set(FZ_COMMIT 556da9ff7d7616704dda515756219ab1dab693ec)
set(FZ_DIR ${CMAKE_CURRENT_BINARY_DIR}/fossilize-${FZ_COMMIT})
set(FZ_SOURCES  # repo|commit|SHA-256 of codeload.github.com's tar.gz|folder in the Fossilize tree (its .gitmodules)
  "ValveSoftware/Fossilize|${FZ_COMMIT}|2802294470007bd71bc36b169bebde25d63008c4ad65a84d288741b33d7d27e1|."
  "Tencent/rapidjson|8f4c021fa2f1e001d2376095928fc0532adf2ae6|3c8e1044b9b3226ff839663ff482b00750ca7dedb53026f8d8a31999865b8466|rapidjson"
  "zeux/volk|8a2387afabdf1336f132e0a8bf38701df9838130|7fd27002911b3bcd2d93b395fd17d913c7a29a119a7da1173ee8b6bbac238915|cli/volk"
  "KhronosGroup/SPIRV-Headers|496543121ce6419f23d6fa5d7194ba66c36212d2|a9bb9c48713245eacf97cc539b6f1d45405a92d8813f8b82e635f0a085ee9898|cli/SPIRV-Headers"
  "tronkko/dirent|c885633e126a3a949ec0497273ec13e2c03e862c|aab1f1d02c3148a3d49a18b8de878603263e62e4a48622121efec92e5b40710b|cli/dirent")
# the patch's SHA-256 with LF line ends, whatever the checkout made of them: a patch changed without this pin stops here
set(FZ_PATCH ${CMAKE_CURRENT_SOURCE_DIR}/third_party/fossilize/scskiller.patch)
set(FZ_PATCH_SHA256 5F67621379DC5F455F90EF72126FDADAED76E7C15182FE3B018D28E50B4BC163)
file(READ ${FZ_PATCH} patch)
string(REPLACE "\r\n" "\n" patch "${patch}")
string(SHA256 FZ_PATCH_HASH "${patch}")
string(TOUPPER "${FZ_PATCH_HASH}" FZ_PATCH_HASH)
if(NOT FZ_PATCH_HASH STREQUAL FZ_PATCH_SHA256)
  message(FATAL_ERROR "third_party/fossilize/scskiller.patch: SHA-256 ${FZ_PATCH_HASH}, not the pinned ${FZ_PATCH_SHA256}")
endif()
set(FZ_STAMP "${FZ_SOURCES}|${FZ_PATCH_HASH}")
if(EXISTS ${FZ_DIR}/.scskiller-stamp)
  file(READ ${FZ_DIR}/.scskiller-stamp FZ_HAVE)
endif()
if(NOT FZ_HAVE STREQUAL FZ_STAMP)
  file(REMOVE_RECURSE ${FZ_DIR} ${FZ_DIR}-x)
  foreach(s ${FZ_SOURCES})
    string(REPLACE "|" ";" s "${s}")
    list(GET s 0 repo)
    list(GET s 1 commit)
    list(GET s 2 want)
    list(GET s 3 into)
    get_filename_component(name ${repo} NAME)
    set(tgz ${CMAKE_CURRENT_BINARY_DIR}/fossilize-archives/${name}-${commit}.tar.gz)
    if(EXISTS ${tgz})
      file(SHA256 ${tgz} got)
    endif()
    if(NOT got STREQUAL want)
      file(DOWNLOAD https://codeload.github.com/${repo}/tar.gz/${commit} ${tgz}.part TLS_VERIFY ON STATUS st)
      list(GET st 0 code)
      if(NOT code EQUAL 0)
        message(FATAL_ERROR "${repo} at ${commit} not downloaded: ${st}")
      endif()
      file(SHA256 ${tgz}.part got)
      if(NOT got STREQUAL want)
        file(REMOVE ${tgz}.part)
        message(FATAL_ERROR "${repo} at ${commit}: SHA-256 ${got}, not ${want}")
      endif()
      file(RENAME ${tgz}.part ${tgz})
    endif()
    file(ARCHIVE_EXTRACT INPUT ${tgz} DESTINATION ${FZ_DIR}-x)
    set(dest ${FZ_DIR})
    if(NOT into STREQUAL ".")
      set(dest ${FZ_DIR}/${into})
    endif()
    file(REMOVE_RECURSE ${dest})  # a submodule's empty folder in Fossilize's archive
    get_filename_component(parent ${dest} DIRECTORY)
    file(MAKE_DIRECTORY ${parent})
    file(RENAME ${FZ_DIR}-x/${name}-${commit} ${dest})
    unset(got)
  endforeach()
  file(REMOVE_RECURSE ${FZ_DIR}-x)
  # LF like the archives, whatever the checkout made of the patch (file(WRITE) writes CRLF here, the patch has no @x@);
  # the ceiling keeps git from applying it to this repo, and no user config (core.autocrlf) changes how it applies
  if(WIN32)
    set(NULL_DEVICE NUL)
  else()
    set(NULL_DEVICE /dev/null)
  endif()
  file(CONFIGURE OUTPUT ${CMAKE_CURRENT_BINARY_DIR}/fossilize-scskiller.patch CONTENT "${patch}" @ONLY NEWLINE_STYLE LF)
  execute_process(COMMAND ${CMAKE_COMMAND} -E env GIT_CEILING_DIRECTORIES=${CMAKE_CURRENT_BINARY_DIR} GIT_CONFIG_GLOBAL=${NULL_DEVICE} GIT_CONFIG_NOSYSTEM=1
                          git apply --whitespace=nowarn ${CMAKE_CURRENT_BINARY_DIR}/fossilize-scskiller.patch
                  WORKING_DIRECTORY ${FZ_DIR} RESULT_VARIABLE applied)
  if(NOT applied EQUAL 0)
    message(FATAL_ERROR "third_party/fossilize/scskiller.patch doesn't apply to Fossilize ${FZ_COMMIT}")
  endif()
  file(WRITE ${FZ_DIR}/.scskiller-stamp "${FZ_STAMP}")
endif()

enable_language(C)  # miniz and volk
set(FOSSILIZE_CLI OFF CACHE BOOL "" FORCE)   # its CMake builds every tool and SPIRV-Tools: the replayer is built below
set(FOSSILIZE_VULKAN_LAYER OFF CACHE BOOL "" FORCE)
set(FOSSILIZE_TESTS OFF CACHE BOOL "" FORCE)
add_subdirectory(${FZ_DIR} fossilize EXCLUDE_FROM_ALL)
if(NOT WIN32)
  # libVkLayer_scskiller.so: as VkLayer_scskiller.dll below, with libstdc++ inside and only the layer's entry points
  # exported (another Fossilize in the process, Steam's own layer, keeps its symbols and we keep ours).
  # VkLayer_scskiller.json beside it: the layer as an explicit one, for VK_LAYER_PATH (the app writes the per-game one).
  set_target_properties(fossilize PROPERTIES CXX_STANDARD 14 POSITION_INDEPENDENT_CODE ON CXX_VISIBILITY_PRESET hidden)
  add_library(scskiller_vklayer OBJECT vklayer.cpp)
  target_include_directories(scskiller_vklayer PRIVATE third_party/vulkan)
  set_target_properties(scskiller_vklayer PROPERTIES POSITION_INDEPENDENT_CODE ON CXX_VISIBILITY_PRESET hidden)
  add_library(VkLayer_scskiller SHARED ${FZ_DIR}/layer/device.cpp ${FZ_DIR}/layer/instance.cpp ${FZ_DIR}/layer/dispatch.cpp
    ${FZ_DIR}/layer/dispatch_helper.cpp $<TARGET_OBJECTS:scskiller_vklayer>)
  target_include_directories(VkLayer_scskiller PRIVATE ${FZ_DIR}/layer ${CMAKE_CURRENT_SOURCE_DIR} ${CMAKE_CURRENT_BINARY_DIR})
  set_target_properties(VkLayer_scskiller PROPERTIES CXX_STANDARD 14 CXX_VISIBILITY_PRESET hidden)
  find_package(Threads REQUIRED)
  target_link_libraries(VkLayer_scskiller PRIVATE fossilize Threads::Threads ${CMAKE_DL_LIBS})
  file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/vklayer.map "{ global: VK_LAYER_fossilize_*; local: *; };
")
  target_link_options(VkLayer_scskiller PRIVATE -Wl,--no-undefined -Wl,-Bsymbolic -Wl,--version-script=${CMAKE_CURRENT_BINARY_DIR}/vklayer.map -static-libstdc++ -static-libgcc)
  file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/VkLayer_scskiller.json [=[{
    "file_format_version": "1.0.0",
    "layer": {
        "name": "VK_LAYER_SCSKILLER",
        "type": "GLOBAL",
        "library_path": "./libVkLayer_scskiller.so",
        "api_version": "1.4.303",
        "implementation_version": "1",
        "description": "SCSKiller's Vulkan recorder",
        "functions": {"vkGetInstanceProcAddr": "VK_LAYER_fossilize_GetInstanceProcAddr", "vkGetDeviceProcAddr": "VK_LAYER_fossilize_GetDeviceProcAddr"}
    }
}
]=])
  # `selftest_linux vkrec`: the layer on the system's loader (selftest_linux.cpp). Exports SCSKiller_WarmHost (ENABLE_EXPORTS).
  add_executable(selftest_linux selftest_linux.cpp)
  target_include_directories(selftest_linux PRIVATE third_party/vulkan ${CMAKE_CURRENT_SOURCE_DIR})
  set_target_properties(selftest_linux PROPERTIES ENABLE_EXPORTS ON)
  target_link_libraries(selftest_linux PRIVATE ${CMAKE_DL_LIBS})
  add_dependencies(selftest_linux VkLayer_scskiller)
  # The Vulkan warm: scskiller_warm (warm_linux.cpp) and fossilize-replay beside it, the replayer as below, both with
  # libstdc++ inside as the layer
  add_executable(fossilize-replay ${FZ_DIR}/cli/fossilize_replay.cpp ${FZ_DIR}/cli/cli_parser.cpp ${FZ_DIR}/cli/device.cpp
    ${FZ_DIR}/cli/file.cpp ${FZ_DIR}/cli/fossilize_feature_filter.cpp ${FZ_DIR}/cli/volk/volk.c)
  target_include_directories(fossilize-replay PRIVATE ${FZ_DIR}/cli ${FZ_DIR}/cli/volk ${FZ_DIR}/khronos
    ${FZ_DIR}/cli/SPIRV-Headers/include/spirv/unified1)
  set_target_properties(fossilize-replay PROPERTIES CXX_STANDARD 14)
  # --spirv-val, as Steam's replayer checks Valve's download: the SPIRV-Tools of the build's system (the sniper SDK's)
  find_package(SPIRV-Tools CONFIG REQUIRED)
  target_compile_definitions(fossilize-replay PRIVATE FOSSILIZE_REPLAYER_SPIRV_VAL)
  target_link_libraries(fossilize-replay PRIVATE SPIRV-Tools-static)
  add_executable(scskiller_warm warm_linux.cpp)
  target_include_directories(scskiller_warm PRIVATE third_party/vulkan)
  foreach(t fossilize-replay scskiller_warm)
    target_link_libraries(${t} PRIVATE fossilize Threads::Threads ${CMAKE_DL_LIBS})
    target_link_options(${t} PRIVATE -static-libstdc++ -static-libgcc)
  endforeach()
  add_dependencies(scskiller_warm fossilize-replay)
  if(SCSK_VERSION MATCHES "^0[.]0[.]0-internal")
    target_compile_definitions(fossilize-replay PRIVATE SCSK_TEST_HOOKS)  # SCSKILLER_REPLAY_CRASH and the others
  endif()
  return()
endif()
# as Fossilize's cli/CMakeLists.txt builds it, without SPIR-V validation (FOSSILIZE_REPLAYER_SPIRV_VAL off)
add_executable(fossilize-replay ${FZ_DIR}/cli/fossilize_replay.cpp ${FZ_DIR}/cli/cli_parser.cpp ${FZ_DIR}/cli/device.cpp
  ${FZ_DIR}/cli/file.cpp ${FZ_DIR}/cli/fossilize_feature_filter.cpp ${FZ_DIR}/cli/volk/volk.c version.rc)
target_include_directories(fossilize-replay PRIVATE ${FZ_DIR}/cli ${FZ_DIR}/cli/volk ${FZ_DIR}/khronos
  ${FZ_DIR}/cli/SPIRV-Headers/include/spirv/unified1 ${CMAKE_CURRENT_BINARY_DIR})
target_compile_definitions(fossilize-replay PRIVATE SCSK_RC_REPLAY)
target_compile_options(fossilize-replay PRIVATE /D_CRT_SECURE_NO_WARNINGS /D_SCL_SECURE_NO_WARNINGS /wd4267 /wd4244 /wd4309 /wd4005 /DNOMINMAX)
target_link_libraries(fossilize-replay PRIVATE fossilize)
set_target_properties(fossilize fossilize-replay PROPERTIES CXX_STANDARD 14)
# VkLayer_scskiller.dll, the Vulkan recorder: Fossilize's capture layer (patched: admission, the dump path, no environment,
# timings) with vklayer.cpp, which is C++20 like admit.h while Fossilize builds as C++14. The app registers it per game.
add_library(scskiller_vklayer OBJECT vklayer.cpp)
target_compile_options(scskiller_vklayer PRIVATE /DNOMINMAX)
target_include_directories(scskiller_vklayer PRIVATE third_party/vulkan)
add_library(VkLayer_scskiller SHARED ${FZ_DIR}/layer/device.cpp ${FZ_DIR}/layer/instance.cpp ${FZ_DIR}/layer/dispatch.cpp
  ${FZ_DIR}/layer/dispatch_helper.cpp version.rc $<TARGET_OBJECTS:scskiller_vklayer>)
target_include_directories(VkLayer_scskiller PRIVATE ${FZ_DIR}/layer ${CMAKE_CURRENT_SOURCE_DIR} ${CMAKE_CURRENT_BINARY_DIR})
target_compile_definitions(VkLayer_scskiller PRIVATE SCSK_RC_VKLAYER)
target_compile_options(VkLayer_scskiller PRIVATE /D_CRT_SECURE_NO_WARNINGS /D_SCL_SECURE_NO_WARNINGS /wd4267 /wd4244 /wd4309 /wd4005 /DNOMINMAX)
target_link_libraries(VkLayer_scskiller PRIVATE fossilize bcrypt)
target_link_options(VkLayer_scskiller PRIVATE /DELAYLOAD:shell32.dll /DELAYLOAD:ole32.dll)  # only admission uses them (the recorder folder)
target_link_libraries(VkLayer_scskiller PRIVATE delayimp)
set_target_properties(VkLayer_scskiller PROPERTIES CXX_STANDARD 14)

if(SCSK_VERSION MATCHES "^0[.]0[.]0-internal")
  target_compile_definitions(fossilize-replay PRIVATE SCSK_TEST_HOOKS)  # SCSKILLER_REPLAY_CRASH, for selftest vkwarm
  target_compile_definitions(selftest PRIVATE SCSK_TEST_HOOKS)
endif()
add_dependencies(selftest scskiller_warm)
foreach(t scskiller_warm scskiller_warm_segheap)
  target_link_libraries(${t} fossilize bcrypt)  # ExternalReplayer and its control block; BCrypt: the failed keys' SHA-1
  add_dependencies(${t} fossilize-replay)
endforeach()

# D3D11 driver-cache measurement probe. Standalone: talks to system d3d11.dll directly, no SCSKiller shim involved.
# Also the "game" of the D3D11 warm end-to-end test (compile, first).
add_executable(probe11 probe11.cpp)
target_link_libraries(probe11 d3dcompiler)

# D3D11 compile-key probe: which input-layout fields, partner stages and output state make the driver compile again.
add_executable(layout11 layout11.cpp)
target_link_libraries(layout11 d3dcompiler)

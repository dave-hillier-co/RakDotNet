cmake_minimum_required(VERSION 3.24)
project(interop_peer LANGUAGES CXX)

include(FetchContent)

# RakNet 4.081 pinned to the upstream archive commit. SOURCE_SUBDIR points at a directory
# that does not exist, so FetchContent_MakeAvailable only populates the sources and never
# configures RakNet's own CMakeLists (it declares cmake 2.6, which CMake 4 rejects).
# Pass -DFETCHCONTENT_SOURCE_DIR_RAKNET=<checkout> to build from a local copy without network.
FetchContent_Declare(
  RakNet
  GIT_REPOSITORY https://github.com/facebookarchive/RakNet.git
  GIT_TAG 1a169895a900c9fc4841c556e16514182b75faf8
  SOURCE_SUBDIR interop-peer-no-cmake
)

FetchContent_Declare(
  nlohmann_json
  URL https://github.com/nlohmann/json/releases/download/v3.11.3/json.tar.xz
  URL_HASH SHA256=d6c65aca6b1ed68e7a182f4757257b107ae403032760ed6ef121c9d55e81757d
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
)

FetchContent_MakeAvailable(RakNet nlohmann_json)

set(RAKNET_SOURCE_ROOT "${raknet_SOURCE_DIR}/Source")
file(GLOB RAKNET_SOURCES CONFIGURE_DEPENDS "${RAKNET_SOURCE_ROOT}/*.cpp")
if(NOT RAKNET_SOURCES)
  message(FATAL_ERROR "No RakNet sources found under ${RAKNET_SOURCE_ROOT}")
endif()

find_package(Threads REQUIRED)

# RakNet is compiled as-is; build problems are fixed with flags and definitions only.
# gnu++98: ReplicaManager3.cpp:141 compares a pointer with `false`, which is a null pointer
# constant only before C++11 (clang rejects it as an error in C++11 and later).
add_library(raknet_static STATIC ${RAKNET_SOURCES})
target_include_directories(raknet_static SYSTEM PUBLIC "${RAKNET_SOURCE_ROOT}")
set_target_properties(raknet_static PROPERTIES
  CXX_STANDARD 98
  CXX_STANDARD_REQUIRED ON
  CXX_EXTENSIONS ON)
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
  target_compile_options(raknet_static PRIVATE -w)
endif()
# CCRakNetSlidingWindow.cpp:94 converts the double -1 (UNSET_TIME_US) to uint64_t, which is
# undefined behaviour. Clang folds it to a trap and the peer dies with SIGTRAP on its first
# ACK. This flag makes out-of-range float-to-int conversions saturate instead.
include(CheckCXXCompilerFlag)
check_cxx_compiler_flag(-fno-strict-float-cast-overflow INTEROP_HAS_FLOAT_CAST_OVERFLOW_FLAG)
if(INTEROP_HAS_FLOAT_CAST_OVERFLOW_FLAG)
  target_compile_options(raknet_static PRIVATE -fno-strict-float-cast-overflow)
endif()
# TCPInterface's thread takes accept()'s -1 for a new socket (TCPInterface.cpp:1064-1066), as it gets when Stop closes the
# listening socket under it, and passes it to FD_SET on its next pass (TCPInterface.cpp:1038). glibc's fortified FD_SET
# aborts the process on that descriptor; Ubuntu's compilers fortify by default, so RakNet is compiled without it.
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
  target_compile_options(raknet_static PRIVATE -U_FORTIFY_SOURCE)
endif()
target_link_libraries(raknet_static PUBLIC Threads::Threads)

function(interop_peer_settings target)
  set_target_properties(${target} PROPERTIES
    CXX_STANDARD 17
    CXX_STANDARD_REQUIRED ON
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")
  if(CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
    target_compile_options(${target} PRIVATE -Wall -Wextra -Werror)
  endif()
endfunction()

# cat::EasyHandshake key generation, compiled as RakNet is because cat's headers are not valid C++17.
add_library(interop_keys STATIC src/KeyGeneration.cpp)
target_include_directories(interop_keys PUBLIC src)
target_link_libraries(interop_keys PUBLIC raknet_static)
set_target_properties(interop_keys PROPERTIES CXX_STANDARD 98 CXX_STANDARD_REQUIRED ON CXX_EXTENSIONS ON)

# Translation logic (hex, enum names, packet events, plugin and security commands), shared by the peer and its tests.
add_library(interop_logic STATIC
  src/CloudCommands.cpp
  src/ConsoleCommands.cpp
  src/FileCommands.cpp
  src/Hex.cpp
  src/Names.cpp
  src/NatCommands.cpp
  src/NatGateway.cpp
  src/PacketEvent.cpp
  src/PluginCommands.cpp
  src/RelayCommands.cpp
  src/ReplicaManager3Commands.cpp
  src/ReplicationCommands.cpp
  src/SecurityCommands.cpp
  src/ServiceCommands.cpp
  src/ServiceRedirects.cpp
  src/TcpCommands.cpp
  src/TeamCommands.cpp
  src/ThreadPriority.cpp
  src/TopologyCommands.cpp)
target_include_directories(interop_logic PUBLIC src)
target_link_libraries(interop_logic PUBLIC raknet_static interop_keys nlohmann_json::nlohmann_json ${CMAKE_DL_LIBS})
interop_peer_settings(interop_logic)

add_executable(interop-peer src/main.cpp)
target_link_libraries(interop-peer PRIVATE interop_logic)
interop_peer_settings(interop-peer)

# RakNet's Samples/LoopbackPerformanceTest as an unattended burst, compared with RakDotNet in docs/performance.md.
add_executable(loopback-bench src/LoopbackBench.cpp)
target_link_libraries(loopback-bench PRIVATE interop_logic)
interop_peer_settings(loopback-bench)

enable_testing()
add_executable(interop-peer-tests tests/LogicTests.cpp)
target_link_libraries(interop-peer-tests PRIVATE interop_logic)
interop_peer_settings(interop-peer-tests)
add_test(NAME interop-peer-logic COMMAND interop-peer-tests)

# interop-peer-secure: the same peer linked against RakNet compiled with LIBCAT_SECURITY=1, which builds
# DependentExtensions/cat through Source/SecureHandshake.cpp. A separate binary, because a RakNet build either has
# secure connections or does not. Its sources are taken from the targets above, so commands added there are built
# into both peers.
option(LIBCAT_SECURITY "Also build interop-peer-secure against RakNet with LIBCAT_SECURITY=1" ON)
if(LIBCAT_SECURITY)
  set(RAKNET_DEPENDENT_EXTENSIONS "${raknet_SOURCE_DIR}/DependentExtensions")
  if(NOT EXISTS "${RAKNET_DEPENDENT_EXTENSIONS}/cat")
    message(FATAL_ERROR "No DependentExtensions/cat found under ${raknet_SOURCE_DIR}")
  endif()

  add_library(raknet_static_secure STATIC ${RAKNET_SOURCES})
  target_include_directories(raknet_static_secure SYSTEM PUBLIC "${RAKNET_SOURCE_ROOT}" "${RAKNET_DEPENDENT_EXTENSIONS}")
  target_compile_definitions(raknet_static_secure PUBLIC LIBCAT_SECURITY=1)
  get_target_property(raknet_options raknet_static COMPILE_OPTIONS)
  if(raknet_options)
    target_compile_options(raknet_static_secure PRIVATE ${raknet_options})
  endif()
  set_target_properties(raknet_static_secure PROPERTIES CXX_STANDARD 98 CXX_STANDARD_REQUIRED ON CXX_EXTENSIONS ON)
  target_link_libraries(raknet_static_secure PUBLIC Threads::Threads)

  # Each interop target's secure twin links the secure twins of its interop dependencies.
  function(interop_secure_twin target twin kind)
    get_target_property(sources ${target} SOURCES)
    if(kind STREQUAL "EXECUTABLE")
      add_executable(${twin} ${sources})
    else()
      add_library(${twin} STATIC ${sources})
    endif()
    get_target_property(includes ${target} INCLUDE_DIRECTORIES)
    if(includes)
      target_include_directories(${twin} PUBLIC ${includes})
    endif()
    get_target_property(libraries ${target} LINK_LIBRARIES)
    foreach(library IN LISTS libraries)
      if(library STREQUAL "raknet_static")
        target_link_libraries(${twin} PUBLIC raknet_static_secure)
      elseif(TARGET ${library}_secure)
        target_link_libraries(${twin} PUBLIC ${library}_secure)
      else()
        target_link_libraries(${twin} PUBLIC ${library})
      endif()
    endforeach()
    foreach(property CXX_STANDARD CXX_STANDARD_REQUIRED CXX_EXTENSIONS COMPILE_OPTIONS)
      get_target_property(value ${target} ${property})
      if(value)
        set_target_properties(${twin} PROPERTIES ${property} "${value}")
      endif()
    endforeach()
    set_target_properties(${twin} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")
  endfunction()

  interop_secure_twin(interop_keys interop_keys_secure LIBRARY)
  interop_secure_twin(interop_logic interop_logic_secure LIBRARY)
  interop_secure_twin(interop-peer interop-peer-secure EXECUTABLE)
endif()

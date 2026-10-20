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

# Translation logic (hex, enum names, packet events), shared by the peer and its tests.
add_library(interop_logic STATIC
  src/Hex.cpp
  src/Names.cpp
  src/PacketEvent.cpp
  src/PluginCommands.cpp)
target_include_directories(interop_logic PUBLIC src)
target_link_libraries(interop_logic PUBLIC raknet_static nlohmann_json::nlohmann_json)
interop_peer_settings(interop_logic)

add_executable(interop-peer src/main.cpp)
target_link_libraries(interop-peer PRIVATE interop_logic)
interop_peer_settings(interop-peer)

enable_testing()
add_executable(interop-peer-tests tests/LogicTests.cpp)
target_link_libraries(interop-peer-tests PRIVATE interop_logic)
interop_peer_settings(interop-peer-tests)
add_test(NAME interop-peer-logic COMMAND interop-peer-tests)

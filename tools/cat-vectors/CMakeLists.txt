cmake_minimum_required(VERSION 3.24)
project(cat_vectors LANGUAGES CXX)

include(FetchContent)

# The same pinned RakNet as tools/interop-peer. SOURCE_SUBDIR points at a directory that does not exist, so
# RakNet's own CMakeLists is never configured. Pass -DFETCHCONTENT_SOURCE_DIR_RAKNET=<checkout> to build from a
# local copy without network.
FetchContent_Declare(
  RakNet
  GIT_REPOSITORY https://github.com/facebookarchive/RakNet.git
  GIT_TAG 1a169895a900c9fc4841c556e16514182b75faf8
  SOURCE_SUBDIR cat-vectors-no-cmake
)
FetchContent_MakeAvailable(RakNet)

set(RAKNET_SOURCE_ROOT "${raknet_SOURCE_DIR}/Source")
set(RAKNET_DEPENDENT_EXTENSIONS "${raknet_SOURCE_DIR}/DependentExtensions")
if(NOT EXISTS "${RAKNET_SOURCE_ROOT}/SecureHandshake.cpp" OR NOT EXISTS "${RAKNET_DEPENDENT_EXTENSIONS}/cat")
  message(FATAL_ERROR "No RakNet sources with DependentExtensions/cat found under ${raknet_SOURCE_DIR}")
endif()

find_package(Threads REQUIRED)

# Source/SecureHandshake.cpp is the translation unit RakNet uses to compile cat when LIBCAT_SECURITY is 1; it
# includes exactly the cat sources RakNet links. cat is compiled as-is, with RakNet's flags (docs/plan.md).
add_executable(cat-vectors cat-vectors.cpp "${RAKNET_SOURCE_ROOT}/SecureHandshake.cpp")
target_include_directories(cat-vectors SYSTEM PRIVATE "${RAKNET_SOURCE_ROOT}" "${RAKNET_DEPENDENT_EXTENSIONS}")
target_compile_definitions(cat-vectors PRIVATE LIBCAT_SECURITY=1)
set_target_properties(cat-vectors PROPERTIES
  CXX_STANDARD 98
  CXX_STANDARD_REQUIRED ON
  CXX_EXTENSIONS ON
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
  target_compile_options(cat-vectors PRIVATE -w)
endif()
target_link_libraries(cat-vectors PRIVATE Threads::Threads)

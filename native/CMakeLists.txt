cmake_minimum_required(VERSION 3.24)
project(AoTYaml LANGUAGES CXX)

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
include(FetchContent)

set(YAML_CPP_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(YAML_CPP_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(YAML_CPP_BUILD_CONTRIB OFF CACHE BOOL "" FORCE)
set(YAML_CPP_FORMAT_SOURCE OFF CACHE BOOL "" FORCE)
set(YAML_CPP_INSTALL OFF CACHE BOOL "" FORCE)
set(YAML_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(YAML_MSVC_SHARED_RT OFF CACHE BOOL "" FORCE)
FetchContent_Declare(yaml_cpp
    URL https://github.com/jbeder/yaml-cpp/archive/refs/tags/0.8.0.tar.gz
    URL_HASH SHA512=aae9d618f906117d620d63173e95572c738db518f4ff1901a06de2117d8deeb8045f554102ca0ba4735ac0c4d060153a938ef78da3e0da3406d27b8298e5f38e
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_Declare(qtr
    URL https://github.com/QTR-Modding/CLibUtilsQTR/archive/v2.6.0.tar.gz
    URL_HASH SHA512=73369daa96015cddacbf4bc949bff1d522695bdc17fdc715beca3c8ef80a417557ede22d902acbbf1c0c14c6a0b227a11b3c2553b3019ee657f22e04eb34a7ea
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    SOURCE_SUBDIR headers_only)
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
FetchContent_MakeAvailable(yaml_cpp qtr)

add_executable(AoTYaml main.cpp)
target_compile_features(AoTYaml PRIVATE cxx_std_23)
target_include_directories(AoTYaml PRIVATE "${qtr_SOURCE_DIR}/include")
target_link_libraries(AoTYaml PRIVATE yaml-cpp::yaml-cpp)

configure_file("${qtr_SOURCE_DIR}/LICENSE" "${CMAKE_BINARY_DIR}/licenses/CLibUtilsQTR.txt" COPYONLY)
configure_file("${yaml_cpp_SOURCE_DIR}/LICENSE" "${CMAKE_BINARY_DIR}/licenses/yaml-cpp.txt" COPYONLY)

cmake_minimum_required(VERSION 3.20)
project(cellport_audio_supervisor C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

add_library(cellport_audio_supervisor STATIC
    protocol.c
    runtime.c
    sha256.c
    supervisor.c)
target_include_directories(cellport_audio_supervisor PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
if(MSVC)
    target_compile_options(cellport_audio_supervisor PRIVATE /W4 /WX)
else()
    target_compile_options(cellport_audio_supervisor PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

add_executable(cellport-audio-supervisor-sim simulator_main.c)
target_link_libraries(cellport-audio-supervisor-sim PRIVATE cellport_audio_supervisor)

include(CTest)
if(BUILD_TESTING)
    add_executable(cellport-audio-supervisor-tests tests.c)
    target_link_libraries(cellport-audio-supervisor-tests PRIVATE cellport_audio_supervisor)
    add_test(NAME cellport-audio-supervisor-tests COMMAND cellport-audio-supervisor-tests)
endif()

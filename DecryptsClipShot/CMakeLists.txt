cmake_minimum_required(VERSION 3.21)
project(DecryptsClipShotEngine C)

set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_executable(TrickClip src/trickclip.c)
target_compile_definitions(TrickClip PRIVATE UNICODE _UNICODE _CRT_SECURE_NO_WARNINGS)
target_link_libraries(TrickClip PRIVATE user32 shell32 ole32 uuid psapi)

if(MSVC)
    target_compile_options(TrickClip PRIVATE /W4)
else()
    target_compile_options(TrickClip PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_custom_command(TARGET TrickClip POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${CMAKE_SOURCE_DIR}/trickclip.ini"
            "$<TARGET_FILE_DIR:TrickClip>/trickclip.ini")

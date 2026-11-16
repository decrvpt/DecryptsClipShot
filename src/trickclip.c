#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif

#include <windows.h>
#include <shlobj.h>
#include <knownfolders.h>
#include <tlhelp32.h>
#include <direct.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>
#include <ctype.h>
#include <time.h>

#define ARRAY_COUNT(a) (sizeof(a) / sizeof((a)[0]))
#define PATH_CAP 2048
#define COMMAND_CAP 16384
#define MAX_SEGMENTS 512
#define MAX_AUDIO_HELPERS 1
#define AUDIO_SEGMENT_SECONDS 5
#define ENGINE_BUILD L"classic-video-12"
#define ENGINE_PIPELINE L"realtime-timestamps-aligned-audio"
/* Frames FFmpeg may queue between capture and encoder. The old value (2048)
   let a slow encoder build a silent backlog of more than a minute. A small
   queue keeps latency bounded: if the encoder falls behind, capture frames are
   dropped at the source and the real capture timestamps keep time correct. */
#define CAPTURE_QUEUE_FRAMES 16
#define TIMING_REPORT_MS 60000.0
#define DRIFT_WARNING_MS 250.0
#define AUDIO_RESTART_COOLDOWN_MS 10000ULL
/* Seconds of extra footage requested before the clip start so a stream-copy
   cut can begin on the preceding keyframe (GOP is two seconds). */
#define KEYFRAME_MARGIN_SECONDS 2
#define HOTKEY_SAVE 1
#define FORTNITE_PROCESS L"FortniteClient-Win64-Shipping.exe"
#define SAVE_EVENT_NAME L"Local\\DecryptsClipShot.Save"
#define STOP_EVENT_NAME L"Local\\DecryptsClipShot.Stop"
#define ENGINE_MUTEX_NAME L"Local\\DecryptsClipShot.Engine"

typedef struct {
    wchar_t base_dir[PATH_CAP];
    wchar_t ffmpeg_path[PATH_CAP];
    wchar_t buffer_dir[PATH_CAP];
    wchar_t clips_dir[PATH_CAP];
    wchar_t work_dir[PATH_CAP];
    wchar_t game_audio_device[512];
    wchar_t manual_hotkey[128];
    wchar_t capture_backend[64];
    wchar_t video_encoder[128];
    wchar_t video_options[1024];
    wchar_t audio_helper_path[PATH_CAP];
    wchar_t local_parser_path[PATH_CAP];
    int framerate;
    int segment_seconds;
    int buffer_seconds;
    int clip_seconds;
    int replay_stable_seconds;
    int post_match_seconds;
    int start_only_with_fortnite;
    int keep_rejected_clips;
    int local_replay_enabled;
    int capture_game_audio;
    int output_height;
    int prioritize_game;
    int av_sync_offset_ms;
    int game_audio_volume;
    int capture_microphone;
    int microphone_volume;
    wchar_t microphone_device[512];
} AppConfig;

/* Reads FFmpeg's -progress output on a background thread and compares the
   media time FFmpeg has written with real elapsed time. Growth of that
   difference over a session is exactly the "progressive delay" symptom. */
typedef struct {
    HANDLE read;
    HANDLE thread;
    SRWLOCK lock;
    double started_ms;
    double lag_ms;
    double baseline_ms;
    double sample_ms;
    int samples;
} ProgressClock;

typedef struct {
    PROCESS_INFORMATION process;
    PROCESS_INFORMATION audio_process;
    PROCESS_INFORMATION audio_helpers[MAX_AUDIO_HELPERS];
    int audio_helper_count;
    HANDLE stdin_write;
    HANDLE job;
    int running;
    int audio_running;
    int audio_wanted;
    int audio_restarts;
    ULONGLONG audio_failed_tick;
    ProgressClock video_clock;
    ProgressClock audio_clock;
    double last_timing_report_ms;
} Recorder;

typedef struct {
    double video_drift_ms;
    double audio_drift_ms;
    double video_lag_ms;
    int have_video;
    int have_audio;
} TimingSnapshot;

typedef struct {
    wchar_t path[PATH_CAP];
    FILETIME modified;
    ULONGLONG size;
} Segment;

typedef struct {
    wchar_t tracked_path[PATH_CAP];
    ULONGLONG last_size;
    ULONGLONG last_change_tick;
    int handled;
    int observed_activity;
} ReplayWatch;

typedef struct {
    int conclusive;
    int placement;
    int has_kill;
    int sniper;
    int trickshot_weapon; /* any sniper or heavy-ammo weapon */
    double distance_uu;
    char killer[128];
    char victim[128];
    char weapon[128];
    char shot_type[64];
    char reason[512];
} MatchResult;

static volatile LONG g_quit = 0;
/* QPC time at which capture last stopped; used to place clips saved after
   Fortnite closed (no live segment to measure against). */
static double g_capture_stopped_ms = 0.0;

static double now_ms(void)
{
    static LARGE_INTEGER frequency;
    LARGE_INTEGER counter;
    if (!frequency.QuadPart) QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&counter);
    return (double)counter.QuadPart * 1000.0 / (double)frequency.QuadPart;
}

static void print_last_error(const wchar_t *message)
{
    wprintf(L"%ls (Windows error %lu)\n", message, GetLastError());
}

static BOOL WINAPI console_handler(DWORD signal)
{
    if (signal == CTRL_C_EVENT || signal == CTRL_CLOSE_EVENT ||
        signal == CTRL_BREAK_EVENT || signal == CTRL_SHUTDOWN_EVENT) {
        InterlockedExchange(&g_quit, 1);
        return TRUE;
    }
    return FALSE;
}

static void get_executable_directory(wchar_t *out, size_t out_count)
{
    DWORD length = GetModuleFileNameW(NULL, out, (DWORD)out_count);
    if (length == 0 || length >= out_count) {
        wcscpy_s(out, out_count, L".");
        return;
    }

    wchar_t *slash = wcsrchr(out, L'\\');
    if (slash) *slash = L'\0';
}

static int path_is_absolute(const wchar_t *path)
{
    return (wcslen(path) >= 3 && path[1] == L':' &&
            (path[2] == L'\\' || path[2] == L'/')) ||
           (path[0] == L'\\' && path[1] == L'\\');
}

static void join_path(wchar_t *out, size_t out_count,
                      const wchar_t *left, const wchar_t *right)
{
    if (path_is_absolute(right)) {
        wcscpy_s(out, out_count, right);
        return;
    }
    swprintf_s(out, out_count, L"%ls\\%ls", left, right);
}

static void ensure_directory(const wchar_t *path)
{
    if (CreateDirectoryW(path, NULL)) return;
    if (GetLastError() != ERROR_ALREADY_EXISTS) print_last_error(L"Could not create directory");
}

static void load_ini_string(const wchar_t *ini, const wchar_t *section,
                            const wchar_t *key, const wchar_t *fallback,
                            wchar_t *out, DWORD out_count)
{
    GetPrivateProfileStringW(section, key, fallback, out, out_count, ini);
}

static int load_ini_int(const wchar_t *ini, const wchar_t *section,
                        const wchar_t *key, int fallback)
{
    return (int)GetPrivateProfileIntW(section, key, fallback, ini);
}

static void get_config_path(const wchar_t *base_dir, wchar_t *out, size_t out_count)
{
    wchar_t local_app_data[PATH_CAP];
    wchar_t config_dir[PATH_CAP];
    wchar_t bundled_ini[PATH_CAP];
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local_app_data,
                                           ARRAY_COUNT(local_app_data));

    if (length == 0 || length >= ARRAY_COUNT(local_app_data)) {
        join_path(out, out_count, base_dir, L"trickclip.ini");
        return;
    }

    join_path(config_dir, ARRAY_COUNT(config_dir), local_app_data, L"Decrypts ClipShot");
    ensure_directory(config_dir);
    join_path(out, out_count, config_dir, L"trickclip.ini");
    if (GetFileAttributesW(out) == INVALID_FILE_ATTRIBUTES) {
        join_path(bundled_ini, ARRAY_COUNT(bundled_ini), base_dir, L"trickclip.ini");
        CopyFileW(bundled_ini, out, TRUE);
    }
}

static void get_default_clips_dir(const wchar_t *base_dir, wchar_t *out, size_t out_count)
{
    PWSTR videos = NULL;
    if (SUCCEEDED(SHGetKnownFolderPath(&FOLDERID_Videos, KF_FLAG_CREATE, NULL, &videos)) && videos) {
        swprintf_s(out, out_count, L"%ls\\Decrypt's ClipShot", videos);
        CoTaskMemFree(videos);
        return;
    }
    if (videos) CoTaskMemFree(videos);
    join_path(out, out_count, base_dir, L"clips");
}

static void load_config(AppConfig *config)
{
    wchar_t ini[PATH_CAP];
    wchar_t relative[PATH_CAP];

    ZeroMemory(config, sizeof(*config));
    get_executable_directory(config->base_dir, ARRAY_COUNT(config->base_dir));
    get_config_path(config->base_dir, ini, ARRAY_COUNT(ini));

    config->framerate = load_ini_int(ini, L"recording", L"framerate", 30);
    config->segment_seconds = load_ini_int(ini, L"recording", L"segment_seconds", 30);
    config->buffer_seconds = load_ini_int(ini, L"recording", L"buffer_seconds", 180);
    config->clip_seconds = load_ini_int(ini, L"recording", L"clip_seconds", 90);
    config->replay_stable_seconds = load_ini_int(ini, L"app", L"replay_stable_seconds", 15);
    config->post_match_seconds = load_ini_int(ini, L"app", L"post_match_seconds", 15);
    config->start_only_with_fortnite = load_ini_int(ini, L"app", L"start_only_with_fortnite", 1);
    config->keep_rejected_clips = load_ini_int(ini, L"app", L"keep_rejected_clips", 1);
    config->local_replay_enabled = load_ini_int(ini, L"local_replay", L"enabled", 1);
    config->capture_game_audio = load_ini_int(ini, L"recording", L"capture_game_audio", 1);
    config->output_height = load_ini_int(ini, L"recording", L"output_height", 1080);
    config->prioritize_game = load_ini_int(ini, L"recording", L"prioritize_game", 1);
    /* Advanced, not shown in the GUI: positive values delay audio relative to
       video in saved clips. Leave at 0 unless a fixed offset is measured. */
    config->av_sync_offset_ms = load_ini_int(ini, L"recording", L"av_sync_offset_ms", 0);
    /* Clip volume slider in Settings → Audio, in percent (100 = as heard in game). */
    config->game_audio_volume = load_ini_int(ini, L"recording", L"game_audio_volume", 100);
    /* Microphone is off unless the player turns it on in Settings → Audio.
       It is mixed into the audio track by ClipShotAudio, never by the video
       FFmpeg, so it can't cost game FPS. */
    config->capture_microphone = load_ini_int(ini, L"recording", L"capture_microphone", 0);
    config->microphone_volume = load_ini_int(ini, L"recording", L"microphone_volume", 100);
    load_ini_string(ini, L"recording", L"microphone_device", L"Default microphone",
                    config->microphone_device, ARRAY_COUNT(config->microphone_device));
    if (!config->microphone_device[0])
        wcscpy_s(config->microphone_device, ARRAY_COUNT(config->microphone_device), L"Default microphone");

    load_ini_string(ini, L"recording", L"ffmpeg_path", L"ffmpeg.exe",
                    relative, ARRAY_COUNT(relative));
    if (wcschr(relative, L'\\') || wcschr(relative, L'/'))
        join_path(config->ffmpeg_path, ARRAY_COUNT(config->ffmpeg_path), config->base_dir, relative);
    else {
        wchar_t bundled_ffmpeg[PATH_CAP];
        join_path(bundled_ffmpeg, ARRAY_COUNT(bundled_ffmpeg), config->base_dir, relative);
        if (GetFileAttributesW(bundled_ffmpeg) != INVALID_FILE_ATTRIBUTES)
            wcscpy_s(config->ffmpeg_path, ARRAY_COUNT(config->ffmpeg_path), bundled_ffmpeg);
        else
            wcscpy_s(config->ffmpeg_path, ARRAY_COUNT(config->ffmpeg_path), relative);
    }

    load_ini_string(ini, L"recording", L"video_encoder", L"libx264",
                    config->video_encoder, ARRAY_COUNT(config->video_encoder));
    load_ini_string(ini, L"recording", L"capture_backend", L"auto",
                    config->capture_backend, ARRAY_COUNT(config->capture_backend));
    load_ini_string(ini, L"recording", L"video_options",
                    L"-preset veryfast -crf 20 -pix_fmt yuv420p",
                    config->video_options, ARRAY_COUNT(config->video_options));
    load_ini_string(ini, L"recording", L"game_audio_device", L"Default Windows output",
                    config->game_audio_device, ARRAY_COUNT(config->game_audio_device));
    if (!config->game_audio_device[0]) {
        load_ini_string(ini, L"recording", L"audio_device", L"",
                        config->game_audio_device, ARRAY_COUNT(config->game_audio_device));
    }
    if (!config->game_audio_device[0])
        wcscpy_s(config->game_audio_device, ARRAY_COUNT(config->game_audio_device),
                 L"Default Windows output");
    load_ini_string(ini, L"controls", L"manual_clip_hotkey", L"F8",
                    config->manual_hotkey, ARRAY_COUNT(config->manual_hotkey));

    load_ini_string(ini, L"folders", L"buffer", L"buffer", relative, ARRAY_COUNT(relative));
    join_path(config->buffer_dir, ARRAY_COUNT(config->buffer_dir), config->base_dir, relative);
    /* Clips go to the player's Videos folder unless they picked another one.
       An empty value, or the old "clips" default (inside the install folder),
       means Videos\Decrypt's ClipShot. */
    load_ini_string(ini, L"folders", L"clips", L"", relative, ARRAY_COUNT(relative));
    if (!relative[0] || _wcsicmp(relative, L"clips") == 0)
        get_default_clips_dir(config->base_dir, config->clips_dir, ARRAY_COUNT(config->clips_dir));
    else
        join_path(config->clips_dir, ARRAY_COUNT(config->clips_dir), config->base_dir, relative);
    load_ini_string(ini, L"folders", L"work", L"work", relative, ARRAY_COUNT(relative));
    join_path(config->work_dir, ARRAY_COUNT(config->work_dir), config->base_dir, relative);

    load_ini_string(ini, L"local_replay", L"parser_path", L"ClipShotReplayParser.exe",
                    relative, ARRAY_COUNT(relative));
    join_path(config->local_parser_path, ARRAY_COUNT(config->local_parser_path),
              config->base_dir, relative);

    load_ini_string(ini, L"recording", L"audio_helper_path", L"ClipShotAudio.exe",
                    relative, ARRAY_COUNT(relative));
    join_path(config->audio_helper_path, ARRAY_COUNT(config->audio_helper_path),
              config->base_dir, relative);

    if (config->framerate < 1) config->framerate = 30;
    if (config->segment_seconds < 1) config->segment_seconds = 30;
    if (config->buffer_seconds < config->segment_seconds * 3) config->buffer_seconds = 60;
    if (config->clip_seconds < config->segment_seconds) config->clip_seconds = config->segment_seconds;
    if (config->clip_seconds > config->buffer_seconds) config->clip_seconds = config->buffer_seconds;
    if (config->replay_stable_seconds < 5) config->replay_stable_seconds = 5;
    if (config->post_match_seconds < 0) config->post_match_seconds = 0;
    if (config->post_match_seconds > 60) config->post_match_seconds = 60;
    if (config->output_height != 720 && config->output_height != 1080)
        config->output_height = 0;
    if (config->av_sync_offset_ms < -2000) config->av_sync_offset_ms = -2000;
    if (config->av_sync_offset_ms > 2000) config->av_sync_offset_ms = 2000;
    if (config->game_audio_volume < 0) config->game_audio_volume = 0;
    if (config->game_audio_volume > 200) config->game_audio_volume = 200;
    if (config->microphone_volume < 0) config->microphone_volume = 0;
    if (config->microphone_volume > 200) config->microphone_volume = 200;
}

static int video_wrap_count(const AppConfig *config)
{
    int count = config->buffer_seconds / config->segment_seconds + 2;
    return count > MAX_SEGMENTS ? MAX_SEGMENTS : count;
}

static int audio_wrap_count(const AppConfig *config)
{
    int count = config->buffer_seconds / AUDIO_SEGMENT_SECONDS + 3;
    return count > MAX_SEGMENTS ? MAX_SEGMENTS : count;
}

/* ---------- FFmpeg progress clocks ---------- */

static void progress_handle_line(ProgressClock *clock, const char *line,
                                 LONGLONG *pending_out_time_us)
{
    if (strncmp(line, "out_time_us=", 12) == 0) {
        const char *value = line + 12;
        *pending_out_time_us = (*value >= '0' && *value <= '9')
            ? _strtoi64(value, NULL, 10) : -1;
    } else if (strncmp(line, "progress=", 9) == 0) {
        if (*pending_out_time_us > 0) {
            double arrival = now_ms();
            double lag = (arrival - clock->started_ms) -
                         (double)*pending_out_time_us / 1000.0;
            AcquireSRWLockExclusive(&clock->lock);
            clock->lag_ms = lag;
            if (clock->samples == 0 || lag < clock->baseline_ms)
                clock->baseline_ms = lag;
            clock->sample_ms = arrival;
            clock->samples++;
            ReleaseSRWLockExclusive(&clock->lock);
        }
        *pending_out_time_us = -1;
    }
}

static DWORD WINAPI progress_thread(LPVOID parameter)
{
    ProgressClock *clock = (ProgressClock *)parameter;
    char chunk[1024];
    char line[256];
    size_t length = 0;
    LONGLONG pending = -1;
    DWORD read = 0;

    while (ReadFile(clock->read, chunk, sizeof(chunk), &read, NULL) && read > 0) {
        for (DWORD i = 0; i < read; i++) {
            char c = chunk[i];
            if (c == '\r') continue;
            if (c == '\n') {
                line[length] = '\0';
                progress_handle_line(clock, line, &pending);
                length = 0;
            } else if (length + 1 < sizeof(line)) {
                line[length++] = c;
            }
        }
    }
    return 0;
}

/* Creates the pipe FFmpeg writes -progress to. The write end is returned
   inheritable for exactly one CreateProcess call; the caller closes it. */
static int progress_prepare(ProgressClock *clock, HANDLE *child_write)
{
    SECURITY_ATTRIBUTES security;
    ZeroMemory(&security, sizeof(security));
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;
    ZeroMemory(clock, sizeof(*clock));
    InitializeSRWLock(&clock->lock);
    if (!CreatePipe(&clock->read, child_write, &security, 65536)) {
        clock->read = NULL;
        *child_write = NULL;
        return 0;
    }
    SetHandleInformation(clock->read, HANDLE_FLAG_INHERIT, 0);
    return 1;
}

static void progress_start(ProgressClock *clock)
{
    clock->started_ms = now_ms();
    if (clock->read)
        clock->thread = CreateThread(NULL, 0, progress_thread, clock, 0, NULL);
}

static void progress_close(ProgressClock *clock)
{
    if (clock->thread) {
        /* The thread ends when FFmpeg exits and the pipe breaks. */
        if (WaitForSingleObject(clock->thread, 2000) == WAIT_TIMEOUT) {
            CancelSynchronousIo(clock->thread);
            WaitForSingleObject(clock->thread, 1000);
        }
        CloseHandle(clock->thread);
    }
    if (clock->read) CloseHandle(clock->read);
    clock->thread = NULL;
    clock->read = NULL;
    clock->samples = 0;
}

static int progress_read(ProgressClock *clock, double *lag, double *drift)
{
    int have;
    AcquireSRWLockShared(&clock->lock);
    have = clock->samples > 0;
    if (have) {
        if (lag) *lag = clock->lag_ms;
        if (drift) *drift = clock->lag_ms - clock->baseline_ms;
    }
    ReleaseSRWLockShared(&clock->lock);
    return have;
}

static void timing_snapshot(Recorder *recorder, TimingSnapshot *timing)
{
    ZeroMemory(timing, sizeof(*timing));
    if (!recorder || !recorder->running) return;
    timing->have_video = progress_read(&recorder->video_clock, &timing->video_lag_ms,
                                       &timing->video_drift_ms);
    if (recorder->audio_running)
        timing->have_audio = progress_read(&recorder->audio_clock, NULL,
                                           &timing->audio_drift_ms);
}

static void trim_whitespace(wchar_t *value)
{
    wchar_t *start = value;
    wchar_t *end;
    while (*start == L' ' || *start == L'\t') start++;
    if (start != value) memmove(value, start, (wcslen(start) + 1) * sizeof(wchar_t));
    end = value + wcslen(value);
    while (end > value && (end[-1] == L' ' || end[-1] == L'\t')) end--;
    *end = L'\0';
}

static int parse_hotkey(const wchar_t *text, UINT *modifiers, UINT *virtual_key)
{
    wchar_t buffer[128];
    wchar_t *context = NULL;
    wchar_t *token;
    UINT mods = MOD_NOREPEAT;
    UINT key = 0;

    wcscpy_s(buffer, ARRAY_COUNT(buffer), text && text[0] ? text : L"F8");
    token = wcstok_s(buffer, L"+", &context);
    while (token) {
        int function_key;
        trim_whitespace(token);
        if (_wcsicmp(token, L"Ctrl") == 0 || _wcsicmp(token, L"Control") == 0)
            mods |= MOD_CONTROL;
        else if (_wcsicmp(token, L"Alt") == 0)
            mods |= MOD_ALT;
        else if (_wcsicmp(token, L"Shift") == 0)
            mods |= MOD_SHIFT;
        else if (_wcsicmp(token, L"Win") == 0 || _wcsicmp(token, L"Windows") == 0)
            mods |= MOD_WIN;
        else if (wcslen(token) == 1 && token[0] >= L'A' && token[0] <= L'Z')
            key = (UINT)token[0];
        else if (wcslen(token) == 1 && token[0] >= L'a' && token[0] <= L'z')
            key = (UINT)(token[0] - L'a' + L'A');
        else if (wcslen(token) == 1 && token[0] >= L'0' && token[0] <= L'9')
            key = (UINT)token[0];
        else if ((token[0] == L'F' || token[0] == L'f') &&
                 (function_key = _wtoi(token + 1)) >= 1 && function_key <= 24)
            key = VK_F1 + (UINT)function_key - 1;
        else if (_wcsicmp(token, L"Space") == 0) key = VK_SPACE;
        else if (_wcsicmp(token, L"Insert") == 0) key = VK_INSERT;
        else if (_wcsicmp(token, L"Delete") == 0) key = VK_DELETE;
        else if (_wcsicmp(token, L"Home") == 0) key = VK_HOME;
        else if (_wcsicmp(token, L"End") == 0) key = VK_END;
        else if (_wcsicmp(token, L"PageUp") == 0) key = VK_PRIOR;
        else if (_wcsicmp(token, L"PageDown") == 0) key = VK_NEXT;
        else return 0;
        token = wcstok_s(NULL, L"+", &context);
    }

    if (!key) return 0;
    *modifiers = mods;
    *virtual_key = key;
    return 1;
}

static int process_is_running(const wchar_t *name)
{
    PROCESSENTRY32W entry;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    int found = 0;

    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    ZeroMemory(&entry, sizeof(entry));
    entry.dwSize = sizeof(entry);

    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (_wcsicmp(entry.szExeFile, name) == 0) {
                found = 1;
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return found;
}

static int run_process_wait_logged(wchar_t *command, const wchar_t *working_dir,
                                   DWORD timeout_ms, DWORD *exit_code,
                                   const wchar_t *log_path)
{
    SECURITY_ATTRIBUTES security;
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    HANDLE log_file;
    DWORD wait_result;

    ZeroMemory(&security, sizeof(security));
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;
    log_file = CreateFileW(log_path, GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE,
                           &security, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (log_file == INVALID_HANDLE_VALUE) {
        print_last_error(L"Could not create FFmpeg save log");
        return 0;
    }

    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = log_file;
    startup.hStdError = log_file;

    if (!CreateProcessW(NULL, command, NULL, NULL, TRUE,
                        CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS,
                        NULL, working_dir, &startup, &process)) {
        print_last_error(L"Could not start FFmpeg save process");
        CloseHandle(log_file);
        return 0;
    }

    wait_result = WaitForSingleObject(process.hProcess, timeout_ms);
    if (wait_result == WAIT_TIMEOUT) {
        TerminateProcess(process.hProcess, 2);
        WaitForSingleObject(process.hProcess, 2000);
    }

    if (exit_code) GetExitCodeProcess(process.hProcess, exit_code);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    CloseHandle(log_file);
    return wait_result != WAIT_TIMEOUT;
}

static void clear_segment_pattern(const wchar_t *buffer_dir, const wchar_t *wildcard)
{
    wchar_t pattern[PATH_CAP];
    WIN32_FIND_DATAW data;
    HANDLE search;

    swprintf_s(pattern, ARRAY_COUNT(pattern), L"%ls\\%ls", buffer_dir, wildcard);
    search = FindFirstFileW(pattern, &data);
    if (search == INVALID_HANDLE_VALUE) return;

    do {
        wchar_t path[PATH_CAP];
        if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        swprintf_s(path, ARRAY_COUNT(path), L"%ls\\%ls", buffer_dir, data.cFileName);
        DeleteFileW(path);
    } while (FindNextFileW(search, &data));
    FindClose(search);
}

static void clear_old_segments(const wchar_t *buffer_dir)
{
    clear_segment_pattern(buffer_dir, L"segment_*.mkv");
    clear_segment_pattern(buffer_dir, L"audio_*.mka");
}

/* Stops only the audio helper and audio FFmpeg. The shared job object is left
   open: closing it would also kill the video FFmpeg (KILL_ON_JOB_CLOSE). */
static void stop_audio_chain(Recorder *recorder)
{
    int index;
    if (recorder->audio_process.hProcess) {
        if (WaitForSingleObject(recorder->audio_process.hProcess, 0) == WAIT_TIMEOUT) {
            TerminateProcess(recorder->audio_process.hProcess, 0);
            WaitForSingleObject(recorder->audio_process.hProcess, 2000);
        }
        CloseHandle(recorder->audio_process.hProcess);
    }
    if (recorder->audio_process.hThread)
        CloseHandle(recorder->audio_process.hThread);
    ZeroMemory(&recorder->audio_process, sizeof(recorder->audio_process));
    recorder->audio_running = 0;
    progress_close(&recorder->audio_clock);
    for (index = 0; index < recorder->audio_helper_count; index++) {
        if (recorder->audio_helpers[index].hProcess) {
            if (WaitForSingleObject(recorder->audio_helpers[index].hProcess, 2000) == WAIT_TIMEOUT) {
                TerminateProcess(recorder->audio_helpers[index].hProcess, 0);
                WaitForSingleObject(recorder->audio_helpers[index].hProcess, 2000);
            }
            CloseHandle(recorder->audio_helpers[index].hProcess);
        }
        if (recorder->audio_helpers[index].hThread)
            CloseHandle(recorder->audio_helpers[index].hThread);
    }
    ZeroMemory(recorder->audio_helpers, sizeof(recorder->audio_helpers));
    recorder->audio_helper_count = 0;
}

static void stop_audio_helpers(Recorder *recorder)
{
    if (recorder->job) {
        CloseHandle(recorder->job);
        recorder->job = NULL;
    }
    stop_audio_chain(recorder);
}

static int start_audio_helper(Recorder *recorder, const AppConfig *config,
                              const wchar_t *mode, const wchar_t *device,
                              const wchar_t *pipe_name)
{
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    wchar_t command[COMMAND_CAP];
    DWORD flags = CREATE_NO_WINDOW;

    if (recorder->audio_helper_count >= MAX_AUDIO_HELPERS || !recorder->job)
        return 0;
    if (GetFileAttributesW(config->audio_helper_path) == INVALID_FILE_ATTRIBUTES) {
        wprintf(L"Audio helper is missing: %ls. Recording will continue without this audio source.\n",
                config->audio_helper_path);
        return 0;
    }

    /* Volumes are applied in the helper, so the clip-volume slider changes
       game sound only and the microphone has its own level. With game audio
       turned off but the microphone on, game sound is captured at 0% so the
       loopback stream still provides the steady clock. */
    swprintf_s(command, ARRAY_COUNT(command),
               L"\"%ls\" --mode %ls --device \"%ls\" --pipe \"%ls\" --game-volume %d",
               config->audio_helper_path, mode, device, pipe_name,
               config->capture_game_audio ? config->game_audio_volume : 0);
    if (config->capture_microphone) {
        size_t used = wcslen(command);
        swprintf_s(command + used, ARRAY_COUNT(command) - used,
                   L" --mic \"%ls\" --mic-volume %d",
                   config->microphone_device, config->microphone_volume);
    }
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);
    /* Audio stays at normal priority so a busy game cannot starve FFmpeg's clock. */

    if (!CreateProcessW(NULL, command, NULL, NULL, TRUE, flags,
                        NULL, config->base_dir, &startup, &process)) {
        print_last_error(L"Could not start the Windows audio helper");
        return 0;
    }
    if (!AssignProcessToJobObject(recorder->job, process.hProcess)) {
        print_last_error(L"Could not attach the audio helper to the cleanup job");
        TerminateProcess(process.hProcess, 5);
        WaitForSingleObject(process.hProcess, 2000);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return 0;
    }

    CloseHandle(process.hThread);
    process.hThread = NULL;
    recorder->audio_helpers[recorder->audio_helper_count++] = process;
    return 1;
}

static int start_audio_segment_recorder(Recorder *recorder, const AppConfig *config,
                                        const wchar_t *pipe_name)
{
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    wchar_t command[COMMAND_CAP];
    wchar_t pattern[PATH_CAP];
    wchar_t pipe_path[256];
    int segment_count = audio_wrap_count(config);
    DWORD exit_code = 1;
    ULONGLONG pipe_wait_started;
    HANDLE progress_write = NULL;

    /* ClipShotAudio owns the server side of the named pipe. CreateProcess only
       proves that the helper was launched; it does not mean its managed runtime
       has created the pipe yet. Starting FFmpeg before that point is a race and
       makes FFmpeg exit without ever creating audio_*.mka. */
    swprintf_s(pipe_path, ARRAY_COUNT(pipe_path), L"\\\\.\\pipe\\%ls", pipe_name);
    pipe_wait_started = GetTickCount64();
    while (!WaitNamedPipeW(pipe_path, 100)) {
        DWORD error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_SEM_TIMEOUT &&
            error != ERROR_PIPE_BUSY) {
            wprintf(L"Could not prepare the game-audio pipe (Windows error %lu).\n",
                    error);
            return 0;
        }
        if (GetTickCount64() - pipe_wait_started >= 5000) {
            wprintf(L"Game-audio helper did not create its pipe within five seconds.\n");
            return 0;
        }
        Sleep(25);
    }

    /* The helper now delivers a gap-free stream: one 48 kHz stereo frame per
       1/48000 s of real time, with silence written for any stall. Timestamps
       therefore come straight from the sample count. The previous wall-clock
       timestamps + aresample async (min_hard_comp=10) stretched or squeezed
       audio by up to 1% instead of filling gaps, so audio did not keep time. */
    swprintf_s(pattern, ARRAY_COUNT(pattern), L"%ls\\audio_%%03d.mka", config->buffer_dir);
    swprintf_s(command, ARRAY_COUNT(command),
        L"\"%ls\" -hide_banner -loglevel warning -y -nostats "
        L"-stats_period 1 -progress pipe:1 "
        L"-thread_queue_size 64 "
        L"-f s16le -ar 48000 -ac 2 -i \"\\\\.\\pipe\\%ls\" "
        L"-map 0:a:0 -c:a pcm_s16le -flush_packets 1 -f segment "
        L"-segment_time %d -segment_wrap %d -segment_format matroska "
        L"-segment_format_options cluster_time_limit=100 -reset_timestamps 1 \"%ls\"",
        config->ffmpeg_path, pipe_name, AUDIO_SEGMENT_SECONDS, segment_count, pattern);

    if (!progress_prepare(&recorder->audio_clock, &progress_write))
        wprintf(L"Audio timing telemetry is unavailable (pipe creation failed).\n");

    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = progress_write ? progress_write : GetStdHandle(STD_OUTPUT_HANDLE);
    startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);

    if (!CreateProcessW(NULL, command, NULL, NULL, TRUE, CREATE_NO_WINDOW,
                        NULL, config->base_dir, &startup, &process)) {
        print_last_error(L"Could not start the independent audio recorder");
        if (progress_write) CloseHandle(progress_write);
        progress_close(&recorder->audio_clock);
        return 0;
    }
    if (progress_write) CloseHandle(progress_write);
    progress_start(&recorder->audio_clock);
    if (!AssignProcessToJobObject(recorder->job, process.hProcess)) {
        print_last_error(L"Could not attach the audio recorder to the cleanup job");
        TerminateProcess(process.hProcess, 5);
        WaitForSingleObject(process.hProcess, 2000);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        progress_close(&recorder->audio_clock);
        return 0;
    }

    /* Give FFmpeg enough time to open the now-existing pipe and reject invalid
       audio arguments. A zero-time check previously reported success while the
       process was still on its way to an immediate startup failure. */
    if (WaitForSingleObject(process.hProcess, 750) == WAIT_OBJECT_0) {
        GetExitCodeProcess(process.hProcess, &exit_code);
        wprintf(L"Independent audio recorder could not start (FFmpeg exit code %lu).\n",
                exit_code);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        progress_close(&recorder->audio_clock);
        return 0;
    }

    CloseHandle(process.hThread);
    process.hThread = NULL;
    recorder->audio_process = process;
    recorder->audio_running = 1;
    return 1;
}

static int start_audio_chain(Recorder *recorder, const AppConfig *config)
{
    wchar_t pipe_name[128];
    /* A fresh pipe name per attempt avoids colliding with a pipe instance
       that a just-exited helper has not fully released yet. */
    swprintf_s(pipe_name, ARRAY_COUNT(pipe_name), L"ClipShotGameAudio_%lu_%d",
               GetCurrentProcessId(), recorder->audio_restarts);
    if (start_audio_helper(recorder, config, L"loopback",
                           config->game_audio_device, pipe_name) &&
        start_audio_segment_recorder(recorder, config, pipe_name))
        return 1;
    stop_audio_chain(recorder);
    return 0;
}

/* The audio chain ends when Windows invalidates the capture device (headset
   unplugged, default device changed, driver reset). Previously that silently
   left the rest of the session without audio files. Restart it instead. */
static void recorder_poll_audio(Recorder *recorder, const AppConfig *config)
{
    DWORD exit_code = 0;
    ULONGLONG now = GetTickCount64();

    if (!recorder->running || !recorder->audio_wanted) return;

    if (recorder->audio_running && recorder->audio_process.hProcess) {
        if (WaitForSingleObject(recorder->audio_process.hProcess, 0) != WAIT_OBJECT_0)
            return;
        GetExitCodeProcess(recorder->audio_process.hProcess, &exit_code);
        wprintf(L"Independent audio recorder stopped unexpectedly (FFmpeg exit code %lu). Video capture remains active; restarting audio.\n",
                exit_code);
        stop_audio_chain(recorder);
        recorder->audio_failed_tick = now;
        return;
    }

    if (recorder->audio_failed_tick &&
        now - recorder->audio_failed_tick < AUDIO_RESTART_COOLDOWN_MS)
        return;
    recorder->audio_restarts++;
    /* Old audio files belong to a different sample timeline; remove them so a
       clip never stitches two unrelated audio timelines together. Saved clips
       spanning the restart get silence for the missing part instead. */
    clear_segment_pattern(config->buffer_dir, L"audio_*.mka");
    if (start_audio_chain(recorder, config)) {
        recorder->audio_failed_tick = 0;
        wprintf(L"Audio capture restarted (attempt %d).\n", recorder->audio_restarts);
    } else {
        recorder->audio_failed_tick = now;
        wprintf(L"Audio capture could not restart; will retry in %llu seconds.\n",
                AUDIO_RESTART_COOLDOWN_MS / 1000ULL);
    }
}

static int recorder_start_backend(Recorder *recorder, const AppConfig *config, int use_dxgi)
{
    SECURITY_ATTRIBUTES security;
    HANDLE stdin_read = NULL;
    wchar_t command[COMMAND_CAP];
    wchar_t pattern[PATH_CAP];
    wchar_t capture_args[1536] = L"";
    wchar_t video_filter[512] = L"";
    wchar_t thread_limit[128] = L"";
    int game_audio_recording = 0;
    int segment_count = video_wrap_count(config);
    int keyframe_interval = config->framerate * 2;
    int capture_width = GetSystemMetrics(SM_CXSCREEN);
    int capture_height = GetSystemMetrics(SM_CYSCREEN);
    HANDLE progress_write = NULL;
    STARTUPINFOW startup;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION job_info;

    if (recorder->running) return 1;
    clear_old_segments(config->buffer_dir);
    swprintf_s(pattern, ARRAY_COUNT(pattern), L"%ls\\segment_%%03d.mkv", config->buffer_dir);

    /* A failed preferred backend can leave an empty job object behind before
       the next fallback attempt. Close it before constructing the new job. */
    if (recorder->job) {
        CloseHandle(recorder->job);
        recorder->job = NULL;
    }
    recorder->job = CreateJobObjectW(NULL, NULL);
    if (recorder->job) {
        ZeroMemory(&job_info, sizeof(job_info));
        job_info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(recorder->job, JobObjectExtendedLimitInformation,
                                     &job_info, sizeof(job_info))) {
            print_last_error(L"Could not configure FFmpeg cleanup job");
            CloseHandle(recorder->job);
            recorder->job = NULL;
        }
    }

    /* Audio is deliberately recorded by a separate FFmpeg process. Keeping it
       out of the live video mux removes audio timing from the proven video path. */
    recorder->audio_wanted = config->capture_game_audio || config->capture_microphone;
    recorder->audio_failed_tick = 0;
    if (recorder->audio_wanted && start_audio_chain(recorder, config))
        game_audio_recording = 1;
    else if (recorder->audio_wanted)
        recorder->audio_failed_tick = GetTickCount64();

    if (use_dxgi) {
        swprintf_s(capture_args, ARRAY_COUNT(capture_args),
                   L"-thread_queue_size %d -f lavfi -i "
                   L"\"ddagrab=output_idx=0:draw_mouse=0:framerate=%d:dup_frames=1\" ",
                   CAPTURE_QUEUE_FRAMES, config->framerate);
    } else {
        swprintf_s(capture_args, ARRAY_COUNT(capture_args),
                   L"-fflags +genpts -use_wallclock_as_timestamps 1 "
                   L"-thread_queue_size %d -f gdigrab -draw_mouse 0 -framerate %d "
                   L"-offset_x 0 -offset_y 0 -video_size %dx%d -i desktop ",
                   CAPTURE_QUEUE_FRAMES, config->framerate, capture_width, capture_height);
    }

    /* Timing fix (progressive delay): the previous graph rewrote every
       timestamp as setpts=N/(fps*TB), i.e. "frame number / fps". Any frame
       the capture or encoder could not deliver was then simply missing from
       the timeline, so recorded time ran slower than real time: 0.2% loss is
       about 2.4 seconds per 20 minutes. The fps filter keeps the capture
       timestamps and produces constant frame rate by duplicating or dropping
       frames against real time instead. It runs before hwdownload so frames
       it drops are never copied off the GPU. */
    if (use_dxgi && config->output_height > 0) {
        swprintf_s(video_filter, ARRAY_COUNT(video_filter),
                   L"-vf \"fps=%d,hwdownload,format=bgra,"
                   L"scale=-2:min(ih\\,%d):flags=fast_bilinear,"
                   L"format=yuv420p\" -fps_mode passthrough ",
                   config->framerate, config->output_height);
    } else if (use_dxgi) {
        swprintf_s(video_filter, ARRAY_COUNT(video_filter),
                   L"-vf \"fps=%d,hwdownload,format=bgra,"
                   L"format=yuv420p\" -fps_mode passthrough ",
                   config->framerate);
    } else if (config->output_height > 0) {
        swprintf_s(video_filter, ARRAY_COUNT(video_filter),
                   L"-vf \"fps=%d,scale=-2:min(ih\\,%d):flags=fast_bilinear\" "
                   L"-fps_mode passthrough ",
                   config->framerate, config->output_height);
    } else {
        swprintf_s(video_filter, ARRAY_COUNT(video_filter),
                   L"-vf \"fps=%d\" -fps_mode passthrough ",
                   config->framerate);
    }
    if (config->prioritize_game && _wcsicmp(config->video_encoder, L"libx264") == 0) {
        wcscpy_s(thread_limit, ARRAY_COUNT(thread_limit), L"-filter_threads 1 ");
        wcscat_s(thread_limit, ARRAY_COUNT(thread_limit), L"-threads 2 ");
    }

    swprintf_s(command, ARRAY_COUNT(command),
        L"\"%ls\" -hide_banner -loglevel warning -y -nostats "
        L"-stats_period 1 -progress pipe:1 "
        L"%ls-map 0:v:0 -an %ls-c:v %ls %ls %ls-bf 0 -g %d "
        L"-force_key_frames \"expr:gte(t,n_forced*%d)\" "
        L"-flush_packets 1 -f segment -segment_time %d -segment_wrap %d "
        L"-segment_format_options cluster_time_limit=100 "
        L"-reset_timestamps 1 \"%ls\"",
        config->ffmpeg_path, capture_args, video_filter,
        config->video_encoder, config->video_options, thread_limit, keyframe_interval,
        config->segment_seconds, config->segment_seconds, segment_count, pattern);

    ZeroMemory(&security, sizeof(security));
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;
    if (!CreatePipe(&stdin_read, &recorder->stdin_write, &security, 0)) {
        stop_audio_helpers(recorder);
        return 0;
    }
    SetHandleInformation(recorder->stdin_write, HANDLE_FLAG_INHERIT, 0);
    if (!progress_prepare(&recorder->video_clock, &progress_write))
        wprintf(L"Video timing telemetry is unavailable (pipe creation failed).\n");

    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&recorder->process, sizeof(recorder->process));
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = stdin_read;
    startup.hStdOutput = progress_write ? progress_write : GetStdHandle(STD_OUTPUT_HANDLE);
    startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);

    int lower_priority = config->prioritize_game &&
                         _wcsicmp(config->video_encoder, L"libx264") == 0;
    DWORD creation_flags = CREATE_NO_WINDOW | CREATE_SUSPENDED;
    if (lower_priority) creation_flags |= BELOW_NORMAL_PRIORITY_CLASS;

    if (!CreateProcessW(NULL, command, NULL, NULL, TRUE, creation_flags,
                        NULL, config->base_dir, &startup, &recorder->process)) {
        print_last_error(L"Could not start FFmpeg");
        CloseHandle(stdin_read);
        if (progress_write) CloseHandle(progress_write);
        progress_close(&recorder->video_clock);
        CloseHandle(recorder->stdin_write);
        recorder->stdin_write = NULL;
        stop_audio_helpers(recorder);
        return 0;
    }

    CloseHandle(stdin_read);
    if (progress_write) CloseHandle(progress_write);
    if (recorder->job && !AssignProcessToJobObject(recorder->job, recorder->process.hProcess)) {
        print_last_error(L"Could not attach FFmpeg to the cleanup job");
        TerminateProcess(recorder->process.hProcess, 5);
        WaitForSingleObject(recorder->process.hProcess, 2000);
        CloseHandle(recorder->stdin_write);
        CloseHandle(recorder->process.hThread);
        CloseHandle(recorder->process.hProcess);
        progress_close(&recorder->video_clock);
        stop_audio_helpers(recorder);
        ZeroMemory(&recorder->process, sizeof(recorder->process));
        recorder->stdin_write = NULL;
        return 0;
    }
    if (ResumeThread(recorder->process.hThread) == (DWORD)-1) {
        print_last_error(L"Could not start FFmpeg capture thread");
        TerminateProcess(recorder->process.hProcess, 4);
        WaitForSingleObject(recorder->process.hProcess, 2000);
        CloseHandle(recorder->stdin_write);
        CloseHandle(recorder->process.hThread);
        CloseHandle(recorder->process.hProcess);
        progress_close(&recorder->video_clock);
        stop_audio_helpers(recorder);
        ZeroMemory(recorder, sizeof(*recorder));
        return 0;
    }
    progress_start(&recorder->video_clock);

    if (WaitForSingleObject(recorder->process.hProcess, 1200) == WAIT_OBJECT_0) {
        DWORD startup_exit_code = 1;
        GetExitCodeProcess(recorder->process.hProcess, &startup_exit_code);
        wprintf(L"%ls capture could not start (FFmpeg exit code %lu).\n",
                use_dxgi ? L"DXGI" : L"GDI", startup_exit_code);
        CloseHandle(recorder->stdin_write);
        CloseHandle(recorder->process.hThread);
        CloseHandle(recorder->process.hProcess);
        recorder->stdin_write = NULL;
        progress_close(&recorder->video_clock);
        stop_audio_helpers(recorder);
        ZeroMemory(&recorder->process, sizeof(recorder->process));
        return 0;
    }
    recorder->running = 1;
    recorder->last_timing_report_ms = now_ms();
    if (game_audio_recording)
        wprintf(L"Audio capture: %ls%ls recording.\n",
                config->capture_game_audio ? L"game sound" : L"",
                config->capture_microphone
                    ? (config->capture_game_audio ? L" and microphone" : L"microphone")
                    : L"");
    else
        wprintf(L"Audio capture: disabled or no configured audio source could start.\n");
    wprintf(L"Capture started with %ls: primary monitor %dx%d, %d-second buffer, %d-second segments at %d FPS, output height %d, %ls priority.\n",
            use_dxgi ? L"DXGI Desktop Duplication" : L"GDI fallback",
            capture_width, capture_height, config->buffer_seconds, config->segment_seconds,
            config->framerate, config->output_height,
            lower_priority ? L"below-normal" : L"normal");
    wprintf(L"RUNTIME|build=%ls|backend=%ls|encoder=%ls|fps=%d|segment=%d|buffer=%d|output_height=%d|pipeline=%ls\n",
            ENGINE_BUILD, use_dxgi ? L"DXGI" : L"GDI", config->video_encoder,
            config->framerate, config->segment_seconds, config->buffer_seconds,
            config->output_height, ENGINE_PIPELINE);
    fflush(stdout);
    return 1;
}

static int recorder_start(Recorder *recorder, const AppConfig *config)
{
    int try_dxgi = _wcsicmp(config->capture_backend, L"gdi") != 0;
    if (try_dxgi && recorder_start_backend(recorder, config, 1)) return 1;
    if (try_dxgi)
        wprintf(L"DXGI Desktop Duplication was unavailable; trying the GDI compatibility fallback.\n");
    return recorder_start_backend(recorder, config, 0);
}

static void recorder_stop(Recorder *recorder)
{
    DWORD written;
    if (!recorder->running) return;

    WriteFile(recorder->stdin_write, "q\n", 2, &written, NULL);
    if (WaitForSingleObject(recorder->process.hProcess, 7000) == WAIT_TIMEOUT) {
        TerminateProcess(recorder->process.hProcess, 3);
        WaitForSingleObject(recorder->process.hProcess, 2000);
    }

    CloseHandle(recorder->stdin_write);
    CloseHandle(recorder->process.hThread);
    CloseHandle(recorder->process.hProcess);
    progress_close(&recorder->video_clock);
    stop_audio_helpers(recorder);
    ZeroMemory(recorder, sizeof(*recorder));
    g_capture_stopped_ms = now_ms();
    wprintf(L"Capture stopped.\n");
}

/* Periodic proof that recording keeps real time. "drift" is how much further
   behind real time each recorder is now than at its best point this session;
   with the timestamp fixes it should stay near zero for the whole session. */
static void recorder_report_timing(Recorder *recorder)
{
    TimingSnapshot timing;
    double now = now_ms();
    if (!recorder->running || now - recorder->last_timing_report_ms < TIMING_REPORT_MS)
        return;
    recorder->last_timing_report_ms = now;
    timing_snapshot(recorder, &timing);
    if (!timing.have_video) return;
    if (timing.have_audio)
        wprintf(L"Timing check: video %.0f ms behind live, drift %+.0f ms; audio drift %+.0f ms; A/V difference %+.0f ms.\n",
                timing.video_lag_ms, timing.video_drift_ms, timing.audio_drift_ms,
                timing.video_drift_ms - timing.audio_drift_ms);
    else
        wprintf(L"Timing check: video %.0f ms behind live, drift %+.0f ms.\n",
                timing.video_lag_ms, timing.video_drift_ms);
    if (timing.video_drift_ms > DRIFT_WARNING_MS)
        wprintf(L"Warning: the video encoder is falling behind real time. Try Optimize for gameplay, 30 FPS, or 720p.\n");
}

static int compare_segments(const void *left, const void *right)
{
    const Segment *a = (const Segment *)left;
    const Segment *b = (const Segment *)right;
    return CompareFileTime(&a->modified, &b->modified);
}

static char *read_entire_file(const wchar_t *path);

/* Directory listings (FindFirstFile) can report stale size/time for a file
   that is still open for writing, which could sort the live segment before
   the one it replaced. Query each file directly for current values. */
static size_t list_segments_matching(const wchar_t *buffer_dir, const wchar_t *wildcard,
                                     Segment *segments, size_t capacity)
{
    wchar_t pattern[PATH_CAP];
    WIN32_FIND_DATAW data;
    HANDLE search;
    size_t count = 0;

    swprintf_s(pattern, ARRAY_COUNT(pattern), L"%ls\\%ls", buffer_dir, wildcard);
    search = FindFirstFileW(pattern, &data);
    if (search == INVALID_HANDLE_VALUE) return 0;

    do {
        WIN32_FILE_ATTRIBUTE_DATA fresh;
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || count >= capacity) continue;
        swprintf_s(segments[count].path, ARRAY_COUNT(segments[count].path),
                   L"%ls\\%ls", buffer_dir, data.cFileName);
        segments[count].modified = data.ftLastWriteTime;
        segments[count].size = ((ULONGLONG)data.nFileSizeHigh << 32) |
                               data.nFileSizeLow;
        if (GetFileAttributesExW(segments[count].path, GetFileExInfoStandard, &fresh)) {
            segments[count].modified = fresh.ftLastWriteTime;
            segments[count].size = ((ULONGLONG)fresh.nFileSizeHigh << 32) |
                                   fresh.nFileSizeLow;
        }
        count++;
    } while (FindNextFileW(search, &data));
    FindClose(search);
    qsort(segments, count, sizeof(segments[0]), compare_segments);
    return count;
}

typedef struct {
    Segment *items;
    size_t count;
    ULONGLONG before[MAX_SEGMENTS];
    int changes[MAX_SEGMENTS];
    FILETIME observed[MAX_SEGMENTS];
} GrowthProbe;

static ULONGLONG current_file_size(const Segment *segment, ULONGLONG fallback)
{
    WIN32_FILE_ATTRIBUTE_DATA data;
    if (!GetFileAttributesExW(segment->path, GetFileExInfoStandard, &data)) return fallback;
    return ((ULONGLONG)data.nFileSizeHigh << 32) | data.nFileSizeLow;
}

static void growth_before(GrowthProbe *probe)
{
    for (size_t i = 0; i < probe->count; i++)
        probe->before[i] = current_file_size(&probe->items[i], probe->items[i].size);
}

static void growth_after(GrowthProbe *probe)
{
    for (size_t i = 0; i < probe->count; i++) {
        WIN32_FILE_ATTRIBUTE_DATA data;
        ULONGLONG after;
        if (!GetFileAttributesExW(probe->items[i].path, GetFileExInfoStandard, &data))
            continue;
        after = ((ULONGLONG)data.nFileSizeHigh << 32) | data.nFileSizeLow;
        if (after != probe->before[i]) {
            probe->changes[i]++;
            probe->observed[i] = data.ftLastWriteTime;
        }
    }
}

static size_t growth_pick(const GrowthProbe *probe)
{
    size_t candidate = SIZE_MAX;
    int candidate_changes = 0;
    for (size_t i = 0; i < probe->count; i++) {
        if (probe->changes[i] > candidate_changes ||
            (probe->changes[i] == candidate_changes && probe->changes[i] > 0 &&
             candidate != SIZE_MAX &&
             CompareFileTime(&probe->observed[i], &probe->observed[candidate]) > 0)) {
            candidate = i;
            candidate_changes = probe->changes[i];
        }
    }
    return candidate_changes > 0 ? candidate : SIZE_MAX;
}

/* Finds the file each live FFmpeg process is writing by watching which one
   grows. Video and audio are sampled in the same loop (about 0.6 s total). */
static void detect_active_segments(Segment *video, size_t video_count, size_t *video_active,
                                   Segment *audio, size_t audio_count, size_t *audio_active)
{
    GrowthProbe *probes = (GrowthProbe *)calloc(2, sizeof(GrowthProbe));
    *video_active = SIZE_MAX;
    *audio_active = SIZE_MAX;
    if (!probes) return;
    probes[0].items = video;
    probes[0].count = video_count > MAX_SEGMENTS ? 0 : video_count;
    probes[1].items = audio;
    probes[1].count = audio_count > MAX_SEGMENTS ? 0 : audio_count;
    for (int attempt = 0; attempt < 6; attempt++) {
        growth_before(&probes[0]);
        growth_before(&probes[1]);
        Sleep(100);
        growth_after(&probes[0]);
        growth_after(&probes[1]);
    }
    *video_active = growth_pick(&probes[0]);
    *audio_active = growth_pick(&probes[1]);
    free(probes);
}

static void move_segment_to_end(Segment *segments, size_t count, size_t index)
{
    Segment active;
    if (index >= count || index + 1 == count) return;
    active = segments[index];
    memmove(&segments[index], &segments[index + 1],
            (count - index - 1) * sizeof(segments[0]));
    segments[count - 1] = active;
}

static int segment_number(const wchar_t *path)
{
    const wchar_t *name = wcsrchr(path, L'\\');
    const wchar_t *digits;
    name = name ? name + 1 : path;
    digits = wcsrchr(name, L'_');
    if (!digits || !iswdigit(digits[1])) return -1;
    return _wtoi(digits + 1);
}

/* FFmpeg's segment muxer writes slots 0..wrap-1 in a ring, so once the live
   slot is known the exact order is live+1, live+2, ... live. This does not
   depend on file timestamps at all. Returns the number of ordered files, with
   the live one last. Without a live slot, timestamp order is kept. */
static size_t order_ring(Segment *segments, size_t count, size_t active, int wrap)
{
    int *slot;
    Segment *ordered;
    size_t ordered_count = 0;
    int active_number;

    if (active == SIZE_MAX || count == 0 || wrap <= 0) return count;
    active_number = segment_number(segments[active].path);
    slot = (int *)malloc((size_t)wrap * sizeof(int));
    ordered = (Segment *)calloc(count, sizeof(Segment));
    if (active_number < 0 || active_number >= wrap || !slot || !ordered) {
        free(slot);
        free(ordered);
        move_segment_to_end(segments, count, active);
        return count;
    }
    for (int i = 0; i < wrap; i++) slot[i] = -1;
    for (size_t i = 0; i < count; i++) {
        int number = segment_number(segments[i].path);
        if (number >= 0 && number < wrap) slot[number] = (int)i;
    }
    for (int step = 1; step <= wrap; step++) {
        int number = (active_number + step) % wrap;
        if (slot[number] >= 0) ordered[ordered_count++] = segments[slot[number]];
    }
    memcpy(segments, ordered, ordered_count * sizeof(Segment));
    free(slot);
    free(ordered);
    return ordered_count;
}

static void make_timestamp(wchar_t *out, size_t out_count)
{
    SYSTEMTIME now;
    GetLocalTime(&now);
    swprintf_s(out, out_count, L"%04u-%02u-%02u_%02u-%02u-%02u-%03u",
               now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute,
               now.wSecond, now.wMilliseconds);
}

/* Reads a finished media file's duration from FFmpeg's own report
   ("Duration: HH:MM:SS.ss"), so no separate ffprobe.exe is required. */
static int probe_duration(const AppConfig *config, const wchar_t *directory,
                          const wchar_t *file, double *seconds)
{
    wchar_t command[COMMAND_CAP];
    wchar_t log_path[PATH_CAP];
    DWORD exit_code = 0;
    char *text;
    const char *found;
    int hours = 0, minutes = 0, ok = 0;
    double secs = 0.0;

    swprintf_s(log_path, ARRAY_COUNT(log_path), L"%ls\\probe.log", directory);
    swprintf_s(command, ARRAY_COUNT(command), L"\"%ls\" -hide_banner -i \"%ls\"",
               config->ffmpeg_path, file);
    if (!run_process_wait_logged(command, directory, 30000, &exit_code, log_path))
        return 0;
    text = read_entire_file(log_path);
    if (!text) return 0;
    found = strstr(text, "Duration: ");
    if (found && sscanf(found + 10, "%d:%d:%lf", &hours, &minutes, &secs) == 3) {
        *seconds = hours * 3600.0 + minutes * 60.0 + secs;
        ok = *seconds > 0.0;
    }
    free(text);
    return ok;
}

static int copy_part(const wchar_t *source, const wchar_t *snapshot,
                     const wchar_t *prefix, const wchar_t *extension, size_t index)
{
    wchar_t destination[PATH_CAP];
    swprintf_s(destination, ARRAY_COUNT(destination), L"%ls\\%ls_%03zu.%ls",
               snapshot, prefix, index, extension);
    if (CopyFileW(source, destination, FALSE)) return 1;
    DeleteFileW(destination);
    return 0;
}

static int write_concat(const wchar_t *snapshot, const wchar_t *list_name,
                        const char *prefix, const char *extension, size_t count)
{
    wchar_t path[PATH_CAP];
    FILE *file;
    swprintf_s(path, ARRAY_COUNT(path), L"%ls\\%ls", snapshot, list_name);
    file = _wfopen(path, L"wb");
    if (!file) return 0;
    for (size_t i = 0; i < count; i++)
        fprintf(file, "file '%s_%03zu.%s'\n", prefix, i, extension);
    fclose(file);
    return 1;
}

static void remove_snapshot(const wchar_t *directory, size_t part_count,
                            size_t audio_part_count)
{
    static const wchar_t *const files[] = {
        L"concat.txt", L"audio_concat.txt", L"joined.mkv", L"trimmed.mkv",
        L"joined_audio.mka", L"final.mp4", L"save_ffmpeg.log", L"probe.log"
    };
    wchar_t path[PATH_CAP];
    for (size_t i = 0; i < part_count; i++) {
        swprintf_s(path, ARRAY_COUNT(path), L"%ls\\part_%03zu.mkv", directory, i);
        DeleteFileW(path);
    }
    for (size_t i = 0; i < audio_part_count; i++) {
        swprintf_s(path, ARRAY_COUNT(path), L"%ls\\audio_part_%03zu.mka", directory, i);
        DeleteFileW(path);
    }
    for (size_t i = 0; i < ARRAY_COUNT(files); i++) {
        swprintf_s(path, ARRAY_COUNT(path), L"%ls\\%ls", directory, files[i]);
        DeleteFileW(path);
    }
    RemoveDirectoryW(directory);
}

static size_t segments_needed(double seconds, int segment_seconds)
{
    if (seconds < 1.0) seconds = 1.0;
    return (size_t)((seconds + segment_seconds - 0.001) / segment_seconds);
}

/* Saves a clip from the rolling buffers.

   target_end_ms is 0 for "now" (manual clips) or the QPC time the clip should
   end at (automatic clips: match end + post-match seconds).

   Synchronization model:
   - Video timestamps follow real time (fps filter) and audio is gap-free at
     48 kHz, so inside each buffer, media time == real time.
   - The two live files are copied back to back and the copy times recorded,
     so the real-time position of each buffer's tail is known.
   - The video is cut first (stream copy starts on a keyframe, so its exact
     length is measured afterwards), then audio is cut to cover exactly that
     video span. Previously both were cut with -sseof and aligned at their
     tails, which assumed both tails were "now" and ignored the keyframe
     pre-roll that stream-copy cutting adds (0-2 s of audio offset). */
static int save_clip(const AppConfig *config, Recorder *recorder, const wchar_t *category,
                     double target_end_ms, wchar_t *saved_path, size_t saved_count)
{
    int running = recorder && recorder->running;
    Segment *video = (Segment *)calloc(MAX_SEGMENTS, sizeof(*video));
    Segment *audio = (Segment *)calloc(MAX_SEGMENTS, sizeof(*audio));
    size_t video_count, audio_count = 0;
    size_t video_active = SIZE_MAX, audio_active = SIZE_MAX;
    size_t video_wanted, audio_wanted = 0, base_wanted;
    int video_live, audio_live;
    TimingSnapshot timing;
    double skip_estimate = 0.0, need_seconds;
    double video_tail_ms = 0.0, audio_tail_ms = 0.0;
    double joined_seconds = 0.0, video_seconds = 0.0, audio_seconds = 0.0;
    double skip = 0.0, start_v, end_v, tail_gap = 0.0;
    wchar_t timestamp[128];
    wchar_t snapshot[PATH_CAP];
    wchar_t path[PATH_CAP];
    wchar_t log_path[PATH_CAP];
    wchar_t category_dir[PATH_CAP];
    wchar_t command[COMMAND_CAP];
    const wchar_t *video_file = L"joined.mkv";
    DWORD exit_code = 1;
    int has_audio = 0, tails_known = 0;

    if (!video || !audio) {
        wprintf(L"Could not allocate memory for the clip segment list.\n");
        free(video);
        free(audio);
        return 0;
    }

    timing_snapshot(recorder, &timing);
    video_count = list_segments_matching(config->buffer_dir, L"segment_*.mkv",
                                         video, MAX_SEGMENTS);
    if (video_count < 1) {
        wprintf(L"No rolling video segment is available yet.\n");
        free(video);
        free(audio);
        return 0;
    }
    if (config->capture_game_audio || config->capture_microphone)
        audio_count = list_segments_matching(config->buffer_dir, L"audio_*.mka",
                                             audio, MAX_SEGMENTS);

    if (running) {
        detect_active_segments(video, video_count, &video_active,
                               audio, audio_count, &audio_active);
        if (video_active == SIZE_MAX)
            wprintf(L"Could not observe live video segment growth; using file times.\n");
    }
    video_count = order_ring(video, video_count, video_active, video_wrap_count(config));
    audio_count = order_ring(audio, audio_count, audio_active, audio_wrap_count(config));
    video_live = video_active != SIZE_MAX;
    audio_live = audio_active != SIZE_MAX;

    if (target_end_ms > 0.0) {
        skip_estimate = (now_ms() - target_end_ms) / 1000.0;
        if (skip_estimate < 0.0) skip_estimate = 0.0;
    }
    need_seconds = config->clip_seconds + skip_estimate + KEYFRAME_MARGIN_SECONDS;
    base_wanted = segments_needed(need_seconds, config->segment_seconds);
    video_wanted = base_wanted + (video_live ? 1 : 0);
    if (video_wanted > video_count) video_wanted = video_count;
    if (audio_count > 0) {
        audio_wanted = segments_needed(need_seconds + 2.0, AUDIO_SEGMENT_SECONDS) +
                       (audio_live ? 1 : 0);
        if (audio_wanted > audio_count) audio_wanted = audio_count;
    }

    make_timestamp(timestamp, ARRAY_COUNT(timestamp));
    join_path(category_dir, ARRAY_COUNT(category_dir), config->clips_dir, category);
    ensure_directory(category_dir);
    swprintf_s(snapshot, ARRAY_COUNT(snapshot), L"%ls\\snapshot_%ls", config->work_dir, timestamp);
    ensure_directory(snapshot);
    swprintf_s(log_path, ARRAY_COUNT(log_path), L"%ls\\save_ffmpeg.log", snapshot);

    /* Copy both live files first and back to back: their tails then describe
       nearly the same instant, and the exact gap is measured. */
    if (audio_live && audio_wanted > 0) {
        if (copy_part(audio[audio_count - 1].path, snapshot, L"audio_part", L"mka",
                      audio_wanted - 1)) {
            audio_tail_ms = now_ms();
        } else {
            audio_live = 0;
            audio_count--;
            audio_wanted = audio_wanted - 1 > audio_count ? audio_count : audio_wanted - 1;
        }
    }
    if (video_live) {
        if (copy_part(video[video_count - 1].path, snapshot, L"part", L"mkv",
                      video_wanted - 1)) {
            video_tail_ms = now_ms();
            wprintf(L"Included a safe snapshot of the active rolling segment.\n");
        } else {
            video_live = 0;
            video_count--;
            video_wanted = base_wanted > video_count ? video_count : base_wanted;
            wprintf(L"The active segment was busy; saving completed segments instead.\n");
        }
    }
    if (video_wanted == 0) {
        wprintf(L"No completed segment was available.\n");
        remove_snapshot(snapshot, 0, audio_wanted);
        free(video);
        free(audio);
        return 0;
    }
    if (!running && g_capture_stopped_ms > 0.0) {
        /* Capture already stopped (Fortnite closed): both buffers ended then. */
        video_tail_ms = g_capture_stopped_ms;
        audio_tail_ms = g_capture_stopped_ms;
    }
    tails_known = video_tail_ms > 0.0 && audio_tail_ms > 0.0;

    wprintf(L"Saving the latest %zu video segment(s) and %zu audio segment(s).\n",
            video_wanted, audio_wanted);
    for (size_t i = 0; i < video_wanted; i++) {
        if (video_live && i == video_wanted - 1) continue;
        if (!copy_part(video[video_count - video_wanted + i].path, snapshot,
                       L"part", L"mkv", i)) {
            print_last_error(L"Could not snapshot a buffer segment");
            remove_snapshot(snapshot, video_wanted, audio_wanted);
            free(video);
            free(audio);
            return 0;
        }
    }
    for (size_t i = 0; i < audio_wanted; i++) {
        if (audio_live && i == audio_wanted - 1) continue;
        if (!copy_part(audio[audio_count - audio_wanted + i].path, snapshot,
                       L"audio_part", L"mka", i)) {
            print_last_error(L"Could not snapshot an audio buffer segment");
            audio_wanted = 0;
            break;
        }
    }
    free(video);
    free(audio);

    if (!write_concat(snapshot, L"concat.txt", "part", "mkv", video_wanted)) {
        remove_snapshot(snapshot, video_wanted, audio_wanted);
        return 0;
    }

    swprintf_s(command, ARRAY_COUNT(command),
               L"\"%ls\" -hide_banner -loglevel warning -y -fflags +genpts "
               L"-f concat -safe 0 -i concat.txt -map 0:v:0 -an "
               L"-c:v copy -avoid_negative_ts make_zero joined.mkv", config->ffmpeg_path);
    wprintf(L"Joining video buffer segments with FFmpeg.\n");
    if (!run_process_wait_logged(command, snapshot, 60000, &exit_code, log_path) || exit_code != 0) {
        wprintf(L"FFmpeg could not join the video buffer segments. Details: %ls\n", log_path);
        wprintf(L"The failed snapshot was kept for troubleshooting: %ls\n", snapshot);
        return 0;
    }
    swprintf_s(path, ARRAY_COUNT(path), L"%ls\\joined.mkv", snapshot);
    if (!probe_duration(config, snapshot, path, &joined_seconds)) joined_seconds = 0.0;

    if (audio_wanted > 0 && write_concat(snapshot, L"audio_concat.txt", "audio_part", "mka",
                                         audio_wanted)) {
        swprintf_s(command, ARRAY_COUNT(command),
                   L"\"%ls\" -hide_banner -loglevel warning -y -fflags +genpts "
                   L"-f concat -safe 0 -i audio_concat.txt -map 0:a:0 "
                   L"-af \"asetpts=N/SR/TB\" -c:a pcm_s16le "
                   L"joined_audio.mka", config->ffmpeg_path);
        wprintf(L"Joining independent audio buffer segments with FFmpeg.\n");
        swprintf_s(path, ARRAY_COUNT(path), L"%ls\\joined_audio.mka", snapshot);
        if (run_process_wait_logged(command, snapshot, 60000, &exit_code, log_path) &&
            exit_code == 0 && probe_duration(config, snapshot, path, &audio_seconds))
            has_audio = 1;
        else
            wprintf(L"Independent audio assembly failed; saving this clip as video only. Details: %ls\n",
                    log_path);
    } else if (config->capture_game_audio || config->capture_microphone) {
        wprintf(L"No independent audio segment was available; saving video only.\n");
    }

    /* How much of the buffer tail lies after the requested clip end. */
    if (target_end_ms > 0.0) {
        if (video_tail_ms > 0.0)
            skip = (video_tail_ms - target_end_ms) / 1000.0 -
                   (timing.have_video ? timing.video_drift_ms / 1000.0 : 0.0);
        else
            skip = skip_estimate;
        if (skip < 0.0) skip = 0.0;
    }

    if (joined_seconds > 0.0) {
        end_v = joined_seconds - skip;
        if (end_v < 1.0) {
            end_v = joined_seconds;
            skip = 0.0;
        }
        start_v = end_v - config->clip_seconds;
        if (start_v < 0.0) start_v = 0.0;
        video_seconds = joined_seconds;
        if (start_v > 0.05 || skip > 0.05) {
            swprintf_s(command, ARRAY_COUNT(command),
                       L"\"%ls\" -hide_banner -loglevel warning -y "
                       L"-ss %.3f -t %.3f -i joined.mkv -map 0:v:0 -c:v copy "
                       L"-avoid_negative_ts make_zero trimmed.mkv",
                       config->ffmpeg_path, start_v, end_v - start_v);
            swprintf_s(path, ARRAY_COUNT(path), L"%ls\\trimmed.mkv", snapshot);
            if (run_process_wait_logged(command, snapshot, 60000, &exit_code, log_path) &&
                exit_code == 0 && probe_duration(config, snapshot, path, &video_seconds)) {
                video_file = L"trimmed.mkv";
                /* A stream-copy cut can start at the next keyframe instead of
                   the previous one; if the clip came out short, cut again
                   from one GOP earlier. The end point is unaffected. */
                if (video_seconds < config->clip_seconds - 0.25 && start_v > 0.0) {
                    double retry_seconds = 0.0;
                    start_v -= KEYFRAME_MARGIN_SECONDS;
                    if (start_v < 0.0) start_v = 0.0;
                    swprintf_s(command, ARRAY_COUNT(command),
                               L"\"%ls\" -hide_banner -loglevel warning -y "
                               L"-ss %.3f -t %.3f -i joined.mkv -map 0:v:0 -c:v copy "
                               L"-avoid_negative_ts make_zero trimmed.mkv",
                               config->ffmpeg_path, start_v, end_v - start_v);
                    if (run_process_wait_logged(command, snapshot, 60000, &exit_code, log_path) &&
                        exit_code == 0 && probe_duration(config, snapshot, path, &retry_seconds))
                        video_seconds = retry_seconds;
                    else {
                        video_file = L"joined.mkv";
                        video_seconds = joined_seconds;
                        skip = 0.0;
                    }
                }
            } else {
                wprintf(L"Could not trim the clip precisely; keeping the full snapshot.\n");
                video_seconds = joined_seconds;
                skip = 0.0;
            }
        }
    } else {
        wprintf(L"Could not measure the joined video; saving it without audio alignment.\n");
        has_audio = 0;
    }

    swprintf_s(saved_path, saved_count, L"%ls\\%ls_%ls.mp4", category_dir, category, timestamp);
    if (has_audio) {
        double drift_gap = 0.0, audio_end, audio_start, lead, seek;
        wchar_t seek_arg[64] = L"";
        if (tails_known) {
            tail_gap = (audio_tail_ms - video_tail_ms) / 1000.0;
            if (running && timing.have_video && timing.have_audio)
                drift_gap = (timing.video_drift_ms - timing.audio_drift_ms) / 1000.0;
        }
        audio_end = audio_seconds - (tail_gap + drift_gap) - skip;
        audio_start = audio_end - video_seconds - config->av_sync_offset_ms / 1000.0;
        if (audio_end <= 0.05 || audio_start >= audio_seconds - 0.05) {
            wprintf(L"The audio buffer does not overlap this clip; saving video only.\n");
            has_audio = 0;
        } else {
            lead = audio_start < 0.0 ? -audio_start : 0.0;
            seek = audio_start > 0.0 ? audio_start : 0.0;
            if (seek > 0.0) swprintf_s(seek_arg, ARRAY_COUNT(seek_arg), L"-ss %.3f ", seek);
            swprintf_s(command, ARRAY_COUNT(command),
                       L"\"%ls\" -hide_banner -loglevel warning -y "
                       L"-i %ls %ls-i joined_audio.mka -map 0:v:0 -map 1:a:0 -c:v copy "
                       L"-af \"adelay=%lldS:all=1,apad\" -c:a aac -b:a 192k "
                       L"-t %.3f -movflags +faststart final.mp4",
                       config->ffmpeg_path, video_file, seek_arg,
                       (long long)(lead * 48000.0 + 0.5), video_seconds);
            wprintf(L"Clip alignment: %.2f s of video; audio placed %+.0f ms from buffer tail (copy gap %+.0f ms, encoder drift %+.0f ms)%ls.\n",
                    video_seconds, (tail_gap + drift_gap) * 1000.0, tail_gap * 1000.0,
                    drift_gap * 1000.0, tails_known ? L"" : L", tails assumed equal");
        }
    }
    if (!has_audio) {
        swprintf_s(command, ARRAY_COUNT(command),
                   L"\"%ls\" -hide_banner -loglevel warning -y "
                   L"-i %ls -map 0:v:0 -an -c:v copy "
                   L"-movflags +faststart final.mp4",
                   config->ffmpeg_path, video_file);
    }
    wprintf(L"Writing the final MP4.\n");
    if (!run_process_wait_logged(command, snapshot, 120000, &exit_code, log_path) || exit_code != 0) {
        wprintf(L"FFmpeg could not create the final MP4. Details: %ls\n", log_path);
        wprintf(L"The failed snapshot was kept for troubleshooting: %ls\n", snapshot);
        return 0;
    }

    swprintf_s(path, ARRAY_COUNT(path), L"%ls\\final.mp4", snapshot);
    if (!MoveFileExW(path, saved_path, MOVEFILE_COPY_ALLOWED | MOVEFILE_REPLACE_EXISTING)) {
        print_last_error(L"Could not move the completed MP4 clip");
        remove_snapshot(snapshot, video_wanted, audio_wanted);
        return 0;
    }

    remove_snapshot(snapshot, video_wanted, audio_wanted);
    wprintf(L"Saved clip: %ls\n", saved_path);
    return 1;
}

static int find_newest_replay(wchar_t *out, size_t out_count)
{
    wchar_t local_app_data[PATH_CAP];
    wchar_t pattern[PATH_CAP];
    wchar_t directory[PATH_CAP];
    WIN32_FIND_DATAW data;
    FILETIME newest = {0};
    HANDLE search;
    int found = 0;

    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", local_app_data, ARRAY_COUNT(local_app_data))) return 0;
    swprintf_s(directory, ARRAY_COUNT(directory), L"%ls\\FortniteGame\\Saved\\Demos", local_app_data);
    swprintf_s(pattern, ARRAY_COUNT(pattern), L"%ls\\*.replay", directory);
    search = FindFirstFileW(pattern, &data);
    if (search == INVALID_HANDLE_VALUE) return 0;

    do {
        if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (!found || CompareFileTime(&data.ftLastWriteTime, &newest) > 0) {
            newest = data.ftLastWriteTime;
            swprintf_s(out, out_count, L"%ls\\%ls", directory, data.cFileName);
            found = 1;
        }
    } while (FindNextFileW(search, &data));
    FindClose(search);
    return found;
}

static int get_file_size_64(const wchar_t *path, ULONGLONG *size)
{
    WIN32_FILE_ATTRIBUTE_DATA data;
    ULARGE_INTEGER combined;
    if (!GetFileAttributesExW(path, GetFileExInfoStandard, &data)) return 0;
    combined.LowPart = data.nFileSizeLow;
    combined.HighPart = data.nFileSizeHigh;
    *size = combined.QuadPart;
    return 1;
}

static int file_is_closed(const wchar_t *path)
{
    HANDLE file = CreateFileW(path, GENERIC_READ, 0, NULL, OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return 0;
    CloseHandle(file);
    return 1;
}

static void replay_watch_initialize(ReplayWatch *watch, int fortnite_running)
{
    ZeroMemory(watch, sizeof(*watch));
    find_newest_replay(watch->tracked_path, ARRAY_COUNT(watch->tracked_path));
    if (watch->tracked_path[0]) get_file_size_64(watch->tracked_path, &watch->last_size);
    watch->handled = 1;
    watch->last_change_tick = GetTickCount64();
    if (fortnite_running && watch->tracked_path[0]) {
        watch->handled = 0;
        watch->observed_activity = !file_is_closed(watch->tracked_path);
        wprintf(L"Fortnite was already running. Watching the current replay for continued match activity: %ls\n",
                watch->tracked_path);
    }
}

static int replay_watch_poll(ReplayWatch *watch, const AppConfig *config,
                             wchar_t *completed, size_t completed_count)
{
    wchar_t newest[PATH_CAP];
    ULONGLONG size;
    ULONGLONG now = GetTickCount64();

    if (!find_newest_replay(newest, ARRAY_COUNT(newest))) return 0;
    if (_wcsicmp(newest, watch->tracked_path) != 0) {
        wcscpy_s(watch->tracked_path, ARRAY_COUNT(watch->tracked_path), newest);
        watch->last_size = 0;
        get_file_size_64(newest, &watch->last_size);
        watch->last_change_tick = now;
        watch->handled = 0;
        watch->observed_activity = 1;
        wprintf(L"New Fortnite replay detected: %ls\n", newest);
        return 0;
    }

    if (watch->handled || !get_file_size_64(newest, &size)) return 0;
    if (size != watch->last_size) {
        if (!watch->observed_activity)
            wprintf(L"Current Fortnite replay is active; match-end detection is armed.\n");
        watch->last_size = size;
        watch->last_change_tick = now;
        watch->observed_activity = 1;
        return 0;
    }

    if (watch->observed_activity &&
        now - watch->last_change_tick >= (ULONGLONG)config->replay_stable_seconds * 1000ULL &&
        file_is_closed(newest)) {
        wcscpy_s(completed, completed_count, newest);
        watch->handled = 1;
        return 1;
    }
    return 0;
}

static char *read_entire_file(const wchar_t *path)
{
    FILE *file = _wfopen(path, L"rb");
    long length;
    char *data;
    if (!file) return NULL;
    fseek(file, 0, SEEK_END);
    length = ftell(file);
    fseek(file, 0, SEEK_SET);
    if (length < 0 || length > 128 * 1024 * 1024) { fclose(file); return NULL; }
    data = (char *)malloc((size_t)length + 1);
    if (!data) { fclose(file); return NULL; }
    if (fread(data, 1, (size_t)length, file) != (size_t)length) {
        free(data); fclose(file); return NULL;
    }
    data[length] = '\0';
    fclose(file);
    return data;
}

static const char *json_value_start(const char *json, const char *key)
{
    char pattern[256];
    const char *found;
    snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    found = strstr(json, pattern);
    if (!found) return NULL;
    found = strchr(found + strlen(pattern), ':');
    if (!found) return NULL;
    found++;
    while (*found && isspace((unsigned char)*found)) found++;
    return found;
}

static int json_int(const char *json, const char *key, int *value)
{
    const char *start = json_value_start(json, key);
    char *end;
    long number;
    if (!start) return 0;
    number = strtol(start, &end, 10);
    if (end == start) return 0;
    *value = (int)number;
    return 1;
}

static int json_bool(const char *json, const char *key, int *value)
{
    const char *start = json_value_start(json, key);
    if (!start) return 0;
    if (strncmp(start, "true", 4) == 0) {
        *value = 1;
        return 1;
    }
    if (strncmp(start, "false", 5) == 0) {
        *value = 0;
        return 1;
    }
    return 0;
}

static int json_double(const char *json, const char *key, double *value)
{
    const char *start = json_value_start(json, key);
    char *end;
    double number;
    if (!start) return 0;
    number = strtod(start, &end);
    if (end == start) return 0;
    *value = number;
    return 1;
}

static int json_string(const char *json, const char *key, char *out, size_t out_count)
{
    const char *start = json_value_start(json, key);
    size_t source_index = 0;
    size_t output_index = 0;
    if (!start || *start != '"') return 0;
    start++;
    while (start[source_index] && start[source_index] != '"' &&
           output_index + 1 < out_count) {
        if (start[source_index] == '\\' && start[source_index + 1]) source_index++;
        out[output_index++] = start[source_index++];
    }
    out[output_index] = '\0';
    return output_index > 0;
}

static int parse_local_replay_result(const wchar_t *result_path, MatchResult *result)
{
    char *json = read_entire_file(result_path);
    int owner_final_kill = 0;

    ZeroMemory(result, sizeof(*result));
    if (!json) return 0;

    if (!json_bool(json, "conclusive", &result->conclusive) ||
        !json_int(json, "placement", &result->placement) ||
        !json_bool(json, "ownerFinalKill", &owner_final_kill) ||
        !json_bool(json, "sniper", &result->sniper)) {
        free(json);
        return 0;
    }

    result->has_kill = owner_final_kill;
    /* Older parsers only reported "sniper". */
    if (!json_bool(json, "trickshotWeapon", &result->trickshot_weapon))
        result->trickshot_weapon = result->sniper;
    json_double(json, "distanceUU", &result->distance_uu);
    json_string(json, "killer", result->killer, sizeof(result->killer));
    json_string(json, "victim", result->victim, sizeof(result->victim));
    json_string(json, "weapon", result->weapon, sizeof(result->weapon));
    json_string(json, "shotType", result->shot_type, sizeof(result->shot_type));
    json_string(json, "reason", result->reason, sizeof(result->reason));
    free(json);
    return 1;
}

static int call_local_replay_parser(const AppConfig *config, const wchar_t *replay,
                                    MatchResult *result)
{
    wchar_t result_path[PATH_CAP];
    wchar_t log_path[PATH_CAP];
    wchar_t command[COMMAND_CAP];
    DWORD exit_code = 1;

    if (GetFileAttributesW(config->local_parser_path) == INVALID_FILE_ATTRIBUTES) {
        wprintf(L"Local replay parser is missing: %ls\n", config->local_parser_path);
        return 0;
    }

    join_path(result_path, ARRAY_COUNT(result_path), config->work_dir,
              L"latest_local_replay.json");
    join_path(log_path, ARRAY_COUNT(log_path), config->work_dir,
              L"local_replay_parser.log");
    DeleteFileW(result_path);
    swprintf_s(command, ARRAY_COUNT(command), L"\"%ls\" \"%ls\" \"%ls\"",
               config->local_parser_path, replay, result_path);

    wprintf(L"Analyzing the completed replay locally.\n");
    if (!run_process_wait_logged(command, config->base_dir, 120000,
                                 &exit_code, log_path) || exit_code != 0) {
        wprintf(L"Local replay analysis failed; the clip remains in Pending. Details: %ls\n",
                log_path);
        return 0;
    }

    if (!parse_local_replay_result(result_path, result)) {
        wprintf(L"Local replay result could not be read; the clip remains in Pending. Details: %ls\n",
                log_path);
        return 0;
    }
    if (!result->conclusive) {
        wprintf(L"Local replay analysis was inconclusive: %hs The clip remains in Pending. Details: %ls\n",
                result->reason[0] ? result->reason : "No reason was returned.", log_path);
        return 0;
    }

    wprintf(L"Replay analysis: %hs\n",
            result->reason[0] ? result->reason : "Classification completed.");

    return 1;
}

static void classify_pending_clip(const AppConfig *config, const wchar_t *clip,
                                  const wchar_t *replay)
{
    MatchResult result;
    wchar_t destination_dir[PATH_CAP], destination[PATH_CAP], timestamp[128];
    const wchar_t *category;
    const wchar_t *shot_label;

    if (!config->local_replay_enabled) {
        wprintf(L"Local replay analysis disabled; automatic clip remains in Pending.\n");
        return;
    }
    if (!call_local_replay_parser(config, replay, &result)) {
        return;
    }

    /* A trickshot: you won, you made the final kill, it was a sniper or a
       heavy-ammo weapon, and the replay measured its distance. */
    if (result.placement == 1 && result.has_kill && result.trickshot_weapon &&
        result.distance_uu > 0.0) {
        double meters = result.distance_uu / 100.0;
        category = L"Trickshots";
        shot_label = _stricmp(result.shot_type, "NoScope") == 0 ? L"no-scope"
                   : result.sniper ? L"sniper"
                   : L"heavy-ammo";
        join_path(destination_dir, ARRAY_COUNT(destination_dir), config->clips_dir, category);
        ensure_directory(destination_dir);
        make_timestamp(timestamp, ARRAY_COUNT(timestamp));
        swprintf_s(destination, ARRAY_COUNT(destination), L"%ls\\%.0fm_%ls.mp4",
                   destination_dir, meters, timestamp);
        if (MoveFileExW(clip, destination, MOVEFILE_COPY_ALLOWED | MOVEFILE_REPLACE_EXISTING))
            wprintf(L"Qualified trickshot (%ls final kill, %.0fm): %ls\n", shot_label, meters, destination);
        else
            print_last_error(L"Could not move qualified clip");
    } else if (config->keep_rejected_clips) {
        category = L"Rejected";
        join_path(destination_dir, ARRAY_COUNT(destination_dir), config->clips_dir, category);
        ensure_directory(destination_dir);
        make_timestamp(timestamp, ARRAY_COUNT(timestamp));
        swprintf_s(destination, ARRAY_COUNT(destination), L"%ls\\Rejected_%ls.mp4", destination_dir, timestamp);
        if (MoveFileExW(clip, destination, MOVEFILE_COPY_ALLOWED | MOVEFILE_REPLACE_EXISTING))
            wprintf(L"Clip did not qualify and was moved to Rejected.\n");
    } else {
        DeleteFileW(clip);
        wprintf(L"Clip did not qualify and was removed.\n");
    }
}

int wmain(void)
{
    AppConfig config;
    Recorder recorder;
    ReplayWatch watch;
    int fortnite_previous = 0;
    ULONGLONG last_process_check = 0;
    ULONGLONG last_replay_check = 0;
    ULONGLONG automatic_clip_due = 0;
    double automatic_clip_end_ms = 0.0;
    wchar_t pending_replay[PATH_CAP] = L"";
    HANDLE save_event;
    HANDLE stop_event;
    HANDLE engine_mutex;
    int save_hotkey_registered;
    UINT save_hotkey_modifiers;
    UINT save_hotkey_virtual_key;

    SetProcessDPIAware();
    SetConsoleOutputCP(CP_UTF8);
    setvbuf(stdout, NULL, _IONBF, 0);
    SetConsoleCtrlHandler(console_handler, TRUE);

    engine_mutex = CreateMutexW(NULL, FALSE, ENGINE_MUTEX_NAME);
    if (!engine_mutex) {
        print_last_error(L"Could not create the ClipShot engine lock");
        return 1;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        wprintf(L"Another ClipShot engine is already running; no second FFmpeg process was started.\n");
        CloseHandle(engine_mutex);
        return 2;
    }

    ZeroMemory(&recorder, sizeof(recorder));
    load_config(&config);
    ensure_directory(config.buffer_dir);
    ensure_directory(config.clips_dir);
    ensure_directory(config.work_dir);
    replay_watch_initialize(&watch, process_is_running(FORTNITE_PROCESS));

    save_event = CreateEventW(NULL, FALSE, FALSE, SAVE_EVENT_NAME);
    stop_event = CreateEventW(NULL, FALSE, FALSE, STOP_EVENT_NAME);
    if (!save_event || !stop_event) {
        print_last_error(L"Could not create GUI control events");
        if (save_event) CloseHandle(save_event);
        if (stop_event) CloseHandle(stop_event);
        CloseHandle(engine_mutex);
        return 1;
    }

    if (!parse_hotkey(config.manual_hotkey, &save_hotkey_modifiers, &save_hotkey_virtual_key)) {
        wcscpy_s(config.manual_hotkey, ARRAY_COUNT(config.manual_hotkey), L"F8");
        save_hotkey_modifiers = MOD_NOREPEAT;
        save_hotkey_virtual_key = VK_F8;
    }
    save_hotkey_registered = RegisterHotKey(NULL, HOTKEY_SAVE,
                                            save_hotkey_modifiers,
                                            save_hotkey_virtual_key);
    if (!save_hotkey_registered)
        wprintf(L"Warning: %ls could not be registered; GUI controls remain available.\n",
                config.manual_hotkey);

    wprintf(L"Decrypt's ClipShot engine\n");
    wprintf(L"%ls saves a manual clip.\n", config.manual_hotkey);
    wprintf(L"Buffer directory: %ls\n", config.buffer_dir);

    if (!config.start_only_with_fortnite) recorder_start(&recorder, &config);

    while (!InterlockedCompareExchange(&g_quit, 0, 0)) {
        MSG message;
        ULONGLONG now = GetTickCount64();

        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            if (message.message == WM_HOTKEY && message.wParam == HOTKEY_SAVE) {
                wchar_t saved[PATH_CAP];
                save_clip(&config, &recorder, L"Manual", 0.0, saved, ARRAY_COUNT(saved));
            }
        }

        if (WaitForSingleObject(save_event, 0) == WAIT_OBJECT_0) {
            wchar_t saved[PATH_CAP];
            save_clip(&config, &recorder, L"Manual", 0.0, saved, ARRAY_COUNT(saved));
        }
        if (WaitForSingleObject(stop_event, 0) == WAIT_OBJECT_0)
            InterlockedExchange(&g_quit, 1);

        if (now - last_process_check >= 1000) {
            recorder_poll_audio(&recorder, &config);
            recorder_report_timing(&recorder);
            int fortnite = process_is_running(FORTNITE_PROCESS);
            if (fortnite && !fortnite_previous) {
                wprintf(L"Fortnite detected.\n");
                if (config.start_only_with_fortnite) recorder_start(&recorder, &config);
            } else if (!fortnite && fortnite_previous) {
                wprintf(L"Fortnite closed.\n");
                if (config.start_only_with_fortnite) recorder_stop(&recorder);
            }
            fortnite_previous = fortnite;
            last_process_check = now;
        }

        if (now - last_replay_check >= 1000) {
            wchar_t completed_replay[PATH_CAP];
            if (replay_watch_poll(&watch, &config, completed_replay, ARRAY_COUNT(completed_replay))) {
                /* The match ended when the replay stopped growing, not when the
                   stability timer finished (replay_stable_seconds later).
                   Anchor the clip end to match end + post-match seconds; the
                   old code ended clips at detection + post-match, which cut
                   up to replay_stable_seconds of the match off the front. */
                ULONGLONG match_end_tick = watch.last_change_tick;
                ULONGLONG wanted_end_tick = match_end_tick +
                                            (ULONGLONG)config.post_match_seconds * 1000ULL;
                wcscpy_s(pending_replay, ARRAY_COUNT(pending_replay), completed_replay);
                automatic_clip_end_ms = now_ms() -
                                        (double)((LONGLONG)now - (LONGLONG)wanted_end_tick);
                /* One extra second lets the final footage reach disk. */
                automatic_clip_due = wanted_end_tick + 1000ULL;
                if (automatic_clip_due < now) automatic_clip_due = now;
                wprintf(L"Completed match replay detected. The clip will end %d seconds after the match ended.\n",
                        config.post_match_seconds);
            }
            last_replay_check = now;
        }

        if (automatic_clip_due && now >= automatic_clip_due) {
            wchar_t pending_clip[PATH_CAP];
            wprintf(L"Post-match delay complete. Saving pending clip.\n");
            if (save_clip(&config, &recorder, L"Pending", automatic_clip_end_ms,
                          pending_clip, ARRAY_COUNT(pending_clip))) {
                classify_pending_clip(&config, pending_clip, pending_replay);
            }
            automatic_clip_due = 0;
            automatic_clip_end_ms = 0.0;
            pending_replay[0] = L'\0';
        }

        Sleep(25);
    }

    recorder_stop(&recorder);
    if (save_hotkey_registered) UnregisterHotKey(NULL, HOTKEY_SAVE);
    CloseHandle(save_event);
    CloseHandle(stop_event);
    CloseHandle(engine_mutex);
    wprintf(L"Decrypt's ClipShot stopped.\n");
    return 0;
}

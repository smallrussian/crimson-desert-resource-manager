/*
 * Camp Ledger launcher ASI.
 *
 * Loaded into the game by an ASI loader. Its ONLY job is to start the external reader
 * (CampLedgerReader.exe watch) when the game loads. The reader exits by itself when the game closes.
 *
 * This DLL does not read or write game memory, does not hook or call game functions, and does not
 * touch saves (AGENTS.md hard rule 1, launcher-only exception approved 2026-11-11).
 *
 * The reader's path comes from CampLedgerLauncher.ini, one line, next to this DLL:
 *     C:\path\to\CampLedgerReader.exe
 * If the file or the reader is missing, the launcher does nothing.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

static HMODULE g_self;

static DWORD WINAPI start_reader(LPVOID unused)
{
    wchar_t ini[MAX_PATH], reader[MAX_PATH] = {0}, cmd[MAX_PATH + 32];
    char line[MAX_PATH] = {0};
    DWORD got = 0, n;
    HANDLE f;
    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;
    (void)unused;

    n = GetModuleFileNameW(g_self, ini, MAX_PATH);
    if (n == 0 || n >= MAX_PATH - 4) return 0;
    while (n > 0 && ini[n - 1] != L'.') n--;      /* replace the extension with "ini" */
    if (n == 0) return 0;
    ini[n] = L'i'; ini[n + 1] = L'n'; ini[n + 2] = L'i'; ini[n + 3] = 0;

    f = CreateFileW(ini, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;
    ReadFile(f, line, sizeof line - 1, &got, NULL);
    CloseHandle(f);
    while (got > 0 && (line[got - 1] == '\r' || line[got - 1] == '\n' || line[got - 1] == ' ')) line[--got] = 0;
    if (got == 0) return 0;
    if (MultiByteToWideChar(CP_UTF8, 0, line, -1, reader, MAX_PATH) == 0) return 0;
    if (GetFileAttributesW(reader) == INVALID_FILE_ATTRIBUTES) return 0;

    wsprintfW(cmd, L"\"%s\" watch", reader);
    if (CreateProcessW(reader, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        HANDLE t;
        g_self = inst;
        DisableThreadLibraryCalls(inst);
        /* Do the work off the loader lock. */
        t = CreateThread(NULL, 0, start_reader, NULL, 0, NULL);
        if (t) CloseHandle(t);
    }
    return TRUE;
}

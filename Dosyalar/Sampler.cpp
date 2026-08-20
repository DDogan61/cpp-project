#include <windows.h>
#include <winternl.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <share.h>
#include <io.h>


// Tells the linker about the libs here, so nothing has to be added in the
// project settings.
#pragma comment(lib, "dbghelp.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "winmm.lib")


#define MAX_FRAMES 256      // most functions we read from one stack
#define MAX_THREADS 64      // most threads we scan
#define DEFAULT_INTERVAL_MS 10      // period used when no argument is given

// Has to be a power of two, the index is taken with a mask.
#define MAX_SYMBOL_CACHE  4096
#define SYM_MAX_PROBE     8

// NtQueryInformationThread is declared in winternl.h, but the info class we
// need is undocumented so we write the constant ourselves. It has not changed
// since NT 4.0, and the NTSTATUS is still checked on every call: if it ever
// does change the function fails and the thread pick below falls back to the
// first one.
#define ThreadQuerySetWin32StartAddress 9


typedef struct {
    DWORD64 address;        // the address we looked up (lookupAddr, so with the -1 applied)
    char    name[256];
    char    file[MAX_PATH];
    DWORD   line;
} SymbolCacheEntry;

typedef NTSTATUS(NTAPI* PFN_NtQueryInformationThread)(
    HANDLE, int, PVOID, ULONG, PULONG);


// Address range of the target exe in memory. This is how we tell which frame on
// the stack is ours and which one belongs to Windows.
DWORD64 g_appBase = 0;
DWORD64 g_appEnd = 0;

// Entry point of the exe (mainCRTStartup). Thread start addresses are compared
// against this to find the main thread.
DWORD64 g_appEntry = 0;

// Global because it is over 1 MB and would not fit on the stack. It also starts
// out zeroed, which the probing below relies on: address == 0 means empty slot.
SymbolCacheEntry g_symCache[MAX_SYMBOL_CACHE];
int g_symCacheCount = 0;

PFN_NtQueryInformationThread g_ntQueryThread = NULL;


void InitNtApi(void)
{
    // ntdll is already loaded into every process, no LoadLibrary needed.
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (ntdll == NULL) {
        return;
    }

    g_ntQueryThread = (PFN_NtQueryInformationThread)
        GetProcAddress(ntdll, "NtQueryInformationThread");
}


// ============================================================================
// Is the target the same bitness as we are?
//
// We do not start the target any more, we attach to whatever is already
// running, so its architecture is not ours to decide. StackWalk64 is called
// with IMAGE_FILE_MACHINE_AMD64 below and a 32 bit target would walk into
// garbage: no frames, an empty chart, and no clue why. Better to say it.
// ============================================================================
BOOL IsSameArchitecture(HANDLE process)
{
    BOOL targetIsWow64 = FALSE;
    BOOL selfIsWow64 = FALSE;

    // If the call itself fails we cannot tell, so we carry on rather than
    // refusing to sample for no reason.
    if (!IsWow64Process(process, &targetIsWow64)) {
        return TRUE;
    }

    if (!IsWow64Process(GetCurrentProcess(), &selfIsWow64)) {
        return TRUE;
    }

    // WOW64 means "32 bit process on a 64 bit Windows". Equal flags mean both
    // sides are the same kind.
    return targetIsWow64 == selfIsWow64;
}


// ============================================================================
// Fills the array with every thread id of the process and returns how many
// there were. Which one gets sampled is decided by IsMainThread below.
// ============================================================================
int FindThreads(DWORD pid, DWORD* threadIds, int maxCount)
{
    // A thread snapshot holds EVERY thread on the system, so we filter by the
    // owner process here.
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return 0;
    }

    int count = 0;

    THREADENTRY32 entry;
    entry.dwSize = sizeof(entry);

    if (Thread32First(snapshot, &entry)) {
        do {
            // Only the threads of our own target
            if (entry.th32OwnerProcessID == pid && count < maxCount) {
                threadIds[count] = entry.th32ThreadID;
                count++;
            }
        } while (Thread32Next(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return count;
}


// ============================================================================
// Finds the address range and the entry point of the target exe.
//
// It doubles as a "is the process ready" check: if the process was just born
// the Windows loader has not built the module list yet and this fails with
// ERROR_PARTIAL_COPY. That is why main loops on it until it succeeds, and why
// SymInitialize may only be called afterwards. Otherwise no symbols get loaded
// and the stack comes out as hex addresses.
// ============================================================================
BOOL InitAppRange(HANDLE process)
{
    // The first module EnumProcessModules gives back is always the exe itself,
    // so an array of one is enough.
    HMODULE modules[1];
    DWORD needed = 0;

    if (!EnumProcessModules(process, modules, sizeof(modules), &needed)) {
        return FALSE;
    }

    MODULEINFO info;
    if (!GetModuleInformation(process, modules[0], &info, sizeof(info))) {
        return FALSE;
    }

    g_appBase = (DWORD64)info.lpBaseOfDll;
    g_appEnd = g_appBase + info.SizeOfImage;
    g_appEntry = (DWORD64)info.EntryPoint;
    return TRUE;
}


// Is the address inside the target exe? If not it belongs to system code like
// ntdll or kernel32 and is none of our business.
BOOL IsAppAddress(DWORD64 address)
{
    return address >= g_appBase && address < g_appEnd;
}


// ============================================================================
// Is this the main thread, the one started from the entry point of the exe?
//
// We do not trust the order Toolhelp returns things in: "the first one is the
// main thread" is only observed behaviour, not a documented guarantee. Windows
// keeps the start address of every thread, and comparing that to the entry
// point of the exe is exact. It also works while the target has not even
// reached main yet.
// ============================================================================
BOOL IsMainThread(HANDLE thread)
{
    if (g_ntQueryThread == NULL || g_appEntry == 0) {
        return FALSE;
    }

    DWORD64 startAddr = 0;
    ULONG returned = 0;

    NTSTATUS status = g_ntQueryThread(thread, ThreadQuerySetWin32StartAddress,
        &startAddr, sizeof(startAddr), &returned);

    if (status != 0 || returned != sizeof(startAddr)) {
        return FALSE;
    }

    return startAddr == g_appEntry;
}


// ============================================================================
// Resolves an address from the cache, or asks the PDB and stores the result.
//
// Open addressing: the keys are return addresses, so every call site is its own
// entry. With a linear search every frame went back to the PDB once the table
// filled up and the sampling rate fell apart.
//
// The returned pointer can go stale on the next call (the slot may be
// overwritten), so the caller uses it right away and does not keep it.
// ============================================================================
SymbolCacheEntry* ResolveAddress(HANDLE process, DWORD64 lookupAddr)
{
    // Addresses are aligned, so the low bits carry no information and masking
    // them directly would pile everything into the same bucket. The multiply
    // pulls the upper bits down.
    size_t i = (size_t)((lookupAddr * 0x9E3779B97F4A7C15ULL) >> 45)
        & (MAX_SYMBOL_CACHE - 1);

    SymbolCacheEntry* slot = &g_symCache[i];

    for (int p = 0; p < SYM_MAX_PROBE; p++) {
        slot = &g_symCache[i];

        if (slot->address == lookupAddr) {
            return slot;   // cache hit
        }

        if (slot->address == 0) {
            g_symCacheCount++;
            break;         // found an empty slot
        }

        i = (i + 1) & (MAX_SYMBOL_CACHE - 1);
    }

    // If there is no room after 8 tries the last slot is overwritten. Probing
    // without a limit spins forever once the table is full; this is bounded and
    // still correct, that one address just gets resolved again later.

    slot->address = lookupAddr;

    // Union: reading a char array as a SYMBOL_INFO* was risky alignment wise.
    // This both guarantees the alignment and makes the intent obvious.
    union {
        SYMBOL_INFO info;
        char raw[sizeof(SYMBOL_INFO) + 256];
    } sym;

    memset(&sym, 0, sizeof(sym));

    SYMBOL_INFO* symbol = &sym.info;
    symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
    symbol->MaxNameLen = 255;

    DWORD64 displacement = 0;

    if (SymFromAddr(process, lookupAddr, &displacement, symbol)) {
        sprintf_s(slot->name, sizeof(slot->name), "%s", symbol->Name);
    }
    else {
        sprintf_s(slot->name, sizeof(slot->name), "0x%llx",
            (unsigned long long)lookupAddr);
    }

    slot->file[0] = '\0';
    slot->line = 0;

    IMAGEHLP_LINE64 lineInfo;
    memset(&lineInfo, 0, sizeof(lineInfo));
    lineInfo.SizeOfStruct = sizeof(IMAGEHLP_LINE64);

    DWORD lineDisplacement = 0;

    if (SymGetLineFromAddr64(process, lookupAddr, &lineDisplacement, &lineInfo)) {
        sprintf_s(slot->file, sizeof(slot->file), "%s", lineInfo.FileName);

        // The JSON trap: a backslash has to be escaped. Turning them into
        // forward slashes keeps the JSON valid and the Windows APIs take the
        // path just the same.
        for (char* p = slot->file; *p; p++) {
            if (*p == '\\') {
                *p = '/';
            }
        }

        slot->line = lineInfo.LineNumber;
    }

    return slot;
}


// ============================================================================
// Freezes one thread, reads its stack, prints a JSON line, lets it go.
// ============================================================================
void PrintStack(HANDLE process, HANDLE thread, DWORD threadId, DWORD elapsedMs)
{
    // The registers of a running thread change all the time, so it has to be
    // stopped before we can read a consistent stack.
    // On failure this returns -1 (normally it is the previous suspend count).
    if (SuspendThread(thread) == (DWORD)-1) {
        return;
    }

    // CONTEXT is every CPU register of the thread at that moment
    CONTEXT context;
    memset(&context, 0, sizeof(context));
    // Without ContextFlags the call succeeds but the struct stays empty. This
    // is the classic mistake here.
    context.ContextFlags = CONTEXT_FULL;

    if (!GetThreadContext(thread, &context)) {
        ResumeThread(thread);   // never leave the thread hanging on an error
        return;
    }

    // Starting point for StackWalk64. Three registers are enough:
    //   Rip = address of the instruction running right now (which function)
    //   Rbp = frame base pointer
    //   Rsp = current top of the stack
    STACKFRAME64 frame;
    memset(&frame, 0, sizeof(frame));
    frame.AddrPC.Offset = context.Rip;      frame.AddrPC.Mode = AddrModeFlat;
    frame.AddrFrame.Offset = context.Rbp;   frame.AddrFrame.Mode = AddrModeFlat;
    frame.AddrStack.Offset = context.Rsp;   frame.AddrStack.Mode = AddrModeFlat;

    DWORD64 addresses[MAX_FRAMES];
    int frameCount = 0;

    while (frameCount < MAX_FRAMES) {
        // Every call takes us one function up. It answers "who called me" by
        // reading the unwind info in the .pdata section of the exe, and it
        // updates frame and context on its own, we do not touch them.
        BOOL ok = StackWalk64(
            IMAGE_FILE_MACHINE_AMD64,     // walking an x64 stack
            process, thread,
            &frame, &context,
            NULL,                         // memory reading: the default is fine
            SymFunctionTableAccess64,     // helper that finds the unwind table
            SymGetModuleBase64,           // helper that finds which module an address is in
            NULL);

        if (!ok || frame.AddrPC.Offset == 0) {
            break;   // reached the bottom of the stack
        }

        addresses[frameCount] = frame.AddrPC.Offset;
        frameCount++;
    }

    // Let the thread go as early as possible. The printing happens below and
    // there is no reason to keep it frozen through that.
    ResumeThread(thread);

    if (frameCount == 0) {
        return;
    }

    // The line is built in memory first and written out in one go. A printf per
    // frame meant a library call per frame.
    char line[16384];
    int n = 0;
    int w = 0;
    int appFrames = 0;

    n = _snprintf_s(line, sizeof(line), _TRUNCATE,
        "{\"t_ms\":%lu,\"tid\":%lu,\"frames\":[", elapsedMs, threadId);

    // Backwards: StackWalk64 starts at the innermost frame and we write from
    // main inwards.
    for (int i = frameCount - 1; i >= 0; i--) {
        // System frames are not written at all. The C# side was throwing them
        // away anyway, so formatting them and putting them on disk was wasted
        // work.
        if (!IsAppAddress(addresses[i])) {
            continue;
        }

        // addresses[0] is the innermost frame, the instruction that is really
        // executing. All the others are return addresses, which point at the
        // instruction AFTER the call. Going back one byte still lands inside
        // the call and gives the right line.
        BOOL isReturnAddress = (i != 0);

        SymbolCacheEntry* e = ResolveAddress(process,
            isReturnAddress ? addresses[i] - 1 : addresses[i]);

        const char* sep = appFrames ? "," : "";

        if (e->line > 0) {
            w = _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE,
                "%s{\"fn\":\"%s\",\"file\":\"%s\",\"line\":%lu}",
                sep, e->name, e->file, e->line);
        }
        else {
            // For frames with no line info (code without a PDB) the file and
            // line fields are left out completely. "Not there means not there"
            // is cleaner on the parser side than checking for empty strings.
            w = _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE,
                "%s{\"fn\":\"%s\"}", sep, e->name);
        }

        if (w < 0) {
            break;   // buffer is full, cut the line where it is
        }

        n += w;
        appFrames++;
    }

    // Samples that were not running our own code produce no line.
    if (appFrames == 0) {
        return;
    }

    w = _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE, "]}\n");
    if (w > 0) {
        n += w;
    }

    fwrite(line, 1, n, stdout);
}


// ============================================================================
// Sampler.exe <pid> [output] [interval_ms] [stop_event]
//
// The pid comes from Visual Studio, which knows it already because the target
// is running under its debugger. Nothing is built, launched or searched for by
// name here: we attach to a process that is up and sample it as it is.
// ============================================================================
int main(int argc, char** argv)
{
    if (argc < 2) {
        fprintf(stderr, "usage: Sampler.exe <pid> [output] [interval_ms] [stop_event]\n");
        return 1;
    }

    DWORD pid = (DWORD)strtoul(argv[1], NULL, 10);
    if (pid == 0) {
        fprintf(stderr, "invalid pid: %s\n", argv[1]);
        return 1;
    }

    const char* outputPath = (argc >= 3) ? argv[2] : "samples.jsonl";

    // _SH_DENYNO means "others may open it too". freopen_s does the opposite
    // and locks the file, and then the C# side cannot read while we write.
    FILE* outputFile = _fsopen(outputPath, "w", _SH_DENYNO);

    if (outputFile == NULL) {
        fprintf(stderr, "cannot open output file: %s\n", outputPath);
        return 1;
    }

    // Put that file in the place of stdout, so the printfs end up there.
    _dup2(_fileno(outputFile), _fileno(stdout));

    // 64 KB buffer, the default ~4 KB makes the write calls far too frequent.
    setvbuf(stdout, NULL, _IOFBF, 1 << 16);

    InitNtApi();

    int intervalMs = (argc > 3) ? atoi(argv[3]) : DEFAULT_INTERVAL_MS;
    if (intervalMs <= 0) {
        intervalMs = DEFAULT_INTERVAL_MS;
    }

    // How the extension tells us to stop. It cannot simply kill us: we may be
    // holding a suspended thread of the target at that very moment, and a
    // killed process never gets to resume it. The app the user is debugging
    // would stay frozen for good. So we are asked to leave and we leave on our
    // own, after the thread is back on its feet.
    //
    // The event is created on the C# side before we start, we only open it.
    HANDLE stopEvent = NULL;

    if (argc > 4 && argv[4][0] != '\0') {
        stopEvent = OpenEventA(SYNCHRONIZE, FALSE, argv[4]);

        if (stopEvent == NULL) {
            // Not fatal: without it we simply run until the target exits.
            fprintf(stderr, "cannot open stop event %s (error %lu)\n",
                argv[4], GetLastError());
        }
    }

    // The process handle. We have to say up front what we are going to do:
    //   PROCESS_QUERY_INFORMATION -> to read the module list
    //   PROCESS_VM_READ           -> to read the stack memory
    //   SYNCHRONIZE               -> to be able to ask "are you done" with
    //                                WaitForSingleObject. Without this right
    //                                the call returns WAIT_FAILED and we never
    //                                notice that the target has exited.
    HANDLE process = OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid);

    if (process == NULL) {
        fprintf(stderr, "cannot open process (error %lu)\n", GetLastError());
        return 1;
    }

    if (!IsSameArchitecture(process)) {
        fprintf(stderr, "target is a 32 bit process, this sampler only walks "
            "x64 stacks. Build the target as x64.\n");
        CloseHandle(process);
        return 1;
    }

    // The target has been running for a while, so the loader is long done and
    // this normally succeeds on the first try. The retries are only there for
    // the case where we are attached right as it starts.
    //
    // Bounded on purpose: when we launched the target ourselves a failure here
    // could only mean "not ready yet", so waiting forever was fine. Attaching
    // to something already running, a failure means something that will not fix
    // itself, and the old loop would have spun until someone killed us.
    int moduleTries = 0;

    while (!InitAppRange(process)) {
        if (++moduleTries > 50) {
            fprintf(stderr, "cannot read the module list (error %lu)\n",
                GetLastError());
            CloseHandle(process);
            return 1;
        }

        Sleep(10);
    }

    // The thread list is taken once, at the start.
    DWORD threadIds[MAX_THREADS];
    int threadCount = FindThreads(pid, threadIds, MAX_THREADS);

    if (threadCount == 0) {
        fprintf(stderr, "no thread found\n");
        CloseHandle(process);
        return 1;
    }

    // ========================================================================
    // Only the main thread is sampled.
    //
    // The chart draws a single call chain (FlameChartModel keeps one open
    // chain), so samples from several threads would just get mixed together.
    // On top of that a separate SuspendThread + StackWalk64 for every thread is
    // wasted work per period and slows the target down for nothing.
    // ========================================================================
    HANDLE mainThread = NULL;
    DWORD mainThreadId = 0;

    for (int i = 0; i < threadCount; i++) {
        //   THREAD_SUSPEND_RESUME    -> to stop and resume it
        //   THREAD_GET_CONTEXT       -> to read the registers
        //   THREAD_QUERY_INFORMATION -> to ask for the start address
        HANDLE thread = OpenThread(
            THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
            FALSE, threadIds[i]);

        if (thread == NULL) {
            continue;
        }

        if (IsMainThread(thread)) {
            mainThread = thread;
            mainThreadId = threadIds[i];
            break;
        }

        CloseHandle(thread);
    }

    if (mainThread == NULL) {
        // We only get here if NtQueryInformationThread cannot be resolved, or
        // if the info class changes one day. Instead of quietly picking the
        // wrong thread we say so and fall back to the order.
        fprintf(stderr, "main thread not identified, falling back to first\n");

        mainThreadId = threadIds[0];
        mainThread = OpenThread(
            THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
            FALSE, mainThreadId);
    }

    if (mainThread == NULL) {
        fprintf(stderr, "cannot open main thread (error %lu)\n", GetLastError());
        CloseHandle(process);
        return 1;
    }

    if (threadCount > 1) {
        fprintf(stderr, "warning: target has %d threads, "
            "only the main thread is sampled\n", threadCount);
    }

    // SYMOPT_UNDNAME     -> make the C++ names readable
    // SYMOPT_LOAD_LINES  -> load the line info from the PDB as well. Without
    //                       this flag SymGetLineFromAddr64 quietly returns FALSE.
    SymSetOptions(SYMOPT_UNDNAME | SYMOPT_LOAD_LINES);
    SymInitialize(process, NULL, TRUE);  // TRUE = scan the target's modules and load the PDBs

    fprintf(stderr, "pid=%lu  tid=%lu  threads=%d  interval=%d ms\n",
        pid, mainThreadId, threadCount, intervalMs);

    // First line of the file is the meta info. Sample lines have no "type"
    // field and this one does, which is how the parser tells them apart.
    // Without interval_ms the C# side cannot work out how many ms a sample is.
    //
    // threads is the total thread count of the target. Even though only one is
    // sampled it is useful to know.
    printf("{\"type\":\"meta\",\"interval_ms\":%d,\"pid\":%lu,\"tid\":%lu,"
        "\"threads\":%d}\n",
        intervalMs, pid, mainThreadId, threadCount);
    fflush(stdout);

    BOOL timerPeriodSet = (timeBeginPeriod(1) == TIMERR_NOERROR);
    if (!timerPeriodSet) {
        fprintf(stderr, "timeBeginPeriod failed\n");
    }

    // GetTickCount only resolves to about 15.6 ms. Sampling every 10 ms with a
    // timestamp that moves in 15.6 ms steps means the same t_ms repeats over and
    // over and the thing we are measuring falls apart. QPC goes below the
    // microsecond.
    LARGE_INTEGER qpcFreq, qpcStart;
    QueryPerformanceFrequency(&qpcFreq);

    // Sleep is stuck with the system timer resolution. This timer runs
    // independently of it and actually keeps the interval we asked for.
    HANDLE waitTimer = CreateWaitableTimerExW(NULL, NULL,
        CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

    if (waitTimer == NULL) {
        fprintf(stderr, "high-res timer unavailable, falling back to Sleep\n");
    }

    // The timer is set up as periodic, the second parameter is the period in ms.
    // Once it is set the loop only has to wait on it.
    //
    // Compared to setting it up again every round: there the period became
    // "10 ms + however long the work took", so sampling thinned out exactly
    // where the deep stacks were being read. A periodic timer keeps the tempo.
    if (waitTimer != NULL) {
        LARGE_INTEGER due;
        // A negative value means "from now on", the unit is 100 ns.
        due.QuadPart = -(LONGLONG)intervalMs * 10000;
        SetWaitableTimer(waitTimer, &due, intervalMs, NULL, NULL, FALSE);
    }

    QueryPerformanceCounter(&qpcStart);
    DWORD lastFlush = GetTickCount();

    // Two things end the run: the target exits, or the extension signals the
    // stop event. Both are waited on TOGETHER with the timer instead of being
    // polled around it. Polling meant we could be parked in the timer wait
    // while one of them fired, and the answer came a period late.
    //
    // Order matters: index 0 is the tick, anything above it ends the loop.
    HANDLE waitHandles[3];
    DWORD waitCount = 0;

    if (waitTimer != NULL) {
        waitHandles[waitCount++] = waitTimer;
    }

    waitHandles[waitCount++] = process;

    if (stopEvent != NULL) {
        waitHandles[waitCount++] = stopEvent;
    }

    for (;;) {
        if (waitTimer != NULL) {
            // Wakes up on the tick, or straight away if the run is over.
            if (WaitForMultipleObjects(waitCount, waitHandles, FALSE, INFINITE)
                != WAIT_OBJECT_0) {
                break;
            }
        }
        else {
            // No high resolution timer: Sleep is the tick and the handles are
            // only asked afterwards, with a timeout of 0.
            Sleep(intervalMs);

            if (WaitForMultipleObjects(waitCount, waitHandles, FALSE, 0)
                != WAIT_TIMEOUT) {
                break;
            }
        }

        // The timestamp is taken AFTER the wait, because the samples are
        // collected after the wait too. Otherwise the t_ms of every line was
        // one period behind.
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);

        DWORD elapsed = (DWORD)((now.QuadPart - qpcStart.QuadPart) * 1000
            / qpcFreq.QuadPart);

        PrintStack(process, mainThread, mainThreadId, elapsed);

        // stdout is redirected to a file so it is fully buffered. Without this
        // the data only reaches the disk once the buffer fills up and the C#
        // side reading live sees nothing. C# reads every 50 ms anyway, so
        // flushing more often than this buys nothing.
        if (GetTickCount() - lastFlush >= 15) {
            fflush(stdout);
            lastFlush = GetTickCount();
        }
    }

    CloseHandle(mainThread);

    if (timerPeriodSet) {
        timeEndPeriod(1);
    }

    if (waitTimer != NULL) {
        CloseHandle(waitTimer);
    }

    if (stopEvent != NULL) {
        CloseHandle(stopEvent);
    }

    SymCleanup(process);
    CloseHandle(process);

    fprintf(stderr, "symbol cache: %d entry\n", g_symCacheCount);
    fprintf(stderr, "done -> %s\n", outputPath);

    fflush(stdout);
    fclose(outputFile);
    return 0;
}
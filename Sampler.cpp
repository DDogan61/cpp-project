#include <windows.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// dbghelp.lib'i linker'a bildirir; proje ayarlarindan eklemeye gerek kalmaz.
#pragma comment(lib, "dbghelp.lib")

#define MAX_FRAMES      64      // bir stack'te okunacak en fazla fonksiyon sayisi
#define MAX_THREADS     64      // izlenecek en fazla thread sayisi
#define INTERVAL_MS     25      // ornekler arasi bekleme
#define SAMPLE_COUNT    200     // toplam ornek sayisi


// ============================================================================
// Exe adindan process ID bulur. Bulamazsa 0 doner.
// ============================================================================
DWORD FindProcessId(const char* exeName)
{
    // Snapshot = o andaki process listesinin donmus bir kopyasi.
    // Liste surekli degistigi icin Windows once fotografini cekmeni istiyor.
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return 0;
    }

    DWORD pid = 0;

    PROCESSENTRY32 entry;
    // dwSize'i doldurmak zorunlu: Windows yapinin hangi surumu oldugunu
    // boyutundan anliyor. Doldurmazsan Process32First basarisiz doner.
    entry.dwSize = sizeof(entry);

    if (Process32First(snapshot, &entry)) {
        do {
            // _stricmp = buyuk/kucuk harf duyarsiz karsilastirma
            if (_stricmp(entry.szExeFile, exeName) == 0) {
                pid = entry.th32ProcessID;
                break;
            }
        } while (Process32Next(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return pid;
}


// ============================================================================
// Process'e ait tum thread ID'lerini diziye doldurur. Bulunan sayiyi doner.
// Multi-thread destegi tamamen bu fonksiyonda: tek thread'li hedefte dizi
// tek elemanli olur, cok thread'lide cok elemanli. Geri kalan kod ayni.
// ============================================================================
int FindThreads(DWORD pid, DWORD* threadIds, int maxCount)
{
    // Thread snapshot'i sistemdeki BUTUN thread'leri icerir,
    // o yuzden asagida sahibine gore filtreliyoruz.
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return 0;
    }

    int count = 0;

    THREADENTRY32 entry;
    entry.dwSize = sizeof(entry);

    if (Thread32First(snapshot, &entry)) {
        do {
            // Sadece bizim hedef process'imize ait thread'ler
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
// Bellek adresini fonksiyon adina cevirir. PDB yoksa hex adres yazar.
// ============================================================================
void AddressToName(HANDLE process, DWORD64 address, char* output, int outputSize)
{
    // SYMBOL_INFO'nun sonunda degisken uzunlukta bir isim alani var.
    // Bu yuzden yapinin kendisinden 256 byte fazla yer ayiriyoruz.
    char buffer[sizeof(SYMBOL_INFO) + 256];
    memset(buffer, 0, sizeof(buffer));

    SYMBOL_INFO* symbol = (SYMBOL_INFO*)buffer;
    symbol->SizeOfStruct = sizeof(SYMBOL_INFO);   // yapinin kendi boyutu
    symbol->MaxNameLen = 255;                   // isim icin ayirdigimiz yer

    // Adres fonksiyonun tam basi degilse, kac byte iceride oldugunu buraya yazar
    DWORD64 displacement = 0;

    if (SymFromAddr(process, address, &displacement, symbol)) {
        sprintf_s(output, outputSize, "%s", symbol->Name);
    }
    else {
        // Sembol bulunamadi: modulun PDB'si yok demektir
        sprintf_s(output, outputSize, "0x%llx", (unsigned long long)address);
    }
}


// ============================================================================
// Tek bir thread'i dondurur, stack'ini okur, JSON satiri basar, birakir.
// ============================================================================
void PrintStack(HANDLE process, HANDLE thread, DWORD threadId, DWORD elapsedMs)
{
    // Calisan bir thread'in register'lari surekli degisir; tutarli bir stack
    // okumak icin once durdurmak zorundayiz.
    // Basarisizlikta -1 doner (normalde onceki askiya alma sayisini dondurur).
    if (SuspendThread(thread) == (DWORD)-1) {
        return;
    }

    // CONTEXT = thread'in o andaki tum CPU register'lari
    CONTEXT context;
    memset(&context, 0, sizeof(context));
    // ContextFlags doldurulmazsa fonksiyon basarili doner ama yapi bos kalir.
    // En sik yapilan hata bu.
    context.ContextFlags = CONTEXT_FULL;

    if (!GetThreadContext(thread, &context)) {
        ResumeThread(thread);   // hata durumunda thread'i asla asili birakma
        return;
    }

    // StackWalk64'un baslangic noktasi. Uc register yeterli:
    //   Rip = su an calisan komutun adresi (hangi fonksiyondayiz)
    //   Rbp = frame taban isaretcisi
    //   Rsp = stack'in su anki tepesi
    STACKFRAME64 frame;
    memset(&frame, 0, sizeof(frame));
    frame.AddrPC.Offset = context.Rip;   frame.AddrPC.Mode = AddrModeFlat;
    frame.AddrFrame.Offset = context.Rbp;   frame.AddrFrame.Mode = AddrModeFlat;
    frame.AddrStack.Offset = context.Rsp;   frame.AddrStack.Mode = AddrModeFlat;

    DWORD64 addresses[MAX_FRAMES];
    int frameCount = 0;

    while (frameCount < MAX_FRAMES) {
        // Her cagri bir ust fonksiyona cikar: "beni kim cagirdi" sorusunu
        // exe'nin .pdata bolumundeki unwind bilgisini okuyarak cozer.
        // frame ve context'i kendisi gunceller, biz dokunmuyoruz.
        BOOL ok = StackWalk64(
            IMAGE_FILE_MACHINE_AMD64,     // x64 stack yuruyoruz
            process, thread,
            &frame, &context,
            NULL,                         // bellek okuma: varsayilan yeterli
            SymFunctionTableAccess64,     // unwind tablosunu bulan yardimci
            SymGetModuleBase64,           // adresin hangi modulde oldugunu bulan yardimci
            NULL);

        if (!ok || frame.AddrPC.Offset == 0) {
            break;   // stack'in dibine geldik
        }

        addresses[frameCount] = frame.AddrPC.Offset;
        frameCount++;
    }

    // Thread'i mumkun olan en erken anda birak.
    // Yazdirma islemi asagida; onu beklerken thread donmus kalmasin.
    ResumeThread(thread);

    if (frameCount == 0) {
        return;
    }

    printf("{\"t_ms\":%lu,\"tid\":%lu,\"frames\":[", elapsedMs, threadId);

    // Diziyi tersten geziyoruz: StackWalk64 en icteki fonksiyondan basliyor,
    // biz main'den ice dogru yazmak istiyoruz.
    for (int i = frameCount - 1; i >= 0; i--) {
        char name[256];
        AddressToName(process, addresses[i], name, sizeof(name));
        printf("\"%s\"", name);
        if (i > 0) {
            printf(",");
        }
    }

    printf("]}\n");
}


int main(int argc, char** argv)
{
    // Veri stdout'a, durum mesajlari stderr'e gidiyor.
    // Boylece stdout dosyaya yonlendirilse bile konsolda ilerlemeyi gorursun.
    FILE* outputFile = NULL;
    freopen_s(&outputFile, "samples.jsonl", "w", stdout);

    // Argüman verilmezse varsayilan hedef
    const char* targetName = (argc >= 2) ? argv[1] : "TargetApp.exe";

    // Argüman sayi ise PID kabul et, degilse exe adi olarak ara.
    // atoi sayi olmayan metinde 0 dondurur.
    DWORD pid = (DWORD)atoi(targetName);

    if (pid == 0) {
        fprintf(stderr, "waiting for %s ...\n", targetName);

        // Hedef henuz baslamamis olabilir; gorunene kadar bekle.
        // Boylece once sampler'i, sonra hedefi baslatabilirsin.
        while (pid == 0) {
            pid = FindProcessId(targetName);
            Sleep(50);
        }
    }

    // Process handle'i: ne yapacagimizi bastan bildirmek zorundayiz.
    //   PROCESS_QUERY_INFORMATION -> modul listesini okumak icin
    //   PROCESS_VM_READ           -> stack bellegini okumak icin
    HANDLE process = OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);

    if (process == NULL) {
        fprintf(stderr, "cannot open process (error %lu)\n", GetLastError());
        return 1;
    }

    // Thread listesi bir kez, baslangicta aliniyor.
    // Calisirken yeni acilan thread'ler goruntulenmez.
    DWORD threadIds[MAX_THREADS];
    int threadCount = FindThreads(pid, threadIds, MAX_THREADS);

    HANDLE threads[MAX_THREADS];
    int openCount = 0;

    for (int i = 0; i < threadCount; i++) {
        //   THREAD_SUSPEND_RESUME    -> durdurup devam ettirmek icin
        //   THREAD_GET_CONTEXT       -> register'lari okumak icin
        //   THREAD_QUERY_INFORMATION -> genel bilgi icin
        threads[i] = OpenThread(
            THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
            FALSE, threadIds[i]);

        if (threads[i] != NULL) {
            openCount++;
        }
    }

    if (openCount == 0) {
        fprintf(stderr, "no thread could be opened\n");
        CloseHandle(process);
        return 1;
    }

    SymSetOptions(SYMOPT_UNDNAME);       // C++ isimlerini okunabilir hale getir
    SymInitialize(process, NULL, TRUE);  // TRUE = hedefin modullerini tara ve PDB'leri yukle

    fprintf(stderr, "pid=%lu  threads=%d  interval=%d ms\n",
        pid, openCount, INTERVAL_MS);

    DWORD startTime = GetTickCount();

    for (int s = 0; s < SAMPLE_COUNT; s++) {
        // Bekleme suresi 0: "bitti mi" diye sorup hemen donuyoruz
        if (WaitForSingleObject(process, 0) == WAIT_OBJECT_0) {
            fprintf(stderr, "target exited\n");
            break;
        }

        DWORD elapsed = GetTickCount() - startTime;

        // Thread'leri hep birlikte degil, sirayla dolasiyoruz.
        // Aradaki mikrosaniyelik kayma istatistiksel olarak onemsiz,
        // ama hepsini ayni anda dondurmaya gore cok daha guvenli.
        for (int i = 0; i < threadCount; i++) {
            if (threads[i] != NULL) {
                PrintStack(process, threads[i], threadIds[i], elapsed);
            }
        }

        Sleep(INTERVAL_MS);
    }

    for (int i = 0; i < threadCount; i++) {
        if (threads[i] != NULL) {
            CloseHandle(threads[i]);
        }
    }

    SymCleanup(process);
    CloseHandle(process);

    fprintf(stderr, "done -> samples.jsonl\n");
    return 0;
}

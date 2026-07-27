# Stack Sampler

Windows üzerinde çalışan bir process'in call stack'ini belirli aralıklarla
örnekleyen basit bir sampling profiler. Uzun vadeli hedef: toplanan veriden
zaman çizgisi (flame chart) üreten bir Visual Studio eklentisi.

## Durum

- [x] `target.cpp` — deterministik test hedefi
- [x] `sampler.cpp` — stack örnekleyici, JSONL çıktı
- [ ] Gürültü filtreleri (CRT/ntdll çerçeveleri, sistem thread'leri)
- [ ] Multi-thread test hedefi
- [ ] C# port / VSIX entegrasyonu
- [ ] Timeline çizimi

## Derleme ve çalıştırma

İki ayrı proje, ikisi de **x64 / Debug**:

- `Sampler` — Console App (C++)
- `TargetApp` — Console App (C++)

Sampler projesinde **Yapılandırma Özellikleri → Gelişmiş → Karakter Kümesi**
ayarını **"Çok Baytlı Karakter Kümesi Kullan"** yapmak gerekiyor. Unicode modda
`PROCESSENTRY32` aslında `PROCESSENTRY32W`'ye çözülüyor ve `szExeFile` alanı
`wchar_t` oluyor; `_stricmp` ile uyuşmuyor.

Sıra:

1. Sampler'ı F5 ile başlat — hedefi görene kadar bekler
2. TargetApp'i Ctrl+F5 ile başlat
3. Çıktı `x64\Debug\samples.jsonl` dosyasına yazılır

Veri `stdout`'a, durum mesajları `stderr`'e gider. Bu yüzden `stdout` dosyaya
yönlendirilmişken bile konsolda ilerlemeyi görürsün.

---

## Temel kavramlar

### Stack thread'e aittir, process'e değil

Process bir kap: bellek alanı, açık dosyalar, handle'lar. Kod çalıştıran şey
thread. Bir process'te N thread varsa N ayrı stack vardır. "Bu programın
stack'i" diye tek bir şey yok — hangi thread'in olduğunu belirtmek gerekiyor.

Bu yüzden akış şöyle: exe adı → PID → thread listesi → her thread için stack.

### Neden register'lar gerekiyor

Stack sadece bir bellek bloğu. İçinde dönüş adresleri, yerel değişkenler ve
geçici değerler karışık halde duruyor; hiçbir yerde "şu 8 byte bir dönüş
adresidir" diye bir işaret yok.

Register'lar giriş kapısı:

| Register | Anlamı |
|---|---|
| `Rip` | Şu an çalışan komutun adresi — hangi fonksiyondayız |
| `Rsp` | Stack'in şu anki tepesi |
| `Rbp` | Frame taban işaretçisi |

Bunlar olmadan elinde nereden okumaya başlayacağını bilmediğin bir sayı yığını
olur.

### Context nereden geliyor

Bir thread çalışmayı bıraktığında (zamanı dolduğunda, `Sleep` çağırdığında, ya
da `SuspendThread` ile durdurulduğunda) çekirdek bütün register'larını kaydeder.
Zaten kaydetmek zorunda — yoksa thread tekrar sıraya geldiğinde kaldığı yerden
devam edemezdi. Buna context switch deniyor, "context" kelimesi buradan.

`GetThreadContext` o kaydedilmiş kopyayı verir. Ama thread duruyor olmalı;
çalışan bir thread'in register'ları mikrosaniyede binlerce kez değişir.

Dört adımlık döngü:

```
SuspendThread     → register'lar donar
GetThreadContext  → donmuş kopyayı al
StackWalk64       → zinciri çöz
ResumeThread      → devam et
```

`CONTEXT` yapısında `ContextFlags` alanını doldurmak zorunlu. Doldurmazsan
`GetThreadContext` başarılı döner ama yapı boş kalır — sessiz hata.

### Unwinding

x64'te optimizasyon açıkken derleyici `Rbp`'yi frame pointer olarak
kullanmayabiliyor, yani "beni kim çağırdı" sorusu basit bir zincir takibiyle
çözülmüyor.

Windows her fonksiyon için "frame'i nasıl geri saracaksın" bilgisini PE
dosyasının `.pdata` bölümünde tutuyor. `StackWalk64` bunu okuyor:

1. `Rip`'e bakıp hangi fonksiyonda olduğunu bulur
2. Unwind bilgisinden frame boyutunu öğrenir
3. O kadar yukarı çıkıp dönüş adresini okur
4. Tekrarlar

Kodda her `StackWalk64` çağrısı bir üst çerçeveye çıkıyor; `frame` ve `context`
yapılarını kendisi güncelliyor.

### Semboller

Stack walk ham adres verir: `0x7ff6a2c31040`. Bunu `fun2` yapan şey PDB dosyası.

- `SymInitialize(process, NULL, TRUE)` — hedefin modüllerini tarar, PDB'leri yükler
- `SymFromAddr` — adresi isme çevirir

PDB yoksa isim de yok. Bu yüzden hedef **Debug** derlenmiş olmalı (`/Zi` +
linker `/DEBUG`).

`SymFromAddr`'ın üçüncü parametresi `displacement` — adresin fonksiyon başından
kaç byte içeride olduğu, çıktısı bizim işimize yaramıyor ama parametre zorunlu.

### Örnekleme aralığı

Aralığından kısa süren her şey kaybolur. Target'taki en kısa aşama 160 ms, o
yüzden 200 ms aralık yetmiyordu; 25 ms'e indirdik. Gerçek profiler'lar 1 kHz
civarında çalışıyor.

Ayrıca sampling **çağrı sayısı** vermez, sadece **zaman dağılımı** verir. Aynı
fonksiyonun ardışık iki çağrısı tek bir blok olarak görünür. Tam çağrı sayısı
için compile-time instrumentation (`/Gh`, `/GH`) gerekiyor — ayrı bir yaklaşım.

### x64 / x86 uyumu

`CONTEXT` yapısı iki mimaride tamamen farklı (x64'te 1232 byte, x86'da 716).
Kodda `IMAGE_FILE_MACHINE_AMD64` sabit; 32-bit hedeflerde çalışmaz. 32-bit
process'ler WOW64 katmanı üzerinden çalıştığı için gerçek register'ları
`Wow64GetThreadContext` ile alınıyor.

Pratikte: hedef de sampler da x64 olmalı. İleride `IsWow64Process` ile kontrol
edip net bir hata mesajı vermek mantıklı.

---

## Windows API alışkanlıkları

**HANDLE ve temizlik.** Process, thread, snapshot — hepsi handle. GC yok,
işi bitince `CloseHandle`.

**Erişim hakları baştan bildirilir.** `OpenProcess` ve `OpenThread` çağrılarında
ne yapacağını söylüyorsun; istemediğin hakkı sonradan kullanamazsın.

| Bayrak | Ne için |
|---|---|
| `PROCESS_QUERY_INFORMATION` | modül listesini okumak |
| `PROCESS_VM_READ` | stack belleğini okumak |
| `THREAD_SUSPEND_RESUME` | durdurup devam ettirmek |
| `THREAD_GET_CONTEXT` | register'ları okumak |

**Hata dönüş değeriyle bildirilir**, exception yok. `OpenProcess` → `NULL`,
`SuspendThread` → `(DWORD)-1`. Ayrıntı `GetLastError()`.

**Yapı boyutu ilk alana yazılır.** `entry.dwSize = sizeof(entry)` ve
`symbol->SizeOfStruct = sizeof(SYMBOL_INFO)`. Windows yıllar içinde bu yapılara
alan ekliyor; boyutu söyleyerek hangi sürümü bildiğini belirtiyorsun. Doldurmayı
unutmak sessiz hataların bir numaralı kaynağı.

**Enumeration deseni.** `CreateToolhelp32Snapshot` ile listenin donmuş bir
kopyasını alırsın, sonra `First` / `Next` ile gezersin. `do-while` kullanılıyor
çünkü `First` zaten ilk kaydı doldurmuş oluyor. Aynı kalıp dosya, servis, modül
aramada da geçerli.

**`SYMBOL_INFO`'nun sonunda değişken uzunlukta isim alanı var.** Bu yüzden
yapıdan büyük bir tampon ayırıp başını yapı olarak gösteriyoruz:

```c
char buffer[sizeof(SYMBOL_INFO) + 256];
SYMBOL_INFO* symbol = (SYMBOL_INFO*)buffer;
symbol->MaxNameLen = 255;
```

**`#pragma comment(lib, "dbghelp.lib")`** — MSVC'ye özel; linker kütüphanesini
proje ayarları yerine kodun içinden bildiriyor.

**`sprintf_s`** — MSVC'nin sınırlı sürümü, hedef tamponun boyutunu da alıyor.

---

## Çıktı formatı

JSON Lines (JSONL). Her satır bağımsız, geçerli bir JSON nesnesi; dışta dizi
parantezi ve aralarda virgül yok.

```
{"t_ms":4890,"tid":19600,"frames":["main","fun1","fun2","fun3"]}
{"t_ms":4922,"tid":19600,"frames":["main","fun1","fun2","fun3"]}
```

| Alan | Anlamı |
|---|---|
| `t_ms` | Örnekleme başlangıcından geçen milisaniye |
| `tid` | Thread ID — multi-thread'de satırları ayırmak için |
| `frames` | `main`'den içeri doğru çağrı zinciri |

Neden JSONL:

- Akış halinde yazılabilir; dosya sonuna satır eklemek yeterli
- Program çökse bile yazılmış satırlar okunabilir kalır
- Satır satır işlenir, tümünü belleğe almak gerekmez

`frames` dizisindeki indeks doğrudan **derinlik**. `frames[0]` en dışta,
sonuncusu o an çalışan fonksiyon. Timeline çizerken ters çevirmeye gerek yok.

---

## Bilinen eksikler

**Gürültü.** Her satırda üç kaynaktan gereksiz çerçeve var:

1. CRT önyükleme zinciri — `RtlUserThreadStart`, `BaseThreadInitThunk`,
   `mainCRTStartup`, `__scrt_common_main`, `__scrt_common_main_seh`,
   `invoke_main`. Her zaman aynı; `main`'i bulup öncesini atmak yeterli.
2. `Sleep`'in iç katmanları — `SleepEx`, `RtlDelayExecution`,
   `ZwDelayExecution`. Modülü `ntdll` / `KernelBase` olanları elemek gerekiyor.
3. Sistem thread'leri — `ZwWaitForWorkViaWorkerFactory` içinde bekleyen thread
   pool worker'ları. Bunlar Windows'un otomatik açtığı thread'ler; boş bir
   `main`'de bile 2-4 tane çıkıyor, sayıları makineye göre değişiyor.

**Thread listesi bir kez alınıyor.** Çalışırken açılan thread'ler görünmez.
Periyodik tazeleme gerekiyor.

**Sembolizasyon örnekleme döngüsünün içinde.** 25 ms aralıkta sorun değil ama
1 kHz'e çıkarken hot path'ten çıkarılmalı: döngüde sadece ham adres + timestamp
toplanır, isim çözümü sonradan yapılır.

**Thread'ler sırayla dolaşılıyor**, hepsi birlikte dondurulmuyor. Aradaki
mikrosaniyelik kayma istatistiksel olarak önemsiz ve bu yol deadlock riski
açısından daha güvenli.

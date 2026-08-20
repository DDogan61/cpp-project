# FlameCharter — Attach Mode Değişiklikleri

Bu doküman, profiler'ın **kendi başlattığı target**'tan **hazırda çalışan target'a attach olan** yapıya geçişini anlatır. Sorun çıktığında buradaki "Belirti → Sebep" tablosundan başla.

Değişen dosyalar: `Sampler.cpp`, `SamplerWindowControl.xaml.cs`, `SamplerWindowControl.xaml`, `SamplerWindow.cs`, `FlameChartView.cs`
Değişmeyenler: `FlameChartModel.cs`, `SampleParser.cs`, `JsonlTailReader.cs` — JSONL formatı aynı kaldı, sadece verinin nasıl üretildiği değişti.

---

## 1. Neden değişti

Eski akış test makinesinde patladı:

1. Extension `slnBuild.BuildProject(..., true)` ile startup project'i **senkron** derliyordu → VS build süresi boyunca dondu.
2. Build, VS'in kullanıcı ortamı yerine extension'ın gördüğü ortamla çalıştı → build error.
3. Sonra `IVsBuildPropertyStorage` ile exe yolu çözülüp target `Process.Start` ile başlatılıyordu.

Bu zincirin tamamı silindi. Artık kullanıcı solution'ı normal şekilde **F5** ile çalıştırıyor, biz sadece çalışan process'e bağlanıyoruz.

## 2. Eski akış vs yeni akış

| | Eski | Yeni |
|---|---|---|
| Target'ı kim başlatır | Extension (`Process.Start`) | Kullanıcı (F5) |
| Build | Extension senkron derler | Yok |
| Exe yolu çözümü | `IVsBuildPropertyStorage` + macro expansion | Yok |
| Target kimliği | Exe **adı** → sampler isimle arar | **PID**, DTE'den alınır |
| Sampler ne zaman başlar | Target'tan önce, bekler | Target çalışırken |
| Durdurma | Target kill → sampler kill | Stop event → sampler kendi çıkar |
| Buton | Run / Stop (iki buton) | Activate ⇄ Deactivate (tek buton) |

**Yeni akış:** F5 → Activate → DTE'den PID → `Sampler.exe <pid> <out> <interval> <stopEvent>` → tail reader + chart → Deactivate → stop event → sampler çıkar → son satırlar okunur.

---

## 3. Dosya bazlı değişiklikler

### 3.1 `Sampler.cpp`

| Değişiklik | Detay |
|---|---|
| `FindProcessId()` **silindi** | İsimle process arama tamamen kalktı |
| `IsSameArchitecture()` **eklendi** | `IsWow64Process` ile target ve sampler bitliğini karşılaştırır |
| Argümanlar değişti | `Sampler.exe <pid> [output] [interval_ms] [stop_event]` — **pid zorunlu** |
| Stop event | `OpenEventA(SYNCHRONIZE, ...)` ile açılır; event'i C# tarafı **oluşturur**, sampler sadece açar |
| `InitAppRange` beklemesi sınırlandı | Sonsuz döngü yerine 50 deneme (500 ms), sonra hata |
| Meta satırı | `"target":"<isim>"` çıktı, `"tid"` eklendi |
| Ana döngü | `WaitForSingleObject(timer)` → `WaitForMultipleObjects(timer, process, stopEvent)` |
| `SAMPLE_ALL_FRAMES` | Yeni compile-time switch (bkz. §7) |
| Line buffer | 16 KB → 64 KB (filtresiz mod için) |

**Ana döngünün mantığı:** handle dizisinde index 0 = tick kaynağı (timer), üstündekiler = döngüyü bitirenler (process, stopEvent). `WAIT_OBJECT_0` dışında bir dönüş = çık. Timer yoksa `Sleep(intervalMs)` + timeout 0 ile sorgu.

### 3.2 `SamplerWindowControl.xaml.cs`

**Silinenler (~180 satır):** `ResolveTargetExe`, `GetBuildProperty`, `GetUserProperty`, `ExpandMacros`, `_debugArgs`, `_debugWorkDir`, `_finished`, `btnStart_Click`, `btnStop_Click`, `StopPreviousRun`

**Eklenenler:**

- `btnToggle_Click` — `_active` durumuna göre Activate/Deactivate
- `Activate()` — PID al, sampler'ı başlat, timer'ı kur
- `Deactivate(status)` — event set, çıkışı bekle, son satırları oku, temizle
- `StopIfActive()` — pencere kapanırken çağrılır (public)
- `FindDebuggedProcess(out name)` — DTE'den PID
- `SafeProcessName(EnvDTE.Process)` — COM istisnalarına karşı sarmalayıcı
- `GetSolutionFolder()` — sadece `FindFirstAppFrame` için, build yok
- `ReadNewSamples()` — `Timer_Tick`'ten ayrıldı, çünkü `Deactivate` de çağırıyor

**Korunanlar:** `KillIfRunning` (artık sadece sampler için), `FindFirstAppFrame`, chart event handler'ları, peak paneli, DTE ile kaynağa gitme.

### 3.3 Diğerleri

- **`SamplerWindowControl.xaml`** — `btnStart`/`btnStop` → tek `btnToggle`; `txtTarget` placeholder metni
- **`SamplerWindow.cs`** — `OnClose()` override, `StopIfActive()` çağırır
- **`FlameChartView.cs`** — `DrawLineSegments` içinde satır numarası konumlandırma (bkz. §6.2)

---

## 4. Kritik tasarım kararları

### 4.1 Sampler neden kill edilmiyor

**En önemli madde.** `Deactivate` sampler'ı öldürmez, stop event ile durmasını ister.

Sebep: sampler her sample'da target thread'ini `SuspendThread` ile durdurup context okuyup `ResumeThread` yapıyor. Tam o aralıkta öldürülürse **Windows suspend'i geri almaz.** Target thread sonsuza kadar askıda kalır — yani kullanıcının debug ettiği program kalıcı olarak donar, kurtarmanın yolu yoktur.

Eski akışta target zaten öldürülüyordu, o yüzden sorun değildi. Attach modunda target kullanıcının programı ve **hayatta kalmak zorunda.**

`KillIfRunning` hâlâ var ama sadece event'e 3 saniye içinde cevap vermezse devreye giriyor. Normal koşulda hiç çalışmamalı.

### 4.2 Stop event neden C# tarafında oluşturuluyor

Sampler event'i sadece `OpenEventA` ile açıyor, oluşturmuyor. Event C# tarafında sampler başlamadan **önce** var olmalı, yoksa ilk Deactivate'te sinyal verilecek bir nesne olmaz.

Ayrıca `new EventWaitHandle(false, ManualReset, name)` — **aynı isimde event zaten varsa initial state parametresi yok sayılır** ve mevcut nesne signaled haliyle döner. Activate → Deactivate → Activate senaryosunda önceki sampler handle'ı henüz kapatmamışsa yeni sampler ilk wait'te "dur" görüp tek sample yazmadan çıkar. Bu yüzden oluşturduktan hemen sonra **`_stopEvent.Reset()` var** — silme.

### 4.3 Target handle'ı neden sadece izleniyor

`_targetProcess` yalnızca `HasExited` için tutuluyor, asla `Kill` edilmiyor. Lifetime'ı debugger yönetiyor.

### 4.4 Timer_Tick'te kontrol sırası

`samplerDone` ve `targetGone` **okumadan önce** hesaplanıyor. Tersi olsaydı: sampler son satırları yazıp çıkar, biz okuduktan sonra "çıkmış" deriz ve o satırlar hiç okunmaz.

---

## 5. Belirti → Sebep tablosu

### 5.1 Tool window durum satırı (`txtStatus`)

| Mesaj | Anlamı / Kontrol |
|---|---|
| `Nothing is running under the debugger. Start it with F5.` | Debug session yok. **Ctrl+F5 ile başlattıysan DTE görmez** — F5 kullan |
| `Sampler.exe not found: <yol>` | VSIX'e gömülmemiş. `.csproj`'da `Include in VSIX = True` ve Build Action = Content mi |
| `The target has already exited.` | PID alındı ama process Activate'e kadar öldü |
| `Cannot ask the debugger: <msg>` | DTE/COM hatası; session kapanma anında olabilir |
| `Cannot start the sampler: <msg>` | `Process.Start` patladı — yol, izin, AV |
| `waiting for sampler...` | JSONL dosyası henüz yok. **2 sn'den uzun sürerse sampler attach olamadı** → §5.2 |
| `attached` | Sampler başladı, ilk tick henüz gelmedi |
| `N samples` | Normal çalışma |
| `target exited, flushing...` | Target bitti, sampler son satırları yazıyor (kısa sürmeli) |
| `Target exited - N samples` | Temiz bitiş |
| `Sampler stopped - N samples` | **Target yaşıyor ama sampler öldü** — anormal, §5.2'ye bak |
| `ERR: <msg>` | Okuma/çizim istisnası, mesaj detayı verir |

### 5.2 Sampler stderr mesajları

Görmek için `Activate()` içinde `CreateNoWindow = false` yap, konsol penceresi açılır.

| Mesaj | Sebep |
|---|---|
| `invalid pid: X` | Argüman parse edilemedi (C# tarafı bozuk gönderiyor) |
| `cannot open output file` | Temp klasörü yazılamıyor |
| `cannot open stop event X (error N)` | **Deactivate çalışmaz, target donma riski.** Event adı uyuşmuyor demek |
| `cannot open process (error N)` | `OpenProcess` başarısız. Error 5 = Access Denied → target elevated |
| `target is a 32 bit process...` | Solution Platforms = x64 yap |
| `cannot read the module list (error N)` | 500 ms'de modül listesi okunamadı; genelde izin sorunu |
| `no thread found` | Snapshot boş — target ölmüş olabilir |
| `main thread not identified, falling back to first` | **Uyarı, hata değil.** Yanlış thread sample'lanıyor olabilir |
| `warning: target has N threads` | Sadece main sample'lanıyor, tasarım gereği |
| `high-res timer unavailable...` | Sleep'e düştü, interval hassasiyeti düşer |
| `done -> <yol>` | Temiz çıkış |

### 5.3 Grafikle ilgili belirtiler

| Belirti | Sebep |
|---|---|
| Chart boş, sample sayısı artıyor | Frame'ler var ama `IsAppAddress` hepsini eliyor → target DLL'de çalışıyor olabilir |
| Beklenen fonksiyon hiç çıkmıyor | O kod **ayrı bir DLL projesinde**. Filtre sadece ana exe aralığını geçirir. Static lib sorun değil |
| Tek geniş kutu, hiç derinleşmiyor | Target IO'da bloklanmış — **doğru davranış** (wall-clock profiler) |
| Kutular tarak gibi, her sample ayrı | Sembol çözülemiyor, her frame farklı hex adres → merge çalışmıyor. Sistem DLL'lerinde PDB yoksa normal |
| Coverage < %100 | `appFrames == 0` olan sample'lar yazılmıyor. Sample kaçmadı, satır üretilemedi |
| Release'de zincir kısa | Inlining. Normal |
| Satır numarası görünmüyor | Segment < 20 px (zoom gerekir) veya §6.2 |

### 5.4 Donma senaryoları

| Belirti | Sebep |
|---|---|
| **Deactivate'ten sonra target donuk** | Stop event açılamadı, sampler kill edildi ve suspend'i geri almadı. stderr'de `cannot open stop event` olmalı. Target'ı kurtarmanın yolu yok, yeniden başlat |
| VS donuyor | Bu değişiklikten sonra olmamalı — build kodu silindi. Olursa `WaitForExit` timeout'larına bak |
| Activate hiç sample vermiyor, ikinci denemede çalışıyor | §4.2'deki `Reset()` çağrısı silinmiş olabilir |

---

## 6. Bilinen sınırlar

### 6.1 Kapsam

- **x86 target desteklenmiyor.** Sampler x64 derlenmiş, `StackWalk64` `IMAGE_FILE_MACHINE_AMD64` ile çağrılıyor. Net hata mesajı verir, sessiz başarısızlık yok.
- **Ctrl+F5 görünmez.** DTE sadece debugger altındaki process'i bilir.
- **DLL projelerinin frame'leri filtrelenir** (`SAMPLE_ALL_FRAMES 0` iken).
- **Sadece main thread** sample'lanır.
- **Thread listesi attach anında alınır**, sonradan açılan thread'ler görünmez.
- **Multi-process debug**: `CurrentProcess` (Debug > Processes'te seçili olan) alınır, seçim yoksa listedeki ilki.
- **Breakpoint'te duruş**: sample'lar aynı stack'i tekrarlar, chart'ta geniş kutu olur. Hata değil.

### 6.2 `FlameChartView` satır numarası düzeltmesi

Eski kural: `sx >= nameEnd` — segment fonksiyon isminin sağında başlamıyorsa numara çizilmiyordu.

Sorun: fonksiyon ismi kutunun soluna değil **görünür alanın soluna** yapışıyor (sticky label). Ekranı kaplayan bir kutuda isim hep viewport'un solunda durur, izlemekte olduğun segment ise çok önce başlamıştır → numara hiç çizilmez.

Yeni kural: numara düşürülmüyor, ismin sağına itiliyor (`textX = Math.Max(sx + 2, nameEnd)`), segmentin kalanı sığdırabiliyorsa çiziliyor.

Bu **sadece çizim** düzeltmesi — veri her zaman doğruydu, kutuya tıklayınca sol panelde görünüyordu.

---

## 7. Anahtarlar / geri alma noktaları

### `SAMPLE_ALL_FRAMES` (`Sampler.cpp` başı)

```cpp
#define SAMPLE_ALL_FRAMES 1
```

- `1` — sistem DLL frame'leri dahil her şey yazılır (ntdll, kernel32, CRT)
- `0` — sadece target exe aralığındaki frame'ler

> **Şu an `1`.** Teslim/demo için **`0` yap** — grafik çok daha okunaklı, JSONL satırları küçülür, disk ve parse maliyeti düşer.

### `FindFirstAppFrame` (`SamplerWindowControl.xaml.cs`, `ReadNewSamples` içinde)

Filtre **iki uçta** çalışıyor:

| Uç | Kesen | Kestiği |
|---|---|---|
| Üst | `IsAppAddress` (C++) | `getchar` → ucrtbase → ntdll |
| Alt | `FindFirstAppFrame` (C#) | `RtlUserThreadStart` → `BaseThreadInitThunk` → `mainCRTStartup` |

Hiçbir şeyin kesilmediği hal için `SAMPLE_ALL_FRAMES 1` **ve** şu iki satırın yorumlanması gerekir:

```csharp
int first = FindFirstAppFrame(frames);
if (first > 0) frames.RemoveRange(0, first);
```

Sadece birini yaparsan "kaldırdım ama değişmedi" izlenimi oluşur.

---

## 8. Tanılama yöntemleri

1. **Sampler konsolunu aç** — `Activate()` içinde `CreateNoWindow = false`. Attach hatalarının tamamı stderr'de.
2. **JSONL'i elle incele** — `%TEMP%\flamecharter\samples.jsonl`. İlk satır meta olmalı; frame'siz satır varsa parse tarafına bak.
3. **Sampler'ı tek başına çalıştır** — `Sampler.exe <pid> out.jsonl 10` (stop event'siz). Extension'ı denklemden çıkarır.
4. **PID doğrula** — Debug > Windows > Processes'teki PID ile `txtTarget`'taki aynı mı.
5. **Orphan kontrolü** — Task Manager'da `Sampler.exe` kalıyor mu (§4.1 hook'u test eder).

## 9. Regresyon testi sırası

Basit bir C++ console app (Debug|x64, döngü + `getchar()`):

1. VSIX derleniyor mu
2. `Sampler.exe` derleniyor mu
3. F5 → Activate → sample sayısı artıyor
4. Deactivate → **target çalışmaya devam ediyor** (donma testi)
5. Activate → Deactivate → **tekrar Activate** (§4.2 testi)
6. `getchar()`'da `main` kutusu genişlemeye devam ediyor
7. Enter → `Target exited - N samples`
8. Static lib projesi ekle → frame'leri görünüyor
9. Activate → pencereyi kapat → Task Manager'da `Sampler.exe` yok

## 10. Kapanmamış işler

- `SAMPLE_ALL_FRAMES` teslim öncesi `0`'a çekilmeli
- x86 desteği yok (isteniyorsa: `#ifdef _WIN64` ile register makroları + ayrı `Sampler32.exe` + C# tarafında seçim; ayrıca `IsMainThread` içindeki `DWORD64 startAddr` → `ULONG_PTR` olmalı, 32-bit'te boyut kontrolü hep fail eder)
- DLL frame'leri için `EnumProcessModules` ile çoklu aralık listesi
- Public repo öncesi kurum izni (bkz. ayrı konu)

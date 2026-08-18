# FlameCharter — İsim, İkon ve Açıklama Değişiklikleri

Şablondan gelen `SamplerWindow` adının ve varsayılan ikonun, projenin kendi
kimliğiyle değiştirilmesi.

## 1. İsimlendirme kararı

Görünen tüm metinlerde `FlameCharter` kullanıldı. `FCCPP` gibi bir kısaltma
elendi: namespace zaten `FlameCharter` ve kısaltma, aracı ilk kez gören birine
hiçbir şey anlatmıyor.

**Sınıf adları değiştirilmedi.** `SamplerWindow`, `SamplerWindowControl` gibi
tipleri yeniden adlandırmak `.vsct` sembollerine, `ProvideToolWindow`
attribute'una, XAML'deki `x:Class`'a ve `.csproj` girdilerine dokunmak demek.
Kullanıcının hiç görmediği bir şey için kırılma riski yüksek, kazanç sıfır.
Değiştirilen yerler yalnızca ekranda görünen dizeler oldu.

## 2. Araç penceresi başlığı

`SamplerWindow.cs`, `ToolWindowPane` yapıcısı:

```csharp
this.Caption = "FlameCharter";
```

Pencerenin sekmesinde görünen isim budur.

## 3. Menü komutu

`FlameCharterPackage.vsct`, `<Buttons>` bölümü:

```xml
<ButtonText>FlameCharter</ButtonText>
```

Komutun `Parent` değeri `IDG_VS_TOOLS_EXT_CUST`, yani araç **Tools** menüsü
altında açılıyor.

## 4. İkon tasarımı

Üç PNG üretildi:

| Dosya | Boyut | Kullanım |
|---|---|---|
| `Icon.png` | 90×90 | Manifest `<Icon>`, Extensions listesi |
| `PreviewImage.png` | 200×200 | Manifest `<PreviewImage>` |
| `FlameCharterCommand.png` | 16×16 | `.vsct` komut ikonu |

Tasarım: dört katman hâlinde üst üste dizilmiş dikdörtgenler, altta koyu
kırmızıdan (`#C62828`) üstte amber'a (`#FBC02D`) giden geçiş. Üst sıralar ayrı
kutulara bölündü — düz bir üçgen yerine flame chart'a benzemesi için.

16×16 sürüm ölçekleyerek değil, tam piksel koordinatlarıyla çizildi; küçültme
sonucu antialiasing yüzünden soluk çıkıyordu.

## 5. VSCT bitmap hatası

İkon dosyası değiştirilince derleme bitmap hatası verdi.

**Sebep:** `usedList` altı ikon bildiriyordu, yani VSCT derleyicisi 96×16'lık
bir şerit bekliyordu. Şablonun orijinal PNG'si altı ikonluk bir şeritti, yeni
dosya ise tek ikon (16×16) olduğu için beş slot eksik kaldı.

**Çözüm:** `usedList` yalnızca kullanılan ikona indirildi.

```xml
<Bitmap guid="guidImages" href="Resources\FlameCharterCommand.png" usedList="bmpPic1"/>
```

`<Symbols>` içindeki kullanılmayan `IDSymbol` satırları zararsız olduğu için
silinmedi.

Alternatif olarak ilk slotu dolu, kalan beşi şeffaf bir 96×16 şerit de
üretilebilirdi; o durumda `.vsct` hiç düzenlenmezdi.

## 6. Manifest

`source.extension.vsixmanifest`, `<Metadata>` bölümü:

```xml
<DisplayName>FlameCharter</DisplayName>
<Description xml:space="preserve">Sampling profiler for C++ projects, inside Visual Studio. Runs the startup project under a stack sampler and draws the result as a live flame chart: each box is a function, its width is the time spent there, and boxes stacked downwards are the call chain. Line level slices show which source line the time went to, and a double click opens that line in the editor.</Description>
<Icon>Resources\Icon.png</Icon>
<PreviewImage>Resources\PreviewImage.png</PreviewImage>
<Tags>profiler, flame chart, C++, performance, sampling</Tags>
```

Sıralama önemli: `<Icon>` ve `<PreviewImage>`, `<Description>`'dan sonra gelir.

`<Identity>` içindeki `Publisher` alanı da şablon varsayılanından değiştirildi;
Extensions listesinde yayıncı sütununda bu değer görünüyor.

## 7. Dosya özellikleri

PNG'ler projenin `Resources` klasörüne kondu. Manifestin gösterdiği iki dosya
için Solution Explorer > dosya seç > **F4**:

- **Build Action** = `Content`
- **Include in VSIX** = `True`

İkincisi varsayılan olarak kapalı geliyor ve **asıl tuzak bu**: kapalıyken
derleme sorunsuz tamamlanır, dosya sadece paketin içine girmez. Belirtisi çok
belirgin — açıklama değişir ama ikon varsayılan hâlinde kalır, çünkü açıklama
metni manifestin içinde durur, ikon ise ayrı bir dosyaya referanstır.

`FlameCharterCommand.png` bu ayara ihtiyaç duymaz: onu VSCT derleyicisi derleme
anında okuyup `.ctmenu` kaynağına gömer.

## 8. Doğrulama

Deneysel örnekte ikon güncellenmezse, deploy klasörüne bakılır:

```
%LOCALAPPDATA%\Microsoft\VisualStudio\17.14_<hash>Exp\Extensions\FlameCharter\FlameCharter\1.0\
```

`Resources\Icon.png` orada yoksa sorun paketlemededir. Varsa ve ikon yine eskiyse
önbellek takılmıştır: manifestteki `Version` artırılır, gerekirse "Reset the
Visual Studio Experimental Instance" kısayolu çalıştırılır.

Derlenen `.vsix` dosyasının kopyası `.zip` yapılıp Gezgin'de açılarak içeriği de
doğrulanabilir; ek yazılım gerektirmez.

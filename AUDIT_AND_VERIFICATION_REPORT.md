# QOVX v4.0.1.0 — KAPSAMLI KOD DENETİMİ, DERLEME HATASI ÇÖZÜMÜ, GÜVENLİK VE MSIX DOĞRULAMA RAPORU

- **Proje Adı:** QOVX (`System & Network Control Suite`)
- **Geliştirici / Yayıncı:** ALLAHVERDİ CAN (`https://www.youtube.com/@ALLAHVERDICAN`)
- **Microsoft Store Ürün Kimliği (Store ID):** `9P6PL8R5TLKN` (`https://apps.microsoft.com/detail/9P6PL8R5TLKN`)
- **Package/Identity/Name:** `ALLAHVERDCAN.QOVX`
- **Package/Identity/Publisher:** `CN=80F86ABB-4ABB-4524-A032-0CE77FF05712`
- **Package/Properties/PublisherDisplayName:** `ALLAHVERDİ CAN`
- **Hedef Sürüm:** `4.0.1.0` (Önceki Store Sürümü: `2.0.4.0`)
- **Hedef Çerçeve & Mimari:** `.NET 10 LTS` (`net10.0-windows10.0.19041.0`), `win-x64`, `UseWPF=true`, `Nullable=enable`, `ImplicitUsings=enable`

## 1. Giderilen 25 Derleme Hatasının (CS1010, CS1003, CS1525, CS1026, CS8087, CS1009) Kök Neden ve Çözüm Tablosu

| Dosya ve Satır | Hata Kodu | Kök Neden | Uygulanan Kesin Çözüm | Durum |
| :--- | :--- | :--- | :--- | :--- |
| `App.xaml.cs(31,42)` – `(36,98)` (9 Hata) | `CS1010`, `CS1003`, `CS1525`, `CS1026` | `SanitizeLogField` içindeki `input.Replace("\r", " ").Replace("\n", " | ")` satırı oluşturulurken kaçış dizilerinin gerçek satır sonuna (CR/LF) dönüşmesi ve C# string sabitinin 3 satıra bölünmesi. | `App.xaml.cs` içindeki kaçış dizileri tek satırlık geçerli C# string sabiti (`input.Replace("\r", " ").Replace("\n", " | ")`) olarak onarıldı ve karakter bazlı C# leksikal denetleyicisiyle doğrulandı. | **DOĞRULANDI** |
| `MainWindow.xaml.cs(2111,23)` – `(2116,31)` (14 Hata) | `CS1010`, `CS1003` | `BtnResetWinsock_Click` içindeki `MessageBox.Show` onay metninde `\n\n` kaçış dizilerinin gerçek satır kırılmasına dönüşerek C# string sabitini bölmesi. | `BtnResetWinsock_Click` onay diyaloğundaki TR/EN string sabitleri tek satırlık `\n\n` kaçış dizileriyle onarıldı. | **DOĞRULANDI** |
| `MainWindow.xaml.cs(2417,76)` (2 Hata) | `CS8087`, `CS1009` | `BtnApplyPrivacy_Click` içindeki `$"{t.SubKeyPath}\{t.ValueName}: {ex.Message}"` ifadesinde tek ters eğik çizginin (`\{`) geçersiz kaçış dizisi oluşturması. | İnterpolasyonlu string yerine güvenli string birleştirme (`t.SubKeyPath + "/" + t.ValueName + ": " + ex.Message`) kullanıldı; tüm `.cs` dosyalarında geçersiz kaçış dizisi kalmadığı doğrulandı. | **DOĞRULANDI** |

## 2. Modül Bazlı Güvenlik, Kararlılık ve Mimari İyileştirmeler Özeti

1. **Uygulama Yaşam Döngüsü ve Loglama (`App.xaml.cs`):**
   - `OutOfMemoryException`, `StackOverflowException`, `AccessViolationException`, `BadImageFormatException`, `InvalidProgramException` ve `SEHException` gibi kritik durum bozulmaları `IsFatalCorruptedStateException` ile ayrıştırılarak `Environment.FailFast` ile güvenli sonlandırma sağlandı; yalnızca kurtarılabilir UI hataları yakalanmaktadır.
   - `%LOCALAPPDATA%\QOVX_Data\qovx_diagnostics.log` dosyası 512 KB boyut sınırına (`MaxLogFileBytes`) ulaştığında `qovx_diagnostics.old.log` olarak döndürülür (log rotation) ve kullanıcı profil yolu (`%USERPROFILE%`, `%USERNAME%`) ile CR/LF karakterleri maskelenir.
2. **RAM ve Süreç Yönetimi (`MainWindow.xaml.cs`):**
   - `ProcessRecord` modeli (`ProcessId`, `ProcessName`, `WorkingSetBytes`, `StartTimeTicks`) ile `LstTopProcesses` bağlandı.
   - `BtnTrimSelectedProc_Click` içinde süreç `Process.GetProcessById(rec.ProcessId)` ile yeniden açılır, süreç adı ve `StartTimeTicks` kontrol edilir. **`StartTimeTicks <= 0` ise veya başlangıç zamanı okunamazsa PID yeniden kullanım (PID reuse) riskine karşı işlem güvenli şekilde iptal edilir.**
3. **Disk Temizliği ve SHA-256 Kopya Dosya Denetimi (`MainWindow.xaml.cs`):**
   - `Current Cache Size`, `Actually Freed Space` ve `Duplicate Potential Savings` metrikleri 3 ayrı kartta tutulur.
   - Geri Dönüşüm Kutusu'na (`SHFileOperation` + `FOF_ALLOWUNDO`) taşınan dosyalar `Actually Freed Space` sayacına **eklenmez** (`_lastRecycledBytes` olarak ayrı izlenir).
   - Kopya silinmeden hemen önce **hem orijinal dosyanın hem de kopya dosyanın** varlığı, boyutu, `ReparsePoint` durumu, NTFS dosya kimliği (`GetFileInformationByHandle` ile aynı fiziksel dosya/hardlink olup olmadığı) ve anlık SHA-256 özeti yeniden doğrulanır.
4. **Ağ, Çoklu Örneklem UDP/TCP DNS Kıyaslama ve Onarım (`MainWindow.xaml.cs`):**
   - DNS kıyaslaması 5 sağlayıcı için 3'er örneklem üzerinden **Min / Medyan / Maks** hesaplar; rastgele `Transaction ID`, `QR=1`, `RCODE=0`, `QDCOUNT=1`, `ANCOUNT>=1` ve soru bölümü (`example.com` QNAME/QTYPE/QCLASS) doğrulaması yapar.
   - UDP/53 zaman aşımında veya `TC=1` bitinde RFC 1035 Bölüm 4.2.2 uyumlu **TCP/53 yedeklemesi** (`ReadExactAsync` döngüsüyle 2 bayt uzunluk öneki + tam gövde okuma) devreye girer.
   - `BtnTestPing_Click`, `BtnFlushDns_Click` ve `BtnResetWinsock_Click` ortak `_netBusyFlag` (`Interlocked`) kilidiyle korunur.
5. **Gizlilik, Windows Registry Yedekleme ve Geri Yükleme (`MainWindow.xaml.cs`):**
   - Herhangi bir HKCU Registry anahtarı değiştirilmeden önce mevcut durum `privacy_backup_v1.json` dosyasına atomik olarak (`temp` + `File.Move`) kaydedilir ve okunarak doğrulanır (`SavePrivacyBackupStoreVerified`). **Yedek diske doğrulanarak yazılamazsa hiçbir Registry değişikliği uygulanmaz.**
   - Geri yükleme (`BtnRevertPrivacy_Click`) sırasında değerin QOVX sonrasında harici olarak değiştirilip değiştirilmediği kontrol edilir; önceden var olmayan değerler sentetik `1` yazmak yerine `DeleteValue` ile silinir ve işlem sonrasında anahtar yeniden okunarak doğrulanır.
6. **Güç Planları, Uyku Kilidi ve Kapatma Zamanlayıcısı (`MainWindow.xaml.cs`):**
   - `powercfg /list` ile mevcut şemalar taranmadan `/duplicatescheme` çağrılmaz; `/setactive` sonrasında `/getactivescheme` çıktısı okunarak aktif GUID doğrulanır.
   - `_qovxScheduledShutdownUtc` ile QOVX tarafından başlatılan kapatma zamanlayıcısı izlenir; aktif bir QOVX zamanlayıcısı yoksa `shutdown /a` çalıştırmadan önce kullanıcıdan onay alınır.
   - Pencere kapanırken (`MainWindow_Closing`) aktif `SetThreadExecutionState` uyku kilidi (`ES_CONTINUOUS`) otomatik sıfırlanır.

## 3. Doğrulama Durumu Özeti

| Kontrol Kalemi | Durum | Açıklama |
| :--- | :--- | :--- |
| 25 C# Derleme Hatasının (CS1010/CS1003/CS1525/CS1026/CS8087/CS1009) Giderilmesi | **DOĞRULANDI** | Python tabanlı C# string/char/escape leksikal ayrıştırıcısı ile `App.xaml.cs` ve `MainWindow.xaml.cs` üzerinde 0 hata doğrulandı. |
| XAML ve C# Olay (`Click`) / Kontrol (`x:Name`) Eşleşmesi | **DOĞRULANDI** | 25 `Click` olay işleyicisi ve 96 `x:Name` kontrolünün tamamı `MainWindow.xaml` ve `MainWindow.xaml.cs` arasında birebir doğrulandı. |
| Microsoft Store Sabit Kimlik Doğrulaması (`Package.appxmanifest`) | **DOĞRULANDI** | `ALLAHVERDCAN.QOVX`, `CN=80F86ABB-4ABB-4524-A032-0CE77FF05712`, `ALLAHVERDİ CAN`, `Version="4.0.1.0"` doğrulandı. |
| MSIX Görsel Varlıkları (PNG İmza ve Boyut Kontrolü) | **DOĞRULANDI** | `StoreLogo.png` (50x50), `Square44x44Logo.png` (44x44), `Square150x150Logo.png` (150x150), `Wide310x150Logo.png` (310x150), `SplashScreen.png` (620x300) doğrulandı. |
| Fiziksel Windows 10/11 Ortamında `dotnet publish` ve `makeappx pack` | **DOĞRULANAMADI** | Bulut inceleme ortamı Linux tabanlı olduğundan fiziksel `dotnet.exe` ve `makeappx.exe` çalıştırması kullanıcı bilgisayarında `build-msix.bat` ile yapılmalıdır. |

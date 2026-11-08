# QOVX — Windows Performans ve Sistem Kontrol Merkezi (v4.0.1.0)

**Yayıncı:** ALLAHVERDİ CAN  
**Paket Kimliği:** `ALLAHVERDCAN.QOVX`  
**Mimari:** `.NET 10 LTS` (`net10.0-windows10.0.19041.0`), `x64` Yerel Windows Masaüstü Uygulaması  

## Temel Özellikler

1. **Akıllı Mod (Basit Mod) ve Gelişmiş Mod (Pro) Ayrımı:**
   - **Basit Mod (Varsayılan):** Açılışta karmaşık tablolar yerine sade durum özeti, nefes alma (pulse) ve dönüş animasyonlu **"Sistemi Optimize Et"** merkez butonu ve işlem sonunda **Fade-In & Scale-Up** animasyonlu **Şeffaf Kazanım Raporu** sunar.
   - **Gelişmiş Mod (Pro):** Üst bardaki mod anahtarıyla tek tıkla 6 sekmeli teknik kontrol merkezine geçiş sağlar.
2. **Kapsamlı Performans Modülleri:**
   - **Akıllı Arka Plan Çöp Temizliği:** `%TEMP%`, DirectX Shader önbelleği (`D3DSCache`), kapalı tarayıcı önbellekleri ve uygulama çökme dökümlerini (`CrashDumps` ve `WER`) sembolik bağlantıları atlayarak temizler; `>= 1 KB` SHA-256 birebir kopya dosyaları tespit eder.
   - **Bellek (RAM) Çalışma Kümesi Yönetimi:** Kritik işletim sistemi süreçlerini koruyarak kullanıcı uygulamalarının pasif bellek sayfalarını native `EmptyWorkingSet` (`psapi.dll`) ile daraltır.
   - **Başlangıç Yöneticisi & Görev Zamanlayıcısı Denetimi:** `HKCU/HKLM Run`, `RunOnce` ve Başlangıç klasörlerini listeler; kullanıcı (`HKCU Run`) başlangıç öğelerini yerel yedeklemeli olarak tek tıkla duraklatıp etkinleştirir ve Görev Zamanlayıcısı üzerindeki üçüncü taraf görevleri denetler.
   - **Görsel Performans Ayarları & Güç Planları:** Hantal Windows saydamlık ve animasyon efektlerini yedekleyerek "En İyi Performans" moduna geçirir veya varsayılan kaliteye döndürür; Nihai Performans güç planını ve Uyku Kilidini (`SetThreadExecutionState`) yönetir.
3. **Kulak Tırmalamayan Akustik Geri Bildirim (`SoundManager`):**
   - Harici ses dosyası kullanmadan, bellek üzerinde düşük frekanslı yumuşak dokunsal tıklama (`PlayClick`) ve tamamlanma akoru (`PlaySuccessChord`) üretir.

## Tek Tıkla MSIX Paketleme

Proje kök dizinindeki `build-msix.bat` (veya `TEK_TIKLA_MSIX_OLUSTUR.bat`) betiğine çift tıklayarak `ALLAHVERDCAN.QOVX_4.0.1.0_x64.msix` paketini doğrudan oluşturabilirsiniz.

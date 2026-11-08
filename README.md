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
Türkçe (Turkish)
QOVX – Gizlilik PolitikasıSon Güncelleme Tarihi: 8 Kasım 2026

QOVX (“Biz”, “Uygulama”), kullanıcılarımızın gizliliğine büyük bir önem verir. Bu gizlilik politikası, QOVX masaüstü uygulamasını kullandığınızda verilerinizin nasıl işlendiğini açıklamaktadır.

Veri Toplamama ve Yerel Çalışma: QOVX, tamamen yerel bir Windows (.NET 10 / WinUI 3) sistem optimizasyon aracıdır. Uygulama, cihazınızda gerçekleştirilen tüm bellek yönetimi, önbellek temizliği, başlangıç denetimi ve ağ işleçlerini yalnızca yerel sisteminizde yürütür.

Telemetri ve Bulut Aktarımı Yoktur: Uygulama hiçbir surette kişisel verilerinizi, sistem kullanım alışkanlıklarınızı veya telemetri bilgilerini toplamaz, kaydetmez, analiz etmez veya harici sunuculara (buluta) aktarmaz. Tüm işlemler sizin kontrolünüzde, tamamen çevrimdışı ve güvenli bir ortamda gerçekleşir.

İletişim: Gizlilik politikamız hakkında herhangi bir sorunuz olması durumunda, uygulama mağazası geliştirici profili üzerinden bizimle iletişime geçebilirsiniz.

İngilizce (English)
QOVX – Privacy PolicyEffective Date: November 8, 2026

QOVX ("We", "Our", or "The Application") is committed to protecting your privacy. This privacy policy outlines how your data is handled when you use the QOVX desktop application.

Zero Data Collection & Local Operation: QOVX is a native Windows (.NET 10 / WinUI 3) system optimization utility. All memory management, cache cleanup, startup auditing, and network operations performed by the application are executed strictly locally on your device.

No Telemetry or Cloud Transmission: The application does not collect, store, analyze, or transmit any personal data, system usage habits, or telemetry information to external servers or the cloud. Everything operates entirely offline under your direct control.

Contact: If you have any questions or concerns regarding our privacy practices, you may reach out to us via the developer profile on the application store.

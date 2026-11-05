using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Qovx
{
    public partial class MainWindow : Window
    {
        private bool isEnglish = false;
        private bool isAwakeLocked = false;
        private DispatcherTimer telemetryTimer;
        private List<string> detectedDuplicatePaths = new List<string>();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        private static extern UInt32 DnsFlushResolverCache();

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public MainWindow()
        {
            InitializeComponent();
            UpdateRealHardwareMetrics();
            ScanTempSizeAsync();

            telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            telemetryTimer.Tick += (s, e) => UpdateRealHardwareMetrics();
            telemetryTimer.Start();
        }

        private void UpdateRealHardwareMetrics()
        {
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                    double usedGb = totalGb - availGb;
                    uint loadPct = memStatus.dwMemoryLoad;

                    ValRamPercent.Text = $"{loadPct}%";
                    ValRamBytes.Text = $"{usedGb:F1} GB / {totalGb:F1} GB";
                    TxtRamDetail.Text = isEnglish
                        ? $"Real Physical RAM: {usedGb:F2} GB in use out of {totalGb:F2} GB ({loadPct}% load) | Logical CPU Cores: {Environment.ProcessorCount}"
                        : $"Gerçek Fiziksel Bellek: {usedGb:F2} GB kullanılan / {totalGb:F2} GB toplam (%{loadPct} yük) | Mantıksal Çekirdek: {Environment.ProcessorCount}";

                    if (ChkAutoRam?.IsChecked == true && loadPct >= 82)
                    {
                        PurgeWorkingSets();
                    }
                }
            }
            catch { }
        }

        private async void ScanTempSizeAsync()
        {
            long totalBytes = await Task.Run(() =>
            {
                long b = 0;
                b += GetDirSize(Path.GetTempPath());
                b += GetDirSize(@"C:\Windows\Temp");
                string d3dCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
                b += GetDirSize(d3dCache);
                return b;
            });

            double mb = totalBytes / (1024.0 * 1024.0);
            ValTempSize.Text = mb >= 1024 ? $"{(mb / 1024.0):F2} GB" : $"{mb:F0} MB";
        }

        private void BtnLang_Click(object sender, RoutedEventArgs e)
        {
            isEnglish = !isEnglish;
            ApplyLocalization();
            UpdateRealHardwareMetrics();
        }

        private void ApplyLocalization()
        {
            TxtLangLabel.Text = isEnglish ? "Language: EN (Switch to TR)" : "Dil: TR (EN'ye Geç)";
            TxtZeroTelemetry.Text = isEnglish ? "● 100% Offline • Zero Telemetry" : "● %100 Çevrimdışı • Sıfır Telemetri";

            NavTxtDash.Text = isEnglish ? "System Overview & Boost" : "Sistem Özeti & Hızlandır";
            NavTxtRam.Text = isEnglish ? "Memory (RAM) Control" : "Bellek (RAM) Yönetimi";
            NavTxtDisk.Text = isEnglish ? "Storage & Duplicate Cleaner" : "Disk & Kopya Temizleyici";
            NavTxtNet.Text = isEnglish ? "Network, DNS & Latency" : "Ağ, DNS & Gecikme";
            NavTxtPriv.Text = isEnglish ? "Privacy & Telemetry" : "Gizlilik & Telemetri";
            NavTxtStart.Text = isEnglish ? "Power, Awake & Tools" : "Güç, Uyku Kilidi & Araçlar";

            TxtDashTitle.Text = isEnglish ? "System Performance & Control Panel" : "Sistem Performans & Kontrol Paneli";
            TxtDashSubtitle.Text = isEnglish
                ? "Inspect real hardware telemetry locally and execute one-step system maintenance."
                : "Gerçek donanım verilerini yerel olarak inceleyin ve tek adımla sistem bakımını çalıştırın.";
            CardTxtRam.Text = isEnglish ? "Physical Memory Load" : "Fiziksel Bellek Yükü";
            CardTxtTrash.Text = isEnglish ? "Reclaimable Cache Size" : "Temizlenebilir Önbellek";
            CardTxtDns.Text = isEnglish ? "Active DNS Latency" : "Aktif DNS Yanıt Süresi";
            TxtBtnMaster.Text = isEnglish ? "RUN FULL SYSTEM OPTIMIZATION" : "SİSTEM OPTİMİZASYONUNU BAŞLAT";

            TxtRamTitle.Text = isEnglish ? "Memory (RAM) Working Set Control" : "Bellek (RAM) Çalışma Kümesi Yönetimi";
            TxtBtnPurgeRam.Text = isEnglish ? "Trim Memory Working Sets Now" : "Bellek Çalışma Kümesini Şimdi Boşalt";
            ChkAutoRam.Content = isEnglish ? "Auto-Trim RAM when load exceeds 82%" : "Bellek yükü %82'yi aştığında otomatik boşalt";

            TxtDiskTitle.Text = isEnglish ? "Storage Cleanup & SHA-256 Duplicate Scanner" : "Depolama Temizliği & SHA-256 Kopya Dosya Tarayıcı";
            TxtBtnScanDisk.Text = isEnglish ? "Scan Cache & Duplicates" : "Önbellek ve Kopyaları Tara";
            TxtBtnCleanDisk.Text = isEnglish ? "Clean Selected Caches" : "Seçili Önbellekleri Temizle";

            TxtNetTitle.Text = isEnglish ? "Network Diagnostics, DNS Benchmark & Socket Reset" : "Ağ Tanılama, DNS Kıyaslama & Soket Onarımı";
            TxtBtnTestPing.Text = isEnglish ? "Run Live DNS Ping Test" : "Canlı DNS Gecikme Testini Başlat";
            TxtBtnFlushDns.Text = isEnglish ? "Flush DNS & ARP Cache" : "DNS ve ARP Önbelleğini Temizle";

            TxtPrivTitle.Text = isEnglish ? "Privacy Controls & Telemetry Restriction" : "Gizlilik Ayarları & Telemetri Sınırlandırma";
            TxtBtnApplyPrivacy.Text = isEnglish ? "Apply Registry Privacy Rules" : "Kayıt Defteri Gizlilik Kurallarını Uygula";

            TxtStartTitle.Text = isEnglish ? "Power Schemes, Awake Lock & Shutdown Timer" : "Güç Planları, Uyku Kilidi & Zamanlayıcı";
        }

        private void BtnYouTube_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.youtube.com/@ALLAHVERDICAN") { UseShellExecute = true });
            }
            catch { }
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                TabDash.Visibility = tag == "Dash" ? Visibility.Visible : Visibility.Collapsed;
                TabRam.Visibility = tag == "Ram" ? Visibility.Visible : Visibility.Collapsed;
                TabDisk.Visibility = tag == "Disk" ? Visibility.Visible : Visibility.Collapsed;
                TabNet.Visibility = tag == "Net" ? Visibility.Visible : Visibility.Collapsed;
                TabPriv.Visibility = tag == "Priv" ? Visibility.Visible : Visibility.Collapsed;
                TabStart.Visibility = tag == "Start" ? Visibility.Visible : Visibility.Collapsed;

                Button[] all = { NavDash, NavRam, NavDisk, NavNet, NavPriv, NavStart };
                foreach (var b in all)
                {
                    b.Background = b == btn ? (SolidColorBrush)FindResource("CardDarkHover") : Brushes.Transparent;
                    b.BorderBrush = b == btn ? (SolidColorBrush)FindResource("AccentCyan") : Brushes.Transparent;
                    b.BorderThickness = new Thickness(b == btn ? 1 : 0);
                }
            }
        }

        private long PurgeWorkingSets()
        {
            long freedBytes = 0;
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        long before = p.WorkingSet64;
                        EmptyWorkingSet(p.Handle);
                        p.Refresh();
                        long after = p.WorkingSet64;
                        if (before > after) freedBytes += (before - after);
                    }
                    catch { }
                }
            }
            catch { }
            return freedBytes / (1024 * 1024);
        }

        private void BtnPurgeRam_Click(object sender, RoutedEventArgs e)
        {
            long freedMb = PurgeWorkingSets();
            UpdateRealHardwareMetrics();
            MessageBox.Show(isEnglish
                ? $"Memory working sets trimmed via Win32 EmptyWorkingSet! Reclaimed ~{freedMb} MB physical RAM."
                : $"Win32 EmptyWorkingSet ile çalışma kümeleri daraltıldı! Yaklaşık ~{freedMb} MB fiziksel RAM boşaltıldı.",
                "QOVX", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnMasterBoost_Click(object sender, RoutedEventArgs e)
        {
            PbBoost.Visibility = Visibility.Visible;
            PbBoost.IsIndeterminate = true;
            BtnMasterBoost.IsEnabled = false;

            long freedRam = 0;
            int deletedFiles = 0;

            await Task.Run(() =>
            {
                freedRam = PurgeWorkingSets();
                try { DnsFlushResolverCache(); } catch { }
                deletedFiles += CleanDirectory(Path.GetTempPath());
                deletedFiles += CleanDirectory(@"C:\Windows\Temp");
                string d3d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
                deletedFiles += CleanDirectory(d3d);
            });

            PbBoost.Visibility = Visibility.Collapsed;
            BtnMasterBoost.IsEnabled = true;
            UpdateRealHardwareMetrics();
            ScanTempSizeAsync();

            TxtBoostStatus.Text = isEnglish
                ? $"Completed: ~{freedRam} MB RAM reclaimed, {deletedFiles} cache files removed, DNS flushed."
                : $"Tamamlandı: ~{freedRam} MB RAM boşaltıldı, {deletedFiles} önbellek dosyası silindi, DNS sıfırlandı.";
        }

        private async void BtnScanDisk_Click(object sender, RoutedEventArgs e)
        {
            TxtDiskResult.Text = isEnglish ? "Scanning directories and SHA-256 hashes..." : "Dizinler ve SHA-256 dosya özetleri taranıyor...";
            long tempBytes = 0;
            int dupCount = 0;

            await Task.Run(() =>
            {
                if (ChkUserTemp.IsChecked == true) tempBytes += GetDirSize(Path.GetTempPath());
                if (ChkWinTemp.IsChecked == true) tempBytes += GetDirSize(@"C:\Windows\Temp");
                if (ChkThumbnails.IsChecked == true)
                {
                    string d3d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
                    tempBytes += GetDirSize(d3d);
                }

                detectedDuplicatePaths.Clear();
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloads))
                {
                    var hashes = new HashSet<string>();
                    using var sha = SHA256.Create();
                    foreach (var file in Directory.GetFiles(downloads))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.Length > 0 && fi.Length < 50 * 1024 * 1024)
                            {
                                using var stream = File.OpenRead(file);
                                string h = BitConverter.ToString(sha.ComputeHash(stream));
                                if (!hashes.Add(h))
                                {
                                    detectedDuplicatePaths.Add(file);
                                    dupCount++;
                                }
                            }
                        }
                        catch { }
                    }
                }
            });

            double mb = tempBytes / (1024.0 * 1024.0);
            TxtDiskResult.Text = isEnglish
                ? $"Scan finished: {mb:F2} MB temporary cache + {dupCount} identical duplicate file(s) in Downloads."
                : $"Tarama tamamlandı: {mb:F2} MB geçici önbellek + İndirilenler klasöründe {dupCount} adet birebir kopya dosya bulundu.";
        }

        private void BtnCleanDisk_Click(object sender, RoutedEventArgs e)
        {
            int count = 0;
            if (ChkUserTemp.IsChecked == true) count += CleanDirectory(Path.GetTempPath());
            if (ChkWinTemp.IsChecked == true) count += CleanDirectory(@"C:\Windows\Temp");
            if (ChkThumbnails.IsChecked == true)
            {
                string d3d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
                count += CleanDirectory(d3d);
            }

            ScanTempSizeAsync();
            TxtDiskResult.Text = isEnglish
                ? $"Cleaned {count} temporary cache files."
                : $"{count} adet geçici önbellek dosyası temizlendi.";
        }

        private long GetDirSize(string path)
        {
            long size = 0;
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var f in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
                    {
                        try { size += new FileInfo(f).Length; } catch { }
                    }
                }
            }
            catch { }
            return size;
        }

        private int CleanDirectory(string path)
        {
            int deleted = 0;
            try
            {
                if (!Directory.Exists(path)) return 0;
                foreach (var f in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(f);
                        deleted++;
                    }
                    catch { }
                }
            }
            catch { }
            return deleted;
        }

        private async void BtnTestPing_Click(object sender, RoutedEventArgs e)
        {
            TxtPingResults.Text = isEnglish ? "Running live ICMP ping test..." : "Canlı ICMP gecikme testi yapılıyor...";
            BtnTestPing.IsEnabled = false;

            var resolvers = new Dictionary<string, string>
            {
                { "Primary Edge Resolver (1.1.1.1)", "1.1.1.1" },
                { "Global Anycast Resolver (8.8.8.8)", "8.8.8.8" },
                { "Ad-Filtering Resolver (94.140.14.14)", "94.140.14.14" },
                { "Threat-Blocking Resolver (9.9.9.9)", "9.9.9.9" },
                { "Standard Open Resolver (208.67.222.222)", "208.67.222.222" }
            };

            string output = "";
            long bestPing = long.MaxValue;
            string bestName = "";

            await Task.Run(() =>
            {
                using var pinger = new Ping();
                foreach (var kv in resolvers)
                {
                    try
                    {
                        var reply = pinger.Send(kv.Value, 1000);
                        if (reply.Status == IPStatus.Success)
                        {
                            output += $"{kv.Key}: {reply.RoundtripTime} ms\n";
                            if (reply.RoundtripTime < bestPing)
                            {
                                bestPing = reply.RoundtripTime;
                                bestName = kv.Key;
                            }
                        }
                        else
                        {
                            output += $"{kv.Key}: Timeout\n";
                        }
                    }
                    catch
                    {
                        output += $"{kv.Key}: Unreachable\n";
                    }
                }
            });

            BtnTestPing.IsEnabled = true;
            if (bestPing < long.MaxValue)
            {
                ValDnsPing.Text = $"{bestPing} ms";
                ValDnsProvider.Text = bestName;
                output += isEnglish
                    ? $"\nOptimal Resolver: {bestName} ({bestPing} ms)"
                    : $"\nEn Düşük Gecikmeli Çözümleyici: {bestName} ({bestPing} ms)";
            }
            TxtPingResults.Text = output;
        }

        private void BtnFlushDns_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DnsFlushResolverCache();
                Process.Start(new ProcessStartInfo("arp", "-d *") { CreateNoWindow = true, UseShellExecute = false });
                MessageBox.Show(isEnglish ? "DNS & ARP Resolver Cache Flushed!" : "DNS ve ARP Önbelleği Temizlendi!", "QOVX");
            }
            catch { }
        }

        private void BtnApplyPrivacy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ChkAdId.IsChecked == true)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo");
                    key?.SetValue("Enabled", 0, RegistryValueKind.DWord);
                }
                if (ChkStartWeb.IsChecked == true)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Search");
                    key?.SetValue("BingSearchEnabled", 0, RegistryValueKind.DWord);
                    key?.SetValue("CortanaConsent", 0, RegistryValueKind.DWord);
                }
                if (ChkDiagnostic.IsChecked == true)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
                    key?.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
                }
                TxtPrivResult.Text = isEnglish
                    ? "Registry privacy rules applied successfully."
                    : "Kayıt defteri gizlilik kuralları başarıyla işlendi.";
            }
            catch (Exception ex)
            {
                TxtPrivResult.Text = ex.Message;
            }
        }

        private void BtnOpenStartup_Click(object sender, RoutedEventArgs e)
        {
            isAwakeLocked = !isAwakeLocked;
            if (isAwakeLocked)
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                TxtBtnOpenStartup.Text = isEnglish ? "Disable Keep-Awake Lock (Active)" : "Uyku Kilidini Kapat (Şu An Aktif)";
            }
            else
            {
                SetThreadExecutionState(ES_CONTINUOUS);
                TxtBtnOpenStartup.Text = isEnglish ? "Enable Keep-Awake Lock" : "Uyku Kilidini (Uyanık Tut) Başlat";
            }
        }

        private void BtnUnlockPower_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powercfg", "-duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                MessageBox.Show(isEnglish
                    ? "Ultimate Performance power scheme unlocked in Windows Power Options!"
                    : "Nihai Performans güç şeması Windows Güç Seçeneklerinde etkinleştirildi!", "QOVX");
            }
            catch { }
        }

        private void BtnShutdownTimer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "-s -t 3600") { CreateNoWindow = true, UseShellExecute = false });
                MessageBox.Show(isEnglish
                    ? "System scheduled to shut down in 60 minutes."
                    : "Sistem 60 dakika sonra otomatik kapanacak şekilde zamanlandı.", "QOVX");
            }
            catch { }
        }

        private void BtnCancelShutdown_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "-a") { CreateNoWindow = true, UseShellExecute = false });
                MessageBox.Show(isEnglish ? "Scheduled shutdown cancelled." : "Otomatik kapatma zamanlayıcısı iptal edildi.", "QOVX");
            }
            catch { }
        }
    }
}

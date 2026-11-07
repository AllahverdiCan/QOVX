using System.Security.Principal;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Qovx
{
    public sealed class ProcessRecord
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public long WorkingSetBytes { get; set; }
        public bool IsProtected { get; set; }
        public long StartTimeTicks { get; set; }
        public double WorkingSetMb => WorkingSetBytes / (1024.0 * 1024.0);
    }

    public sealed class DuplicateFileRecord
    {
        public string DuplicatePath { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256Hex { get; set; } = string.Empty;
        public long LastWriteUtcTicks { get; set; }
    }

    public sealed class CleanupOperationReport
    {
        public int DeletedFiles { get; set; }
        public int LockedSkippedFiles { get; set; }
        public int PermissionDeniedFiles { get; set; }
        public int RemovedEmptyDirs { get; set; }
        public int SkippedRunningBrowsers { get; set; }
        public long FreedBytes { get; set; }
        public bool WasCancelled { get; set; }
    }

    public sealed class RamTrimOperationReport
    {
        public int EvaluatedProcesses { get; set; }
        public int SuccessfulApiCalls { get; set; }
        public int ProtectedSkipped { get; set; }
        public double UsedGbBefore { get; set; }
        public double UsedGbAfter { get; set; }
        public double PhysicalDeltaMb => (UsedGbBefore - UsedGbAfter) * 1024.0;
        public bool HasRun { get; set; }
    }

    public enum CommandExecStatus
    {
        Success,
        Failed,
        TimedOut,
        AccessDenied
    }

    public sealed class CommandExecResult
    {
        public CommandExecStatus Status { get; set; }
        public int ExitCode { get; set; } = -1;
        public string StdOut { get; set; } = string.Empty;
        public string StdErr { get; set; } = string.Empty;
    }

    public enum DnsSampleStatus
    {
        Success,
        Timeout,
        InvalidResponse,
        NetworkError,
        RcodeError
    }

    public sealed class DnsProviderBenchmarkResult
    {
        public string ProviderName { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public List<long> SuccessfulLatenciesMs { get; set; } = new List<long>();
        public int FailedCount { get; set; }
        public DnsSampleStatus LastStatus { get; set; }
        public string LastStatusDetail { get; set; } = string.Empty;
        public bool UsedTcpFallback { get; set; }
        public long? IcmpRttMs { get; set; }
        public string IcmpStatusText { get; set; } = string.Empty;

        public long MinMs => SuccessfulLatenciesMs.Count > 0 ? SuccessfulLatenciesMs.Min() : -1;
        public long MaxMs => SuccessfulLatenciesMs.Count > 0 ? SuccessfulLatenciesMs.Max() : -1;
        public long MedianMs
        {
            get
            {
                if (SuccessfulLatenciesMs.Count == 0) return -1;
                var sorted = SuccessfulLatenciesMs.OrderBy(x => x).ToList();
                int count = sorted.Count;
                if (count % 2 == 1)
                    return sorted[count / 2];
                return (long)Math.Round((sorted[(count / 2) - 1] + sorted[count / 2]) / 2.0);
            }
        }
    }

    public sealed class RegistryValueSnapshot
    {
        public string Id { get; set; } = string.Empty;
        public string SubKeyPath { get; set; } = string.Empty;
        public string ValueName { get; set; } = string.Empty;
        public bool KeyExisted { get; set; }
        public bool ValueExisted { get; set; }
        public int ValueKindInt { get; set; }
        public string? RawDataString { get; set; }
        public int AppliedTargetValue { get; set; }
    }

    public sealed class PrivacyBackupStore
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, RegistryValueSnapshot> Snapshots { get; set; } = new Dictionary<string, RegistryValueSnapshot>(StringComparer.OrdinalIgnoreCase);
    }

    public partial class MainWindow : Window
    {
        private bool _isEnglish = false;
        private bool _isCaffeineActive = false;
        private bool _isSoundEnabled = true;
        private bool _isLightTheme = false;
        private volatile bool _isWindowClosing = false;

        // Thread-safe operation guards (Interlocked: 0 = idle, 1 = busy)
        private int _ramBusyFlag = 0;
        private int _diskBusyFlag = 0;
        private int _netBusyFlag = 0;

        private DateTime _lastAutoRamPurge = DateTime.MinValue;
        private DateTime? _qovxScheduledShutdownUtc = null;
        private CancellationTokenSource? _diskCts;
        private CancellationTokenSource? _appLifetimeCts = new CancellationTokenSource();
        private readonly DispatcherTimer _telemetryTimer;

        // Structured state for dynamic bilingual re-rendering
        private readonly List<ProcessRecord> _currentTopProcesses = new List<ProcessRecord>();
        private readonly List<DuplicateFileRecord> _foundDuplicates = new List<DuplicateFileRecord>();
        private readonly List<DnsProviderBenchmarkResult> _lastDnsResults = new List<DnsProviderBenchmarkResult>();
        private RamTrimOperationReport _lastRamReport = new RamTrimOperationReport();
        private long _lastMeasuredCacheBytes = 0;
        private long _lastActuallyFreedBytes = 0;
        private int _lastActuallyFreedFiles = 0;
        private long _lastDuplicatePotentialBytes = 0;

        private static readonly HashSet<string> ExcludedCriticalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit",
            "services", "lsass", "svchost", "dwm", "audiodg", "fontdrvhost", "winlogon",
            "spoolsv", "MsMpEng", "NisSrv", "SecurityHealthService", "SearchIndexer",
            "explorer", "sihost", "taskhostw", "RuntimeBroker", "ctfmon", "Qovx"
        };

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

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        private static extern uint DnsFlushResolverCache();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        public MainWindow()
        {
            InitializeComponent();
            this.Closing += MainWindow_Closing;

            UpdateRealHardwareMetrics();
            CalculateInitialTempSizeAsync();
            RefreshTopProcessesList();
            RefreshNetworkAdaptersList();
            RefreshStartupAppsList();

            _telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.0)
            };
            _telemetryTimer.Tick += (s, e) =>
            {
                if (!_isWindowClosing)
                    UpdateRealHardwareMetrics();
            };
            _telemetryTimer.Start();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _isWindowClosing = true;
            try { _telemetryTimer?.Stop(); } catch { }
            try { _diskCts?.Cancel(); _diskCts?.Dispose(); } catch { }
            try { _appLifetimeCts?.Cancel(); _appLifetimeCts?.Dispose(); } catch { }

            // Release Awake Lock if active on application exit (Section 8)
            if (_isCaffeineActive)
            {
                try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
                _isCaffeineActive = false;
            }
        }

        private void PlayUiFeedbackSound(bool isSuccess = false)
        {
            if (!_isSoundEnabled || _isWindowClosing) return;
            try
            {
                if (isSuccess)
                    System.Media.SystemSounds.Asterisk.Play();
                else
                    System.Media.SystemSounds.Beep.Play();
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("AUDIO", ex.Message);
            }
        }

        private (uint LoadPercent, double TotalGb, double AvailGb, double UsedGb) ReadPhysicalMemorySnapshot()
        {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                double usedGb = Math.Max(0.0, totalGb - availGb);
                return (memStatus.dwMemoryLoad, totalGb, availGb, usedGb);
            }
            return (0, 0, 0, 0);
        }

        private void UpdateRealHardwareMetrics()
        {
            if (_isWindowClosing || !IsLoaded) return;
            try
            {
                var (load, totalGb, availGb, usedGb) = ReadPhysicalMemorySnapshot();
                if (totalGb > 0)
                {
                    ValRamPercent.Text = $"{load}%";
                    ValRamBytes.Text = _isEnglish
                        ? $"{usedGb:F2} GB / {totalGb:F2} GB In Use"
                        : $"{usedGb:F2} GB / {totalGb:F2} GB Kullanımda";

                    TxtRamDetail.Text = _isEnglish
                        ? $"Used: {usedGb:F2} GB  |  Free: {availGb:F2} GB  |  Total: {totalGb:F2} GB (Load: {load}%)"
                        : $"Kullanılan: {usedGb:F2} GB  |  Boş: {availGb:F2} GB  |  Toplam: {totalGb:F2} GB (Yük: %{load})";

                    uint availPercent = 100 - Math.Min(100u, load);
                    ValHealthScore.Text = _isEnglish
                        ? $"{availPercent}% Free ({availGb:F1} GB)"
                        : $"%{availPercent} Boş ({availGb:F1} GB)";

                    bool autoRamChecked = ChkAutoRam.IsChecked == true;
                    if (autoRamChecked && load >= 88 && (DateTime.UtcNow - _lastAutoRamPurge).TotalMinutes >= 5.0)
                    {
                        if (Interlocked.CompareExchange(ref _ramBusyFlag, 1, 0) == 0)
                        {
                            _lastAutoRamPurge = DateTime.UtcNow;
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    var rep = ExecuteWorkingSetTrimCore();
                                    _lastRamReport = rep;
                                    SafeDispatcherInvoke(() => RenderRamLastReportText());
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _ramBusyFlag, 0);
                                }
                            });
                        }
                    }
                }

                TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                TxtUptimeInfo.Text = _isEnglish
                    ? $"Uptime: {(int)uptime.TotalHours}h {uptime.Minutes}m  |  CPUs: {Environment.ProcessorCount}"
                    : $"Çalışma Süresi: {(int)uptime.TotalHours}s {uptime.Minutes}dk  |  Çekirdek: {Environment.ProcessorCount}";

                TxtHardwareSummary.Text = _isEnglish
                    ? $"Machine: {Environment.MachineName} • OS: {Environment.OSVersion.VersionString} • 64-Bit Native Process"
                    : $"Cihaz: {Environment.MachineName} • İS: {Environment.OSVersion.VersionString} • 64-Bit Yerel Süreç";
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("METRICS", ex.Message);
            }
        }

        private void SafeDispatcherInvoke(Action action)
        {
            if (_isWindowClosing) return;
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (!_isWindowClosing && IsLoaded)
                        action();
                });
            }
            catch { }
        }

        private static string GetWindowsTempDirectoryPath()
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(winDir))
                winDir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            return Path.Combine(winDir, "Temp");
        }

        private void CalculateInitialTempSizeAsync()
        {
            CancellationToken token = _appLifetimeCts?.Token ?? CancellationToken.None;
            Task.Run(() =>
            {
                if (token.IsCancellationRequested || _isWindowClosing) return;
                long bytes = GetDirectorySizeSafe(Path.GetTempPath(), token);
                string d3dCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
                bytes += GetDirectorySizeSafe(d3dCache, token);
                bytes += GetBrowserCachesSizeSafe(token, out _);

                if (token.IsCancellationRequested || _isWindowClosing) return;
                _lastMeasuredCacheBytes = bytes;
                SafeDispatcherInvoke(() => UpdateDiskMetricsCards());
            }, token);
        }

        private void UpdateDiskMetricsCards()
        {
            double cacheMb = _lastMeasuredCacheBytes / (1024.0 * 1024.0);
            string cacheStr = cacheMb >= 1024.0 ? $"{(cacheMb / 1024.0):F2} GB" : $"{cacheMb:F1} MB";
            ValTempSize.Text = cacheStr;
            TxtMetricCurrentCache.Text = cacheStr;

            double freedMb = _lastActuallyFreedBytes / (1024.0 * 1024.0);
            TxtMetricFreedSpace.Text = _isEnglish
                ? $"{freedMb:F2} MB ({_lastActuallyFreedFiles} files)"
                : $"{freedMb:F2} MB ({_lastActuallyFreedFiles} dosya)";

            double dupMb = _lastDuplicatePotentialBytes / (1024.0 * 1024.0);
            TxtMetricDupPotential.Text = _isEnglish
                ? $"{dupMb:F2} MB ({_foundDuplicates.Count} items)"
                : $"{dupMb:F2} MB ({_foundDuplicates.Count} kopya)";
        }

        private static bool IsReparsePointOrSymlink(string path)
        {
            try
            {
                FileAttributes attr = File.GetAttributes(path);
                return (attr & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }

        private static long GetDirectorySizeSafe(string rootPath, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath) || IsReparsePointOrSymlink(rootPath))
                return 0;

            long total = 0;
            try
            {
                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Offline
                };
                foreach (string file in Directory.EnumerateFiles(rootPath, "*", opts))
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        var fi = new FileInfo(file);
                        if ((fi.Attributes & FileAttributes.ReparsePoint) == 0)
                            total += fi.Length;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("SIZE_SCAN", ex.Message);
            }
            return total;
        }

        /// <summary>
        /// Dynamically discovers Chrome, Edge, and Brave profile cache folders without touching cookies, history, or passwords.
        /// Skips browsers that are currently running when skipRunningBrowsers is true.
        /// </summary>
        private static List<(string BrowserName, string ProcessName, string CacheDir)> DiscoverBrowserCacheTargets()
        {
            var targets = new List<(string BrowserName, string ProcessName, string CacheDir)>();
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var browsers = new (string Name, string ProcName, string UserDataRelPath)[]
            {
                ("Google Chrome", "chrome", @"Google\Chrome\User Data"),
                ("Microsoft Edge", "msedge", @"Microsoft\Edge\User Data"),
                ("Brave Browser", "brave", @"BraveSoftware\Brave-Browser\User Data")
            };

            string[] safeCacheSubPaths =
            {
                @"Cache\Cache_Data",
                @"Code Cache\js",
                @"Code Cache\wasm",
                @"GPUCache"
            };

            foreach (var b in browsers)
            {
                string userDataRoot = Path.Combine(localApp, b.UserDataRelPath);
                if (!Directory.Exists(userDataRoot) || IsReparsePointOrSymlink(userDataRoot))
                    continue;

                try
                {
                    var profileDirs = new List<string>();
                    string defaultProf = Path.Combine(userDataRoot, "Default");
                    if (Directory.Exists(defaultProf)) profileDirs.Add(defaultProf);

                    foreach (string sub in Directory.EnumerateDirectories(userDataRoot, "Profile *"))
                    {
                        profileDirs.Add(sub);
                    }

                    foreach (string prof in profileDirs)
                    {
                        if (IsReparsePointOrSymlink(prof)) continue;
                        foreach (string cacheSub in safeCacheSubPaths)
                        {
                            string fullCacheDir = Path.Combine(prof, cacheSub);
                            if (Directory.Exists(fullCacheDir) && !IsReparsePointOrSymlink(fullCacheDir))
                            {
                                targets.Add((b.Name, b.ProcName, fullCacheDir));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogDiagnosticEvent("BROWSER_DISCOVER", ex.Message);
                }
            }

            return targets;
        }

        private static bool IsProcessRunningByName(string processName)
        {
            try
            {
                Process[] procs = Process.GetProcessesByName(processName);
                bool running = procs.Length > 0;
                foreach (var p in procs)
                {
                    try { p.Dispose(); } catch { }
                }
                return running;
            }
            catch
            {
                return false;
            }
        }

        private static long GetBrowserCachesSizeSafe(CancellationToken token, out int runningBrowserCount)
        {
            long sum = 0;
            var runningSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in DiscoverBrowserCacheTargets())
            {
                if (token.IsCancellationRequested) break;
                if (IsProcessRunningByName(item.ProcessName))
                {
                    runningSet.Add(item.BrowserName);
                }
                sum += GetDirectorySizeSafe(item.CacheDir, token);
            }
            runningBrowserCount = runningSet.Count;
            return sum;
        }

        private static bool IsProcessCriticalOrSystem(Process proc)
        {
            try
            {
                if (proc.Id <= 4) return true;
                if (ExcludedCriticalProcesses.Contains(proc.ProcessName)) return true;
                // Session 0 processes are Windows services and kernel helpers
                if (proc.SessionId == 0) return true;
            }
            catch
            {
                return true;
            }
            return false;
        }

        private RamTrimOperationReport ExecuteWorkingSetTrimCore()
        {
            var report = new RamTrimOperationReport { HasRun = true };
            var (_, _, _, usedBefore) = ReadPhysicalMemorySnapshot();
            report.UsedGbBefore = usedBefore;

            try
            {
                Process[] processes = Process.GetProcesses();
                foreach (Process proc in processes)
                {
                    try
                    {
                        report.EvaluatedProcesses++;
                        if (IsProcessCriticalOrSystem(proc))
                        {
                            report.ProtectedSkipped++;
                            continue;
                        }

                        if (!proc.HasExited && proc.WorkingSet64 > 25L * 1024L * 1024L && proc.Handle != IntPtr.Zero)
                        {
                            if (EmptyWorkingSet(proc.Handle) != 0)
                            {
                                report.SuccessfulApiCalls++;
                            }
                        }
                    }
                    catch
                    {
                        report.ProtectedSkipped++;
                    }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("RAM_TRIM_CORE", ex.Message);
            }

            var (_, _, _, usedAfter) = ReadPhysicalMemorySnapshot();
            report.UsedGbAfter = usedAfter;
            return report;
        }

        private void RenderRamLastReportText()
        {
            if (!_lastRamReport.HasRun)
            {
                TxtRamLastReport.Text = _isEnglish
                    ? "Last Operation: Working-set trim has not been executed yet."
                    : "Son İşlem: Henüz çalışma kümesi daraltma işlemi çalıştırılmadı.";
                return;
            }

            double deltaMb = _lastRamReport.PhysicalDeltaMb;
            string deltaStr = deltaMb >= 0
                ? $"-{deltaMb:F1} MB"
                : $"+{Math.Abs(deltaMb):F1} MB";

            TxtRamLastReport.Text = _isEnglish
                ? $"Last Result: Evaluated {_lastRamReport.EvaluatedProcesses} processes | Successful EmptyWorkingSet Calls: {_lastRamReport.SuccessfulApiCalls} | Protected/Skipped: {_lastRamReport.ProtectedSkipped} | Physical RAM Before: {_lastRamReport.UsedGbBefore:F2} GB -> After: {_lastRamReport.UsedGbAfter:F2} GB (Net Change: {deltaStr})"
                : $"Sonuç Raporu: İncelenen Süreç: {_lastRamReport.EvaluatedProcesses} | Başarılı EmptyWorkingSet Çağrısı: {_lastRamReport.SuccessfulApiCalls} | Korunan/Atlanan: {_lastRamReport.ProtectedSkipped} | Fiziksel RAM Önce: {_lastRamReport.UsedGbBefore:F2} GB -> Sonra: {_lastRamReport.UsedGbAfter:F2} GB (Net Değişim: {deltaStr})";
        }

        private void RefreshTopProcessesList()
        {
            Task.Run(() =>
            {
                if (_isWindowClosing) return;
                var records = new List<ProcessRecord>();
                try
                {
                    Process[] procs = Process.GetProcesses();
                    foreach (Process p in procs)
                    {
                        try
                        {
                            long ws = p.WorkingSet64;
                            bool prot = IsProcessCriticalOrSystem(p);
                            long startTicks = 0;
                            try { startTicks = p.StartTime.ToUniversalTime().Ticks; } catch { }

                            records.Add(new ProcessRecord
                            {
                                ProcessId = p.Id,
                                ProcessName = p.ProcessName,
                                WorkingSetBytes = ws,
                                IsProtected = prot,
                                StartTimeTicks = startTicks
                            });
                        }
                        catch { }
                        finally
                        {
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogDiagnosticEvent("PROC_REFRESH", ex.Message);
                }

                var top15 = records.OrderByDescending(x => x.WorkingSetBytes).Take(15).ToList();
                SafeDispatcherInvoke(() =>
                {
                    _currentTopProcesses.Clear();
                    _currentTopProcesses.AddRange(top15);
                    RenderTopProcessesListBox();
                });
            });
        }

        private void RenderTopProcessesListBox()
        {
            LstTopProcesses.Items.Clear();
            foreach (var rec in _currentTopProcesses)
            {
                string tag = rec.IsProtected
                    ? (_isEnglish ? "[Protected System/Service]" : "[Korunan Sistem/Hizmet]")
                    : (_isEnglish ? "[Eligible User App]" : "[Kullanıcı Uygulaması]");
                string text = _isEnglish
                    ? $"PID: {rec.ProcessId,-6} | {rec.ProcessName,-26} | Working Set: {rec.WorkingSetMb,8:F1} MB | {tag}"
                    : $"PID: {rec.ProcessId,-6} | {rec.ProcessName,-26} | Çalışma Kümesi: {rec.WorkingSetMb,8:F1} MB | {tag}";

                LstTopProcesses.Items.Add(new ListBoxItem
                {
                    Content = text,
                    Tag = rec
                });
            }
        }

        private void BtnRefreshProc_Click(object sender, RoutedEventArgs e)
        {
            PlayUiFeedbackSound(false);
            RefreshTopProcessesList();
        }

        private void BtnTrimSelectedProc_Click(object sender, RoutedEventArgs e)
        {
            if (LstTopProcesses.SelectedItem is not ListBoxItem item || item.Tag is not ProcessRecord rec)
            {
                TxtRamLastReport.Text = _isEnglish
                    ? "Please select a process from the list below first."
                    : "Lütfen önce aşağıdaki listeden bir süreç seçin.";
                return;
            }

            if (rec.IsProtected)
            {
                TxtRamLastReport.Text = _isEnglish
                    ? $"Action Blocked: '{rec.ProcessName}' (PID {rec.ProcessId}) is a protected system/service process."
                    : $"İşlem Engellendi: '{rec.ProcessName}' (PID {rec.ProcessId}) korunan bir sistem/hizmet sürecidir.";
                return;
            }

            if (Interlocked.CompareExchange(ref _ramBusyFlag, 1, 0) != 0)
            {
                TxtRamLastReport.Text = _isEnglish
                    ? "Another memory operation is currently in progress."
                    : "Şu anda başka bir bellek işlemi devam ediyor.";
                return;
            }

            try
            {
                using Process p = Process.GetProcessById(rec.ProcessId);
                // Verify process identity (ProcessName & StartTimeTicks) to prevent acting on a recycled PID (Section 19.4)
                if (!string.Equals(p.ProcessName, rec.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    TxtRamLastReport.Text = _isEnglish
                        ? $"Aborted: PID {rec.ProcessId} now belongs to '{p.ProcessName}' instead of '{rec.ProcessName}'."
                        : $"İptal Edildi: PID {rec.ProcessId} artık '{rec.ProcessName}' yerine '{p.ProcessName}' sürecine ait.";
                    RefreshTopProcessesList();
                    return;
                }

                if (rec.StartTimeTicks <= 0)
                {
                    TxtRamLastReport.Text = _isEnglish
                        ? $"Safety Abort: Start time was not available for '{rec.ProcessName}' (PID {rec.ProcessId}); trimming blocked to prevent PID reuse risk."
                        : $"Güvenlik İptali: '{rec.ProcessName}' (PID {rec.ProcessId}) başlangıç zamanı okunamadığı için PID yeniden kullanım riskine karşı daraltma engellendi.";
                    return;
                }

                try
                {
                    long currentStartTicks = p.StartTime.ToUniversalTime().Ticks;
                    if (Math.Abs(currentStartTicks - rec.StartTimeTicks) > TimeSpan.TicksPerSecond * 2)
                    {
                        TxtRamLastReport.Text = _isEnglish
                            ? $"Aborted: Process start time mismatch for PID {rec.ProcessId} (process was recycled). Refreshing list."
                            : $"İptal Edildi: PID {rec.ProcessId} başlangıç zamanı uyuşmuyor (süreç yeniden başlatılmış). Liste yenileniyor.";
                        RefreshTopProcessesList();
                        return;
                    }
                }
                catch (Exception pex)
                {
                    TxtRamLastReport.Text = _isEnglish
                        ? $"Safety Abort: Could not verify process start time for PID {rec.ProcessId} ({pex.Message})."
                        : $"Güvenlik İptali: PID {rec.ProcessId} süreç başlangıç zamanı doğrulanamadı ({pex.Message}).";
                    RefreshTopProcessesList();
                    return;
                }

                double wsBeforeMb = p.WorkingSet64 / (1024.0 * 1024.0);
                int apiRes = EmptyWorkingSet(p.Handle);
                if (apiRes == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    TxtRamLastReport.Text = _isEnglish
                        ? $"EmptyWorkingSet failed for '{rec.ProcessName}' (PID {rec.ProcessId}). Win32 Error Code: {err}."
                        : $"'{rec.ProcessName}' (PID {rec.ProcessId}) için EmptyWorkingSet başarısız oldu. Win32 Hata Kodu: {err}.";
                    return;
                }

                p.Refresh();
                double wsAfterMb = p.WorkingSet64 / (1024.0 * 1024.0);
                PlayUiFeedbackSound(true);
                RefreshTopProcessesList();
                UpdateRealHardwareMetrics();

                TxtRamLastReport.Text = _isEnglish
                    ? $"Trimmed '{rec.ProcessName}' (PID {rec.ProcessId}): Process Working Set {wsBeforeMb:F1} MB -> {wsAfterMb:F1} MB."
                    : $"'{rec.ProcessName}' (PID {rec.ProcessId}) daraltıldı: Süreç Çalışma Kümesi {wsBeforeMb:F1} MB -> {wsAfterMb:F1} MB.";
            }
            catch (ArgumentException)
            {
                TxtRamLastReport.Text = _isEnglish
                    ? $"Process '{rec.ProcessName}' (PID {rec.ProcessId}) has already exited."
                    : $"'{rec.ProcessName}' (PID {rec.ProcessId}) süreci zaten sonlanmış.";
                RefreshTopProcessesList();
            }
            catch (Win32Exception wex)
            {
                TxtRamLastReport.Text = _isEnglish
                    ? $"Access denied while trimming '{rec.ProcessName}' (PID {rec.ProcessId}): {wex.Message}"
                    : $"'{rec.ProcessName}' (PID {rec.ProcessId}) için erişim reddedildi: {wex.Message}";
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("SINGLE_TRIM", ex.Message);
                TxtRamLastReport.Text = _isEnglish
                    ? $"Error trimming process: {ex.Message}"
                    : $"Süreç daraltma hatası: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _ramBusyFlag, 0);
            }
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            PlayUiFeedbackSound(false);

            TabDash.Visibility = tag == "Dash" ? Visibility.Visible : Visibility.Collapsed;
            TabRam.Visibility = tag == "Ram" ? Visibility.Visible : Visibility.Collapsed;
            TabDisk.Visibility = tag == "Disk" ? Visibility.Visible : Visibility.Collapsed;
            TabNet.Visibility = tag == "Net" ? Visibility.Visible : Visibility.Collapsed;
            TabPriv.Visibility = tag == "Priv" ? Visibility.Visible : Visibility.Collapsed;
            TabStart.Visibility = tag == "Start" ? Visibility.Visible : Visibility.Collapsed;

            ResetNavButtons();
            btn.Background = (Brush)FindResource("CardDarkHover");
            btn.BorderBrush = (Brush)FindResource("AccentCyan");
            btn.BorderThickness = new Thickness(1);
            if (btn.Content is TextBlock tb)
            {
                tb.Foreground = (Brush)FindResource("TextWhite");
                tb.FontWeight = FontWeights.SemiBold;
            }
        }

        private void ResetNavButtons()
        {
            Button[] navs = { NavDash, NavRam, NavDisk, NavNet, NavPriv, NavStart };
            foreach (Button b in navs)
            {
                b.Background = Brushes.Transparent;
                b.BorderThickness = new Thickness(0);
                if (b.Content is TextBlock tb)
                {
                    tb.Foreground = (Brush)FindResource("TextMuted");
                    tb.FontWeight = FontWeights.Normal;
                }
            }
        }

        private async void BtnMasterBoost_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _ramBusyFlag, 1, 0) != 0)
            {
                TxtBoostStatus.Text = _isEnglish
                    ? "A memory or maintenance operation is already running."
                    : "Şu anda başka bir bellek veya bakım işlemi çalışıyor.";
                return;
            }

            if (Interlocked.CompareExchange(ref _diskBusyFlag, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _ramBusyFlag, 0);
                TxtBoostStatus.Text = _isEnglish
                    ? "A storage scan or cleanup operation is currently running."
                    : "Şu anda bir disk tarama veya temizlik işlemi çalışıyor.";
                return;
            }

            BtnMasterBoost.IsEnabled = false;
            PbBoost.Visibility = Visibility.Visible;
            PbBoost.IsIndeterminate = true;
            PlayUiFeedbackSound(false);

            TxtBoostStatus.Text = _isEnglish
                ? "Trimming eligible user process working sets, cleaning unlocked %TEMP% files, and flushing DNS..."
                : "Kullanıcı süreçlerinin çalışma kümeleri daraltılıyor, kilitsiz %TEMP% dosyaları siliniyor ve DNS sıfırlanıyor...";

            var cleanReport = new CleanupOperationReport();
            bool dnsFlushed = false;

            try
            {
                await Task.Run(async () =>
                {
                    _lastRamReport = ExecuteWorkingSetTrimCore();
                    cleanReport = CleanDirectoryDetailedSafe(Path.GetTempPath(), CancellationToken.None);
                    dnsFlushed = await FlushDnsResolverVerifiedAsync();
                });

                _lastActuallyFreedBytes += cleanReport.FreedBytes;
                _lastActuallyFreedFiles += cleanReport.DeletedFiles;

                UpdateRealHardwareMetrics();
                RenderRamLastReportText();
                CalculateInitialTempSizeAsync();
                RefreshTopProcessesList();

                double freedMb = cleanReport.FreedBytes / (1024.0 * 1024.0);
                double ramDeltaMb = _lastRamReport.PhysicalDeltaMb;
                TxtBoostStatus.Text = _isEnglish
                    ? $"Maintenance Summary: {_lastRamReport.SuccessfulApiCalls} user apps trimmed (Physical RAM Change: {ramDeltaMb:F1} MB), {cleanReport.DeletedFiles} temp files deleted ({freedMb:F2} MB freed, {cleanReport.LockedSkippedFiles} locked skipped), DNS Flush: {(dnsFlushed ? "Verified OK" : "Failed")}."
                    : $"Bakım Özeti: {_lastRamReport.SuccessfulApiCalls} kullanıcı süreci daraltıldı (Fiziksel RAM Değişimi: {ramDeltaMb:F1} MB), {cleanReport.DeletedFiles} geçici dosya silindi ({freedMb:F2} MB kazanıldı, {cleanReport.LockedSkippedFiles} kilitli dosya atlandı), DNS Sıfırlama: {(dnsFlushed ? "Doğrulandı" : "Başarısız")}.";
                PlayUiFeedbackSound(true);
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("MASTER_BOOST", ex.Message);
                TxtBoostStatus.Text = _isEnglish
                    ? $"Maintenance error: {ex.Message}"
                    : $"Bakım hatası: {ex.Message}";
            }
            finally
            {
                PbBoost.Visibility = Visibility.Collapsed;
                BtnMasterBoost.IsEnabled = true;
                Interlocked.Exchange(ref _diskBusyFlag, 0);
                Interlocked.Exchange(ref _ramBusyFlag, 0);
            }
        }

        private async void BtnPurgeRam_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _ramBusyFlag, 1, 0) != 0)
            {
                TxtRamLastReport.Text = _isEnglish
                    ? "A memory operation is already running."
                    : "Şu anda başka bir bellek işlemi çalışıyor.";
                return;
            }

            BtnPurgeRam.IsEnabled = false;
            try
            {
                _lastRamReport = await Task.Run(() => ExecuteWorkingSetTrimCore());
                UpdateRealHardwareMetrics();
                RenderRamLastReportText();
                RefreshTopProcessesList();
                PlayUiFeedbackSound(true);
            }
            finally
            {
                BtnPurgeRam.IsEnabled = true;
                Interlocked.Exchange(ref _ramBusyFlag, 0);
            }
        }

        private void BtnCancelDiskOp_Click(object sender, RoutedEventArgs e)
        {
            try { _diskCts?.Cancel(); } catch { }
        }

        private async void BtnScanDisk_Click(object sender, RoutedEventArgs e)
        {
            string[] defaultDupFolders =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            await ExecuteDiskAndDuplicateScanAsync(defaultDupFolders);
        }

        private async void BtnSelectDupFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = _isEnglish
                    ? "Select Folder for Recursive SHA-256 Duplicate Scan"
                    : "Alt Klasörleriyle Birlikte SHA-256 Kopya Taraması Yapılacak Klasörü Seçin"
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                await ExecuteDiskAndDuplicateScanAsync(new[] { dlg.FolderName });
            }
        }

        private async Task ExecuteDiskAndDuplicateScanAsync(string[] duplicateRootFolders)
        {
            if (Interlocked.CompareExchange(ref _diskBusyFlag, 1, 0) != 0)
            {
                TxtDiskResult.Text = _isEnglish
                    ? "Another disk scan or cleanup operation is currently in progress."
                    : "Şu anda başka bir disk tarama veya temizlik işlemi devam ediyor.";
                return;
            }

            bool scanUserTemp = ChkUserTemp.IsChecked == true;
            bool scanWinTemp = ChkWinTemp.IsChecked == true;
            bool scanDuplicates = ChkUpdateCache.IsChecked == true;
            bool scanD3dCache = ChkThumbnails.IsChecked == true;
            bool scanBrowsers = ChkBrowserCache.IsChecked == true;

            _diskCts?.Cancel();
            _diskCts?.Dispose();
            _diskCts = new CancellationTokenSource();
            CancellationToken token = _diskCts.Token;

            BtnScanDisk.IsEnabled = false;
            BtnSelectDupFolder.IsEnabled = false;
            BtnCleanDisk.IsEnabled = false;
            BtnCancelDiskOp.IsEnabled = true;

            TxtDiskResult.Text = _isEnglish
                ? "Scanning selected cache targets and computing stream-based SHA-256 digests..."
                : "Seçili önbellek hedefleri ve akış tabanlı SHA-256 kopya dosya özetleri hesaplanıyor...";

            long totalCacheBytes = 0;
            int runningBrowsers = 0;
            var dupRecords = new List<DuplicateFileRecord>();

            try
            {
                await Task.Run(() =>
                {
                    if (scanUserTemp && !token.IsCancellationRequested)
                        totalCacheBytes += GetDirectorySizeSafe(Path.GetTempPath(), token);

                    if (scanWinTemp && !token.IsCancellationRequested)
                        totalCacheBytes += GetDirectorySizeSafe(GetWindowsTempDirectoryPath(), token);

                    if (scanD3dCache && !token.IsCancellationRequested)
                    {
                        string d3d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
                        totalCacheBytes += GetDirectorySizeSafe(d3d, token);
                    }

                    if (scanBrowsers && !token.IsCancellationRequested)
                        totalCacheBytes += GetBrowserCachesSizeSafe(token, out runningBrowsers);

                    if (scanDuplicates && !token.IsCancellationRequested)
                    {
                        dupRecords = ScanExactDuplicatesRecursiveSafe(duplicateRootFolders, token);
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("DISK_SCAN", ex.Message);
            }
            finally
            {
                BtnScanDisk.IsEnabled = true;
                BtnSelectDupFolder.IsEnabled = true;
                BtnCleanDisk.IsEnabled = true;
                BtnCancelDiskOp.IsEnabled = false;
                Interlocked.Exchange(ref _diskBusyFlag, 0);
            }

            _foundDuplicates.Clear();
            _foundDuplicates.AddRange(dupRecords);
            _lastMeasuredCacheBytes = totalCacheBytes;
            _lastDuplicatePotentialBytes = _foundDuplicates.Sum(x => x.SizeBytes);

            RenderDuplicateListBox();
            UpdateDiskMetricsCards();

            double cacheMb = totalCacheBytes / (1024.0 * 1024.0);
            double dupMb = _lastDuplicatePotentialBytes / (1024.0 * 1024.0);

            if (token.IsCancellationRequested)
            {
                TxtDiskResult.Text = _isEnglish
                    ? $"Scan Cancelled (Partial Results): {cacheMb:F1} MB cache measured, {_foundDuplicates.Count} duplicates ({dupMb:F1} MB potential) found before stop."
                    : $"Tarama Durduruldu (Kısmi Sonuçlar): Durdurulana kadar {cacheMb:F1} MB önbellek ve {_foundDuplicates.Count} kopya ({dupMb:F1} MB potansiyel) tespit edildi.";
            }
            else
            {
                string browserNote = runningBrowsers > 0
                    ? (_isEnglish ? $" ({runningBrowsers} open browser(s) detected; close browsers before cleaning their caches)." : $" ({runningBrowsers} açık tarayıcı algılandı; tarayıcı önbelleklerini temizlemeden önce tarayıcıyı kapatın).")
                    : "";
                TxtDiskResult.Text = _isEnglish
                    ? $"Preview Complete: {cacheMb:F1} MB current cache size | {_foundDuplicates.Count} exact SHA-256 duplicates ({dupMb:F1} MB potential savings){browserNote}"
                    : $"Önizleme Tamamlandı: {cacheMb:F1} MB mevcut önbellek | {_foundDuplicates.Count} adet birebir SHA-256 kopya ({dupMb:F1} MB potansiyel kazanç){browserNote}";
            }

            PlayUiFeedbackSound(true);
        }

        private void RenderDuplicateListBox()
        {
            LstDuplicates.Items.Clear();
            foreach (var rec in _foundDuplicates)
            {
                double fileMb = rec.SizeBytes / (1024.0 * 1024.0);
                string row = _isEnglish
                    ? $"[DUPLICATE {fileMb:F2} MB] {rec.DuplicatePath}   <== Original: {rec.OriginalPath} (SHA256: {rec.Sha256Hex.Substring(0, 12)}...)"
                    : $"[KOPYA {fileMb:F2} MB] {rec.DuplicatePath}   <== Orijinal: {rec.OriginalPath} (SHA256: {rec.Sha256Hex.Substring(0, 12)}...)";
                LstDuplicates.Items.Add(new ListBoxItem
                {
                    Content = row,
                    Tag = rec
                });
            }

            if (LstDuplicates.Items.Count == 0)
            {
                LstDuplicates.Items.Add(_isEnglish
                    ? "No exact duplicate files found in scanned directories."
                    : "Taranan klasörlerde ve alt klasörlerinde kopya dosya bulunamadı.");
            }
        }

        private async void BtnCleanDisk_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _diskBusyFlag, 1, 0) != 0)
            {
                TxtDiskResult.Text = _isEnglish
                    ? "Another disk operation is currently running."
                    : "Şu anda başka bir disk işlemi devam ediyor.";
                return;
            }

            bool cleanUserTemp = ChkUserTemp.IsChecked == true;
            bool cleanWinTemp = ChkWinTemp.IsChecked == true;
            bool cleanD3dCache = ChkThumbnails.IsChecked == true;
            bool cleanBrowsers = ChkBrowserCache.IsChecked == true;

            if (cleanWinTemp)
            {
                var confirm = MessageBox.Show(this,
                    _isEnglish
                        ? $"You selected Windows System Temp ({GetWindowsTempDirectoryPath()}).\nDo you confirm cleaning unlocked temporary files in this system directory?"
                        : $"Windows Sistem Temp ({GetWindowsTempDirectoryPath()}) dizinini seçtiniz.\nBu sistem dizinindeki kilitsiz geçici dosyaların temizlenmesini onaylıyor musunuz?",
                    "QOVX",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    Interlocked.Exchange(ref _diskBusyFlag, 0);
                    return;
                }
            }

            _diskCts?.Cancel();
            _diskCts?.Dispose();
            _diskCts = new CancellationTokenSource();
            CancellationToken token = _diskCts.Token;

            BtnScanDisk.IsEnabled = false;
            BtnSelectDupFolder.IsEnabled = false;
            BtnCleanDisk.IsEnabled = false;
            BtnCancelDiskOp.IsEnabled = true;

            var totalReport = new CleanupOperationReport();

            try
            {
                await Task.Run(() =>
                {
                    var dirsToClean = new List<string>();
                    if (cleanUserTemp) dirsToClean.Add(Path.GetTempPath());
                    if (cleanWinTemp) dirsToClean.Add(GetWindowsTempDirectoryPath());
                    if (cleanD3dCache)
                        dirsToClean.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"));

                    if (cleanBrowsers)
                    {
                        var skippedBrowsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var bTarget in DiscoverBrowserCacheTargets())
                        {
                            if (IsProcessRunningByName(bTarget.ProcessName))
                            {
                                skippedBrowsers.Add(bTarget.BrowserName);
                                continue;
                            }
                            dirsToClean.Add(bTarget.CacheDir);
                        }
                        totalReport.SkippedRunningBrowsers = skippedBrowsers.Count;
                    }

                    foreach (string dir in dirsToClean)
                    {
                        if (token.IsCancellationRequested)
                        {
                            totalReport.WasCancelled = true;
                            break;
                        }
                        var rep = CleanDirectoryDetailedSafe(dir, token);
                        totalReport.DeletedFiles += rep.DeletedFiles;
                        totalReport.LockedSkippedFiles += rep.LockedSkippedFiles;
                        totalReport.PermissionDeniedFiles += rep.PermissionDeniedFiles;
                        totalReport.RemovedEmptyDirs += rep.RemovedEmptyDirs;
                        totalReport.FreedBytes += rep.FreedBytes;
                        if (rep.WasCancelled)
                        {
                            totalReport.WasCancelled = true;
                            break;
                        }
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                totalReport.WasCancelled = true;
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("DISK_CLEAN", ex.Message);
            }
            finally
            {
                BtnScanDisk.IsEnabled = true;
                BtnSelectDupFolder.IsEnabled = true;
                BtnCleanDisk.IsEnabled = true;
                BtnCancelDiskOp.IsEnabled = false;
                Interlocked.Exchange(ref _diskBusyFlag, 0);
            }

            _lastActuallyFreedBytes = totalReport.FreedBytes;
            _lastActuallyFreedFiles = totalReport.DeletedFiles;
            CalculateInitialTempSizeAsync();
            UpdateDiskMetricsCards();

            double freedMb = totalReport.FreedBytes / (1024.0 * 1024.0);
            string cancelPrefix = totalReport.WasCancelled
                ? (_isEnglish ? "[Cancelled - Partial] " : "[Durduruldu - Kısmi] ")
                : "";
            string browserNote = totalReport.SkippedRunningBrowsers > 0
                ? (_isEnglish ? $" | {totalReport.SkippedRunningBrowsers} open browser(s) skipped for safety" : $" | {totalReport.SkippedRunningBrowsers} açık tarayıcı güvenlik için atlandı")
                : "";

            TxtDiskResult.Text = _isEnglish
                ? $"{cancelPrefix}Deleted: {totalReport.DeletedFiles} files ({freedMb:F2} MB actually freed) | Empty folders removed: {totalReport.RemovedEmptyDirs} | Locked/In-use skipped: {totalReport.LockedSkippedFiles} | Admin permission required: {totalReport.PermissionDeniedFiles}{browserNote}."
                : $"{cancelPrefix}Silinen: {totalReport.DeletedFiles} dosya ({freedMb:F2} MB gerçek alan açıldı) | Kaldırılan boş klasör: {totalReport.RemovedEmptyDirs} | Kilitli/Kullanımda atlanan: {totalReport.LockedSkippedFiles} | Yönetici izni gereken: {totalReport.PermissionDeniedFiles}{browserNote}.";

            PlayUiFeedbackSound(true);
        }

        private static bool SendFileToRecycleBin(string filePath)
        {
            try
            {
                var shf = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = filePath + '\0' + '\0',
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                };
                int ret = SHFileOperation(ref shf);
                return ret == 0 && !shf.fAnyOperationsAborted && !File.Exists(filePath);
            }
            catch
            {
                return false;
            }
        }

        private void BtnDeleteSelectedDup_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _diskBusyFlag, 1, 0) != 0)
            {
                TxtDiskResult.Text = _isEnglish
                    ? "Another disk scan or cleanup operation is currently in progress."
                    : "Şu anda başka bir disk tarama veya temizlik işlemi devam ediyor.";
                return;
            }
            try {
            if (LstDuplicates.SelectedItem is not ListBoxItem item || item.Tag is not DuplicateFileRecord targetRecord)
            {
                TxtDiskResult.Text = _isEnglish
                    ? "Select a duplicate file entry from the list below first."
                    : "Lütfen önce aşağıdaki listeden silmek istediğiniz kopya dosyayı seçin.";
                return;
            }

            if (string.Equals(Path.GetFullPath(targetRecord.DuplicatePath), Path.GetFullPath(targetRecord.OriginalPath), StringComparison.OrdinalIgnoreCase))
            {
                TxtDiskResult.Text = _isEnglish
                    ? "Safety Refusal: Duplicate path and original path refer to the same location."
                    : "Güvenlik Reddi: Kopya ve orijinal dosya yolu aynı konumu gösteriyor.";
                return;
            }

            if (!File.Exists(targetRecord.OriginalPath))
            {
                TxtDiskResult.Text = _isEnglish
                    ? "Safety Refusal: The original counterpart file no longer exists on disk; deletion blocked to prevent data loss."
                    : "Güvenlik Reddi: Orijinal dosya artık diskte bulunamadığı için veri kaybını önlemek adına silme işlemi engellendi.";
                return;
            }

            if (!File.Exists(targetRecord.DuplicatePath))
            {
                TxtDiskResult.Text = _isEnglish
                    ? "Selected duplicate file no longer exists on disk."
                    : "Seçili kopya dosya artık diskte mevcut değil.";
                _foundDuplicates.Remove(targetRecord);
                RenderDuplicateListBox();
                return;
            }

            // Re-verify BOTH duplicate and original with FileShare.Read lock, symlink chain check, and NTFS inode check (Sections 3 & 5)
            try
            {
                if (IsReparsePointOrSymlink(targetRecord.DuplicatePath) || IsReparsePointOrSymlink(targetRecord.OriginalPath))
                {
                    TxtDiskResult.Text = _isEnglish
                        ? "Security Refusal: Symbolic link or junction detected on target path."
                        : "Güvenlik Reddi: Hedef dosya yolunda sembolik bağlantı (symlink/junction) tespit edildi.";
                    return;
                }

                using var sha = SHA256.Create();
                string dupHashNow;
                string origHashNow;

                using (var sDup = new FileStream(targetRecord.DuplicatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var sOrig = new FileStream(targetRecord.OriginalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                {
                    if (sDup.Length != targetRecord.SizeBytes || sOrig.Length != targetRecord.SizeBytes)
                    {
                        TxtDiskResult.Text = _isEnglish
                            ? "Safety Refusal: File size changed on disk since initial scan. Please re-scan before deleting."
                            : "Güvenlik Reddi: Dosya boyutu ilk taramadan sonra değişmiş. Veri kaybını önlemek için lütfen yeniden tarayın.";
                        return;
                    }

                    if (TryGetNtfsFileIdentity(sDup, out ulong dupInode, out _) &&
                        TryGetNtfsFileIdentity(sOrig, out ulong origInode, out _) &&
                        dupInode != 0 && dupInode == origInode)
                    {
                        TxtDiskResult.Text = _isEnglish
                            ? "Safety Refusal: Both paths point to the same physical NTFS file index (hardlink)."
                            : "Güvenlik Reddi: İki dosya yolu aynı fiziksel NTFS kaydına (hardlink) işaret ediyor.";
                        return;
                    }

                    dupHashNow = Convert.ToHexString(sha.ComputeHash(sDup));
                    origHashNow = Convert.ToHexString(sha.ComputeHash(sOrig));
                }

                if (!string.Equals(dupHashNow, targetRecord.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    TxtDiskResult.Text = _isEnglish
                        ? "Safety Refusal: The duplicate file's SHA-256 digest changed since initial scan. Deletion blocked."
                        : "Güvenlik Reddi: Kopya dosyanın SHA-256 özeti taramadan sonra değişmiş. Silme engellendi.";
                    return;
                }

                if (!string.Equals(origHashNow, targetRecord.Sha256Hex, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(dupHashNow, origHashNow, StringComparison.OrdinalIgnoreCase))
                {
                    TxtDiskResult.Text = _isEnglish
                        ? "Safety Refusal: The original preserved file was modified after scanning! Deletion blocked to prevent losing the only intact version."
                        : "Kritik Güvenlik Reddi: Orijinal dosya taramadan sonra değiştirilmiş! Son sağlam kopyanın kaybolmasını önlemek için silme durduruldu.";
                    return;
                }
            }
            catch (Exception ex)
            {
                TxtDiskResult.Text = _isEnglish
                    ? $"Could not re-verify files before deletion: {ex.Message}"
                    : $"Silme öncesi dosyalar doğrulanamadı: {ex.Message}";
                return;
            }

            double mb = targetRecord.SizeBytes / (1024.0 * 1024.0);
            var confirm = MessageBox.Show(this,
                _isEnglish
                    ? $"Move verified duplicate file to Windows Recycle Bin?\n\nDuplicate to Remove ({mb:F2} MB):\n{targetRecord.DuplicatePath}\n\nPreserved Original:\n{targetRecord.OriginalPath}\n\nSHA-256: {targetRecord.Sha256Hex}"
                    : $"Doğrulanan kopya dosya Windows Geri Dönüşüm Kutusu'na gönderilsin mi?\n\nKaldırılacak Kopya ({mb:F2} MB):\n{targetRecord.DuplicatePath}\n\nKorunan Orijinal Dosya:\n{targetRecord.OriginalPath}\n\nSHA-256: {targetRecord.Sha256Hex}",
                "QOVX - SHA-256 Duplicate Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            if (SendFileToRecycleBin(targetRecord.DuplicatePath))
            {
                // Note: Moved to Recycle Bin; disk space will be reclaimed once Recycle Bin is emptied (ChatGPT 2B fix)
                _foundDuplicates.Remove(targetRecord);
                _lastDuplicatePotentialBytes = _foundDuplicates.Sum(x => x.SizeBytes);
                RenderDuplicateListBox();
                UpdateDiskMetricsCards();
                PlayUiFeedbackSound(true);

                TxtDiskResult.Text = _isEnglish
                    ? $"Moved to Windows Recycle Bin: {targetRecord.DuplicatePath} ({mb:F2} MB queued for release upon emptying bin)"
                    : $"Windows Geri Dönüşüm Kutusu'na taşındı: {targetRecord.DuplicatePath} ({mb:F2} MB — Geri Dönüşüm Kutusu boşaltıldığında disk alanı açılacaktır)";
            }
            else
            {
                TxtDiskResult.Text = _isEnglish
                    ? $"Failed to move file to Recycle Bin (file may be locked): {targetRecord.DuplicatePath}"
                    : $"Dosya Geri Dönüşüm Kutusu'na taşınamadı (kullanımda olabilir): {targetRecord.DuplicatePath}";
            }
            }
            finally
            {
                Interlocked.Exchange(ref _diskBusyFlag, 0);
            }
        }

        private static bool TryGetNtfsFileIdentity(FileStream fs, out ulong volumeAndFileIndex, out uint linkCount)
        {
            volumeAndFileIndex = 0;
            linkCount = 1;
            try
            {
                if (GetFileInformationByHandle(fs.SafeFileHandle, out BY_HANDLE_FILE_INFORMATION info))
                {
                    linkCount = info.NumberOfLinks;
                    ulong fileIdx = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                    volumeAndFileIndex = ((ulong)info.VolumeSerialNumber << 48) ^ fileIdx;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string? ComputeStreamSha256Cancellable(string filePath, long expectedSize, out bool isHardlinkToSeenInode, HashSet<ulong> seenInodes, CancellationToken token)
        {
            isHardlinkToSeenInode = false;
            var fiBefore = new FileInfo(filePath);
            long writeTicksBefore = fiBefore.LastWriteTimeUtc.Ticks;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
            if (TryGetNtfsFileIdentity(stream, out ulong inodeKey, out uint linkCount) && inodeKey != 0)
            {
                if (!seenInodes.Add(inodeKey))
                {
                    // Points to the exact same physical MFT record (hardlink) - deleting one link does not free disk space!
                    isHardlinkToSeenInode = true;
                    return null;
                }
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, bytesRead);
                totalRead += bytesRead;
            }

            var fiAfter = new FileInfo(filePath);
            if (totalRead != expectedSize || fiAfter.Length != expectedSize || fiAfter.LastWriteTimeUtc.Ticks != writeTicksBefore)
            {
                // File was modified while being hashed; skip to prevent false duplicate match (Section 4)
                return null;
            }

            return Convert.ToHexString(sha.GetHashAndReset());
        }

        private static List<DuplicateFileRecord> ScanExactDuplicatesRecursiveSafe(IEnumerable<string> rootFolders, CancellationToken token)
        {
            var duplicates = new List<DuplicateFileRecord>();
            var filesBySize = new Dictionary<long, List<string>>();
            var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Offline | FileAttributes.System
            };

            foreach (string folder in rootFolders)
            {
                if (token.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) || IsReparsePointOrSymlink(folder))
                    continue;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder, "*", opts))
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            string normalizedPath = Path.GetFullPath(file);
                            if (!visitedPaths.Add(normalizedPath))
                                continue;

                            var fi = new FileInfo(normalizedPath);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                                continue;

                            // Evaluate files >= 0 bytes (0-byte files grouped together as well)
                            long len = fi.Length;
                            if (!filesBySize.TryGetValue(len, out var list))
                            {
                                list = new List<string>();
                                filesBySize[len] = list;
                            }
                            list.Add(normalizedPath);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    App.LogDiagnosticEvent("DUP_ENUM", ex.Message);
                }
            }

            var seenInodes = new HashSet<ulong>();

            foreach (var kvp in filesBySize.Where(k => k.Value.Count > 1))
            {
                if (token.IsCancellationRequested) break;
                var firstSeenByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (string filePath in kvp.Value)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        string? hash = ComputeStreamSha256Cancellable(filePath, kvp.Key, out bool isHardlink, seenInodes, token);
                        if (isHardlink || string.IsNullOrEmpty(hash))
                            continue;

                        if (firstSeenByHash.TryGetValue(hash, out string? originalPath))
                        {
                            duplicates.Add(new DuplicateFileRecord
                            {
                                DuplicatePath = filePath,
                                OriginalPath = originalPath,
                                SizeBytes = kvp.Key,
                                Sha256Hex = hash,
                                LastWriteUtcTicks = new FileInfo(filePath).LastWriteTimeUtc.Ticks
                            });
                        }
                        else
                        {
                            firstSeenByHash[hash] = filePath;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return duplicates;
                    }
                    catch (Exception ex)
                    {
                        App.LogDiagnosticEvent("DUP_HASH", ex.Message);
                    }
                }
            }

            return duplicates;
        }

        private static CleanupOperationReport CleanDirectoryDetailedSafe(string rootPath, CancellationToken token)
        {
            var report = new CleanupOperationReport();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath) || IsReparsePointOrSymlink(rootPath))
                return report;

            try
            {
                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.System
                };

                foreach (string file in Directory.EnumerateFiles(rootPath, "*", opts))
                {
                    if (token.IsCancellationRequested)
                    {
                        report.WasCancelled = true;
                        break;
                    }
                    try
                    {
                        var fi = new FileInfo(file);
                        if ((fi.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                        {
                            report.LockedSkippedFiles++;
                            continue;
                        }
                        long len = fi.Length;
                        File.Delete(file);
                        report.DeletedFiles++;
                        report.FreedBytes += len;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        report.PermissionDeniedFiles++;
                    }
                    catch (IOException)
                    {
                        report.LockedSkippedFiles++;
                    }
                    catch
                    {
                        report.LockedSkippedFiles++;
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    var subDirs = Directory.EnumerateDirectories(rootPath, "*", opts)
                        .OrderByDescending(d => d.Count(c => c == '\\' || c == '/'))
                        .ThenByDescending(d => d.Length)
                        .ToList();
                    foreach (string subDir in subDirs)
                    {
                        if (token.IsCancellationRequested)
                        {
                            report.WasCancelled = true;
                            break;
                        }
                        try
                        {
                            if (!IsReparsePointOrSymlink(subDir) && !Directory.EnumerateFileSystemEntries(subDir).Any())
                            {
                                Directory.Delete(subDir, false);
                                report.RemovedEmptyDirs++;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("CLEAN_DIR", ex.Message);
            }
            return report;
        }

        private void RefreshNetworkAdaptersList()
        {
            try
            {
                LstNetAdapters.Items.Clear();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var ipProps = ni.GetIPProperties();
                    var ipv4 = ipProps.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A";
                    var gw = ipProps.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A";
                    var dnsList = string.Join(", ", ipProps.DnsAddresses
                        .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                        .Select(d => d.ToString()));
                    if (string.IsNullOrEmpty(dnsList)) dnsList = "Auto";
                    double speedMbps = ni.Speed > 0 ? ni.Speed / 1_000_000.0 : 0;

                    string row = _isEnglish
                        ? $"{ni.Name,-20} | IPv4: {ipv4,-14} | Gateway: {gw,-14} | DNS: {dnsList,-18} | {speedMbps:F0} Mbps"
                        : $"{ni.Name,-20} | IPv4: {ipv4,-14} | Geçit: {gw,-14} | DNS: {dnsList,-18} | {speedMbps:F0} Mbps";
                    LstNetAdapters.Items.Add(row);
                }

                if (LstNetAdapters.Items.Count == 0)
                {
                    LstNetAdapters.Items.Add(_isEnglish
                        ? "No active external network adapter detected."
                        : "Aktif harici ağ adaptörü algılanmadı.");
                }
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("NET_ADAPTERS", ex.Message);
            }
        }

        /// <summary>
        /// Builds a standard RFC 1035 DNS query for 'example.com' (Type A, Class IN) with a cryptographically random 16-bit Transaction ID.
        /// </summary>
        private static byte[] BuildRfc1035QueryPacket(ushort txId)
        {
            return new byte[]
            {
                (byte)(txId >> 8), (byte)(txId & 0xFF),
                0x01, 0x00, // Flags: Standard query (Opcode=0), RD=1
                0x00, 0x01, // QDCOUNT = 1
                0x00, 0x00, // ANCOUNT = 0
                0x00, 0x00, // NSCOUNT = 0
                0x00, 0x00, // ARCOUNT = 0
                0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                0x03, (byte)'c', (byte)'o', (byte)'m',
                0x00,
                0x00, 0x01, // QTYPE = A (1)
                0x00, 0x01  // QCLASS = IN (1)
            };
        }

        private static bool ValidateDnsQuestionSection(byte[] query, byte[] response)
        {
            // Header is 12 bytes, Question section in our query is bytes 12..28 (17 bytes total, packet length 29)
            if (response == null || response.Length < query.Length)
                return false;
            for (int i = 12; i < query.Length; i++)
            {
                if (response[i] != query[i])
                    return false;
            }
            return true;
        }

        private static (DnsSampleStatus Status, long ElapsedMs, string Detail, bool UsedTcp) ExecuteSingleDnsQuerySample(string serverIp, int timeoutMs = 1500)
        {
            ushort txId = (ushort)RandomNumberGenerator.GetInt32(1, 65535);
            byte[] queryPacket = BuildRfc1035QueryPacket(txId);
            IPAddress targetAddr = IPAddress.Parse(serverIp);
            var targetEp = new IPEndPoint(targetAddr, 53);

            try
            {
                using var udp = new UdpClient();
                udp.Client.SendTimeout = timeoutMs;
                udp.Client.ReceiveTimeout = timeoutMs;

                var sw = Stopwatch.StartNew();
                udp.Send(queryPacket, queryPacket.Length, targetEp);
                IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                byte[] response = udp.Receive(ref remoteEp);
                sw.Stop();

                // Verify source IP and Port 53 (Section 6)
                if (!remoteEp.Address.Equals(targetAddr) || remoteEp.Port != 53)
                    return (DnsSampleStatus.InvalidResponse, -1, "SourceMismatch", false);

                if (response == null || response.Length < queryPacket.Length)
                    return (DnsSampleStatus.InvalidResponse, -1, "ShortPacket", false);

                ushort respTxId = (ushort)((response[0] << 8) | response[1]);
                if (respTxId != txId)
                    return (DnsSampleStatus.InvalidResponse, -1, "TxIdMismatch", false);

                bool qrBit = (response[2] & 0x80) != 0;
                int opcode = (response[2] >> 3) & 0x0F;
                bool tcBit = (response[2] & 0x02) != 0;
                int qdCount = (response[4] << 8) | response[5];
                int rcode = response[3] & 0x0F;

                if (!qrBit || opcode != 0 || qdCount < 1 || !ValidateDnsQuestionSection(queryPacket, response))
                    return (DnsSampleStatus.InvalidResponse, -1, "MalformedHeaderOrQuestion", false);

                // If UDP response was truncated (TC bit = 1), fall back to TCP Port 53 (Section 6)
                if (tcBit)
                {
                    return ExecuteTcpDnsFallback(targetEp, queryPacket, txId, timeoutMs);
                }

                if (rcode != 0)
                    return (DnsSampleStatus.RcodeError, sw.ElapsedMilliseconds, $"RCODE:{rcode}", false);

                return (DnsSampleStatus.Success, Math.Max(1, sw.ElapsedMilliseconds), "NOERROR", false);
            }
            catch (SocketException sex)
            {
                if (sex.SocketErrorCode == SocketError.TimedOut)
                    return (DnsSampleStatus.Timeout, -1, "Timeout", false);
                return (DnsSampleStatus.NetworkError, -1, sex.SocketErrorCode.ToString(), false);
            }
            catch (Exception ex)
            {
                return (DnsSampleStatus.NetworkError, -1, ex.GetType().Name, false);
            }
        }

        private static (DnsSampleStatus Status, long ElapsedMs, string Detail, bool UsedTcp) ExecuteTcpDnsFallback(IPEndPoint targetEp, byte[] queryPacket, ushort txId, int timeoutMs)
        {
            try
            {
                using var tcp = new TcpClient();
                tcp.SendTimeout = timeoutMs;
                tcp.ReceiveTimeout = timeoutMs;

                var sw = Stopwatch.StartNew();
                var connectTask = tcp.ConnectAsync(targetEp.Address, targetEp.Port);
                if (!connectTask.Wait(timeoutMs))
                    return (DnsSampleStatus.Timeout, -1, "TCP_Timeout", true);

                using NetworkStream ns = tcp.GetStream();
                byte[] lenPrefix = new byte[] { (byte)(queryPacket.Length >> 8), (byte)(queryPacket.Length & 0xFF) };
                ns.Write(lenPrefix, 0, 2);
                ns.Write(queryPacket, 0, queryPacket.Length);
                ns.Flush();

                byte[] respLenBuf = new byte[2];
                int prefixRead = 0;
                while (prefixRead < 2)
                {
                    int r = ns.Read(respLenBuf, prefixRead, 2 - prefixRead);
                    if (r <= 0) break;
                    prefixRead += r;
                }
                if (prefixRead < 2) return (DnsSampleStatus.InvalidResponse, -1, "TCP_ShortLen", true);
                int respLen = (respLenBuf[0] << 8) | respLenBuf[1];
                byte[] response = new byte[respLen];
                int readTotal = 0;
                while (readTotal < respLen)
                {
                    int n = ns.Read(response, readTotal, respLen - readTotal);
                    if (n <= 0) break;
                    readTotal += n;
                }
                sw.Stop();

                if (readTotal < queryPacket.Length)
                    return (DnsSampleStatus.InvalidResponse, -1, "TCP_Incomplete", true);

                ushort respTxId = (ushort)((response[0] << 8) | response[1]);
                bool qrBit = (response[2] & 0x80) != 0;
                int opcode = (response[2] >> 3) & 0x0F;
                int qdCount = (response[4] << 8) | response[5];
                int rcode = response[3] & 0x0F;
                if (respTxId != txId || !qrBit || opcode != 0 || qdCount < 1 || !ValidateDnsQuestionSection(queryPacket, response))
                    return (DnsSampleStatus.InvalidResponse, -1, "TCP_ValidationFailed", true);

                return rcode == 0
                    ? (DnsSampleStatus.Success, Math.Max(1, sw.ElapsedMilliseconds), "NOERROR_TCP", true)
                    : (DnsSampleStatus.RcodeError, sw.ElapsedMilliseconds, $"RCODE:{rcode}", true);
            }
            catch
            {
                return (DnsSampleStatus.NetworkError, -1, "TCP_FallbackFailed", true);
            }
        }

        private async void BtnTestPing_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _netBusyFlag, 1, 0) != 0)
                return;

            BtnTestPing.IsEnabled = false;

            // Immediately clear previous DNS results on both the Network tab and Dashboard card (Section 19.3)
            _lastDnsResults.Clear();
            ValDnsPing.Text = _isEnglish ? "Testing..." : "Ölçülüyor...";
            ValDnsProvider.Text = _isEnglish ? "Running 3 samples per resolver..." : "Sunucu başına 3 örneklem alınıyor...";
            TxtPingResults.Text = _isEnglish
                ? "Cleared previous results. Running 3-sample RFC 1035 DNS benchmark (UDP/TCP Port 53) and separate ICMP echo test..."
                : "Önceki sonuçlar temizlendi. 3 örneklemli gerçek RFC 1035 DNS sorgu testi (UDP/TCP Port 53) ve ayrı ICMP ping ölçümü yapılıyor...";

            var targets = new (string Name, string Ip)[]
            {
                ("Cloudflare DNS", "1.1.1.1"),
                ("Google Public DNS", "8.8.8.8"),
                ("Quad9 Security DNS", "9.9.9.9"),
                ("OpenDNS Cisco", "208.67.222.222")
            };

            try
            {
                var results = await Task.Run(() =>
                {
                    var list = new List<DnsProviderBenchmarkResult>();
                    using var ping = new Ping();

                    foreach (var t in targets)
                    {
                        var res = new DnsProviderBenchmarkResult
                        {
                            ProviderName = t.Name,
                            IpAddress = t.Ip
                        };

                        // 3 DNS query samples per provider
                        for (int s = 0; s < 3; s++)
                        {
                            var sample = ExecuteSingleDnsQuerySample(t.Ip, 1400);
                            res.LastStatus = sample.Status;
                            res.LastStatusDetail = sample.Detail;
                            if (sample.UsedTcp) res.UsedTcpFallback = true;

                            if (sample.Status == DnsSampleStatus.Success && sample.ElapsedMs >= 0)
                                res.SuccessfulLatenciesMs.Add(sample.ElapsedMs);
                            else
                                res.FailedCount++;
                        }

                        // Separate ICMP Ping check
                        try
                        {
                            PingReply reply = ping.Send(t.Ip, 1000);
                            if (reply.Status == IPStatus.Success)
                            {
                                res.IcmpRttMs = reply.RoundtripTime;
                                res.IcmpStatusText = $"{reply.RoundtripTime} ms";
                            }
                            else
                            {
                                res.IcmpStatusText = reply.Status.ToString();
                            }
                        }
                        catch (Exception pex)
                        {
                            res.IcmpStatusText = pex.GetType().Name;
                        }

                        list.Add(res);
                    }
                    return list;
                });

                _lastDnsResults.Clear();
                _lastDnsResults.AddRange(results);
                RenderDnsBenchmarkResults();
                RefreshNetworkAdaptersList();
                PlayUiFeedbackSound(true);
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("DNS_BENCH", ex.Message);
                ValDnsPing.Text = _isEnglish ? "Error" : "Hata";
                ValDnsProvider.Text = ex.Message;
            }
            finally
            {
                BtnTestPing.IsEnabled = true;
                Interlocked.Exchange(ref _netBusyFlag, 0);
            }
        }

        private void RenderDnsBenchmarkResults()
        {
            if (_lastDnsResults.Count == 0)
            {
                TxtPingResults.Text = _isEnglish
                    ? "Benchmark has not been executed yet."
                    : "Henüz test çalıştırılmadı.";
                return;
            }

            var sb = new StringBuilder();
            DnsProviderBenchmarkResult? fastest = null;

            foreach (var r in _lastDnsResults)
            {
                if (r.SuccessfulLatenciesMs.Count > 0)
                {
                    string proto = r.UsedTcpFallback ? "TCP/53" : "UDP/53";
                    sb.AppendLine(_isEnglish
                        ? $"• {r.ProviderName,-18} ({r.IpAddress,-14}) | Status: SUCCESS ({proto}) | Median: {r.MedianMs,3} ms (Min: {r.MinMs} ms, Max: {r.MaxMs} ms, Fail: {r.FailedCount}/3) | ICMP: {r.IcmpStatusText}"
                        : $"• {r.ProviderName,-18} ({r.IpAddress,-14}) | Durum: BAŞARILI ({proto}) | Medyan: {r.MedianMs,3} ms (Min: {r.MinMs} ms, Maks: {r.MaxMs} ms, Hata: {r.FailedCount}/3) | ICMP: {r.IcmpStatusText}");

                    if (fastest == null || r.MedianMs < fastest.MedianMs)
                        fastest = r;
                }
                else
                {
                    string statusLabel = r.LastStatus switch
                    {
                        DnsSampleStatus.Timeout => _isEnglish ? "TIMEOUT" : "ZAMAN AŞIMI",
                        DnsSampleStatus.InvalidResponse => _isEnglish ? "INVALID DNS RESPONSE" : "GEÇERSİZ DNS YANITI",
                        DnsSampleStatus.RcodeError => _isEnglish ? $"DNS RCODE ERROR ({r.LastStatusDetail})" : $"DNS RCODE HATASI ({r.LastStatusDetail})",
                        _ => _isEnglish ? $"NETWORK ERROR ({r.LastStatusDetail})" : $"AĞ ERİŞİM HATASI ({r.LastStatusDetail})"
                    };
                    sb.AppendLine(_isEnglish
                        ? $"• {r.ProviderName,-18} ({r.IpAddress,-14}) | Status: {statusLabel} (Fail: 3/3) | Separate ICMP: {r.IcmpStatusText}"
                        : $"• {r.ProviderName,-18} ({r.IpAddress,-14}) | Durum: {statusLabel} (Hata: 3/3) | Ayrı ICMP: {r.IcmpStatusText}");
                }
            }

            if (fastest != null)
            {
                ValDnsPing.Text = $"{fastest.MedianMs} ms";
                ValDnsProvider.Text = $"{fastest.ProviderName} ({fastest.IpAddress})";
                sb.AppendLine();
                sb.Append(_isEnglish
                    ? $"★ Fastest Verified DNS Resolver (Median): {fastest.ProviderName} ({fastest.IpAddress}) — {fastest.MedianMs} ms"
                    : $"★ En Hızlı Doğrulanmış DNS Çözümleyici (Medyan): {fastest.ProviderName} ({fastest.IpAddress}) — {fastest.MedianMs} ms");
            }
            else
            {
                ValDnsPing.Text = _isEnglish ? "Failed" : "Başarısız";
                ValDnsProvider.Text = _isEnglish ? "All DNS queries failed" : "Tüm DNS sorguları başarısız oldu";
            }

            TxtPingResults.Text = sb.ToString();
        }

        private static async Task<bool> FlushDnsResolverVerifiedAsync()
        {
            bool apiSuccess = false;
            try
            {
                uint result = DnsFlushResolverCache();
                apiSuccess = result != 0;
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("DNS_API", ex.Message);
            }

            var cmdRes = await RunSystemCommandAsync("ipconfig.exe", new[] { "/flushdns" }, 5000);
            return apiSuccess || cmdRes.Status == CommandExecStatus.Success;
        }

        private async void BtnFlushDns_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _netBusyFlag, 1, 0) != 0)
            {
                TxtPingResults.Text = _isEnglish ? "Another network operation is currently running." : "Şu anda başka bir ağ işlemi devam ediyor.";
                return;
            }
            BtnFlushDns.IsEnabled = false;
            try
            {
                bool dnsApiOk = false;
                try { dnsApiOk = DnsFlushResolverCache() != 0; } catch { }

                var ipconfigRes = await RunSystemCommandAsync("ipconfig.exe", new[] { "/flushdns" }, 5000);
                var arpRes = await RunSystemCommandAsync("arp.exe", new[] { "-d", "*" }, 5000);

                string dnsStatus = (dnsApiOk || ipconfigRes.Status == CommandExecStatus.Success)
                    ? (_isEnglish ? "SUCCESS" : "BAŞARILI")
                    : (_isEnglish ? $"FAILED ({ipconfigRes.Status})" : $"BAŞARISIZ ({ipconfigRes.Status})");

                string arpStatus = arpRes.Status switch
                {
                    CommandExecStatus.Success => _isEnglish ? "SUCCESS" : "BAŞARILI",
                    CommandExecStatus.AccessDenied => _isEnglish ? "REQUIRES ADMIN PRIVILEGE" : "YÖNETİCİ İZNİ GEREKLİ",
                    CommandExecStatus.TimedOut => _isEnglish ? "TIMED OUT" : "ZAMAN AŞIMI",
                    _ => _isEnglish ? "FAILED (Requires Elevation)" : "BAŞARISIZ (Yönetici İzni Gerekli)"
                };

                TxtPingResults.Text = _isEnglish
                    ? $"[DNS Flush Report] DNS Resolver Cache: {dnsStatus} | [ARP Reset Report] ARP Table Flush: {arpStatus}"
                    : $"[DNS Sıfırlama Raporu] DNS Çözümleyici Önbelleği: {dnsStatus} | [ARP Raporu] ARP Tablosu Temizliği: {arpStatus}";
                PlayUiFeedbackSound(dnsApiOk || ipconfigRes.Status == CommandExecStatus.Success);
            }
            finally
            {
                BtnFlushDns.IsEnabled = true;
                Interlocked.Exchange(ref _netBusyFlag, 0);
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private async void BtnResetWinsock_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _netBusyFlag, 1, 0) != 0)
            {
                TxtPingResults.Text = _isEnglish ? "Another network operation is currently running." : "Şu anda başka bir ağ işlemi devam ediyor.";
                return;
            }

            if (!IsCurrentProcessElevated())
            {
                Interlocked.Exchange(ref _netBusyFlag, 0);
                TxtPingResults.Text = _isEnglish
                    ? "Elevation Required: Winsock and TCP/IP stack reset requires Administrator privileges. Please right-click QOVX and select 'Run as administrator'."
                    : "Yönetici Yetkisi Gerekli: Winsock ve TCP/IP sıfırlama işlemi Yönetici izni gerektirir. Lütfen QOVX'e sağ tıklayıp 'Yönetici olarak çalıştır' seçeneğini kullanın.";
                return;
            }

            var confirm = MessageBox.Show(this,
                _isEnglish
                    ? "Resetting Winsock Catalog and TCP/IP stack will reset custom socket providers and requires a Windows restart afterward.\n\nDo you want to proceed?"
                    : "Winsock Kataloğu ve TCP/IP yığınını sıfırlamak ağ soket yapılandırmasını varsayılana döndürür ve işlem sonrasında bilgisayarı yeniden başlatmanız gerekir.\n\nDevam etmek istiyor musunuz?",
                "QOVX - Network Stack Reset Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                Interlocked.Exchange(ref _netBusyFlag, 0);
                return;
            }

            BtnResetWinsock.IsEnabled = false;
            try
            {
                var wsRes = await RunSystemCommandAsync("netsh.exe", new[] { "winsock", "reset" }, 6000);
                var ipRes = await RunSystemCommandAsync("netsh.exe", new[] { "int", "ip", "reset" }, 6000);

                string FormatStatus(CommandExecResult r) => r.Status switch
                {
                    CommandExecStatus.Success => _isEnglish ? "SUCCESS (Restart Required)" : "BAŞARILI (Yeniden Başlatma Gerekli)",
                    CommandExecStatus.AccessDenied => _isEnglish ? "ACCESS DENIED (Run as Administrator)" : "YETKİ YETERSİZ (Yönetici Olarak Çalıştırın)",
                    CommandExecStatus.TimedOut => _isEnglish ? "TIMED OUT" : "ZAMAN AŞIMI",
                    _ => _isEnglish ? $"FAILED (ExitCode: {r.ExitCode})" : $"BAŞARISIZ (Çıkış Kodu: {r.ExitCode})"
                };

                TxtPingResults.Text = _isEnglish
                    ? $"Individual Repair Report:\n• Winsock Catalog Reset: {FormatStatus(wsRes)}\n• TCP/IP Stack Reset: {FormatStatus(ipRes)}"
                    : $"Ayrıntılı Onarım Raporu:\n• Winsock Kataloğu Sıfırlama: {FormatStatus(wsRes)}\n• TCP/IP Yığını Sıfırlama: {FormatStatus(ipRes)}";

                PlayUiFeedbackSound(wsRes.Status == CommandExecStatus.Success && ipRes.Status == CommandExecStatus.Success);
            }
            finally
            {
                BtnResetWinsock.IsEnabled = true;
                Interlocked.Exchange(ref _netBusyFlag, 0);
            }
        }

        /// <summary>
        /// Inspects HKCU/HKLM Run, RunOnce, and User/Common Startup folders in strictly Read-Only mode (Section 9).
        /// </summary>
        private void RefreshStartupAppsList()
        {
            try
            {
                LstStartupApps.Items.Clear();
                ReadRunRegistryKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU Run");
                ReadRunRegistryKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM Run");
                ReadRunRegistryKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU RunOnce");
                ReadRunRegistryKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM RunOnce");

                ReadStartupFolderEntries(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "User Startup");
                ReadStartupFolderEntries(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Common Startup");

                if (LstStartupApps.Items.Count == 0)
                {
                    LstStartupApps.Items.Add(_isEnglish
                        ? "No startup entries found in Registry Run/RunOnce or Startup folders."
                        : "Kayıt defteri Run/RunOnce anahtarlarında veya Başlangıç klasörlerinde öğe bulunamadı.");
                }
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("STARTUP_LIST", ex.Message);
            }
        }

        private void ReadRunRegistryKey(RegistryKey root, string subKeyPath, string scopeLabel)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(subKeyPath, writable: false);
                if (key == null) return;
                foreach (string name in key.GetValueNames())
                {
                    string val = key.GetValue(name)?.ToString() ?? "";
                    LstStartupApps.Items.Add($"[{scopeLabel,-14}]  {name,-26} -> {val}");
                }
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("REG_READ", ex.Message);
            }
        }

        private void ReadStartupFolderEntries(string folderPath, string scopeLabel)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(folderPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;
                    LstStartupApps.Items.Add($"[{scopeLabel,-14}]  {fileName,-26} -> {file}");
                }
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("STARTUP_DIR", ex.Message);
            }
        }

        private void BtnRefreshStartup_Click(object sender, RoutedEventArgs e)
        {
            PlayUiFeedbackSound(false);
            RefreshStartupAppsList();
        }

        private static string GetPrivacyBackupFilePath()
        {
            return Path.Combine(App.GetAppDataDirectory(), "privacy_backup_v1.json");
        }

        private static PrivacyBackupStore LoadPrivacyBackupStore(out bool hadCorruptBackup)
        {
            hadCorruptBackup = false;
            string path = GetPrivacyBackupFilePath();
            if (!File.Exists(path))
                return new PrivacyBackupStore();

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var store = JsonSerializer.Deserialize<PrivacyBackupStore>(json);
                if (store != null && store.Snapshots != null)
                    return store;
            }
            catch (Exception ex)
            {
                hadCorruptBackup = true;
                App.LogDiagnosticEvent("PRIV_BACKUP_CORRUPT", ex.Message, ex);
                try
                {
                    string corruptBackupPath = path + ".corrupt_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
                    File.Copy(path, corruptBackupPath, overwrite: true);
                }
                catch { }
            }
            return new PrivacyBackupStore();
        }

        private static void SavePrivacyBackupStore(PrivacyBackupStore store)
        {
            try
            {
                string json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetPrivacyBackupFilePath(), json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("PRIV_BACKUP_SAVE", ex.Message);
            }
        }

        private static void CaptureBaselineSnapshotIfMissing(PrivacyBackupStore store, string id, string subKeyPath, string valueName)
        {
            if (store.Snapshots.ContainsKey(id))
                return; // Preserve original pre-QOVX user state across repeated applies!

            var snap = new RegistryValueSnapshot
            {
                Id = id,
                SubKeyPath = subKeyPath,
                ValueName = valueName
            };

            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
                if (key == null)
                {
                    snap.KeyExisted = false;
                    snap.ValueExisted = false;
                }
                else
                {
                    snap.KeyExisted = true;
                    object? val = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (val == null)
                    {
                        snap.ValueExisted = false;
                    }
                    else
                    {
                        snap.ValueExisted = true;
                        snap.ValueKindInt = (int)key.GetValueKind(valueName);
                        snap.RawDataString = Convert.ToString(val, CultureInfo.InvariantCulture);
                    }
                }
                store.Snapshots[id] = snap;
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("PRIV_SNAP", ex.Message);
            }
        }

        private static bool ApplyAndVerifyHkcuDword(PrivacyBackupStore store, string id, string subKeyPath, string valueName, int targetValue)
        {
            CaptureBaselineSnapshotIfMissing(store, id, subKeyPath, valueName);
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
                if (key == null) return false;
                key.SetValue(valueName, targetValue, RegistryValueKind.DWord);

                // Re-read to verify change took effect (Section 7)
                object? checkVal = key.GetValue(valueName);
                return checkVal is int intVal && intVal == targetValue;
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("PRIV_SET", $"{subKeyPath}\\{valueName}: {ex.Message}");
                return false;
            }
        }

        private static bool SavePrivacyBackupStoreVerified(PrivacyBackupStore store)
        {
            try
            {
                string targetPath = GetPrivacyBackupFilePath();
                string tempPath = targetPath + ".tmp";
                string json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json, Encoding.UTF8);

                string check = File.ReadAllText(tempPath, Encoding.UTF8);
                var test = JsonSerializer.Deserialize<PrivacyBackupStore>(check);
                if (test != null && test.Snapshots != null)
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                    return true;
                }
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("PRIV_BACKUP_ATOMIC_FAIL", ex.Message);
            }
            return false;
        }

        private void BtnApplyPrivacy_Click(object sender, RoutedEventArgs e)
        {
            var store = LoadPrivacyBackupStore(out bool corruptBackup);

            // STEP 1: Pre-capture all snapshots BEFORE making any changes (ChatGPT 2C fix)
            var targets = new List<(string Id, string SubKeyPath, string ValueName, int TargetVal)>();
            if (ChkAdId.IsChecked == true)
                targets.Add(("AdId", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0));
            if (ChkDiagnostic.IsChecked == true)
            {
                targets.Add(("GameDVR_Store", @"System\GameConfigStore", "GameDVR_Enabled", 0));
                targets.Add(("GameDVR_App", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0));
            }
            if (ChkStartWeb.IsChecked == true)
                targets.Add(("BingSearch", @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1));
            if (ChkTelemetryTailored.IsChecked == true)
                targets.Add(("TailoredExp", @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0));

            foreach (var t in targets)
            {
                CaptureBaselineSnapshotIfMissing(store, t.Id, t.SubKeyPath, t.ValueName);
                if (store.Snapshots.TryGetValue(t.Id, out var sObj)) sObj.AppliedTargetValue = t.TargetVal;
            }

            // STEP 2: Persist and verify backup store file to disk BEFORE modifying Registry
            if (!SavePrivacyBackupStoreVerified(store))
            {
                TxtPrivResult.Text = _isEnglish
                    ? "CRITICAL ABORT: Could not verify atomic privacy backup file on disk. Registry modification blocked for safety."
                    : "KRİTİK İPTAL: Gizlilik durum yedeği diske doğrulanamadı. Orijinal Windows ayarlarınızı riske atmamak için işlem durduruldu.";
                PlayUiFeedbackSound(false);
                return;
            }

            // STEP 3: Now apply and verify Registry modifications
            int verifiedCount = 0;
            int failedCount = 0;
            foreach (var t in targets)
            {
                try
                {
                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(t.SubKeyPath, writable: true);
                    if (key != null)
                    {
                        key.SetValue(t.ValueName, t.TargetVal, RegistryValueKind.DWord);
                        object? check = key.GetValue(t.ValueName);
                        if (check is int iv && iv == t.TargetVal)
                        {
                            verifiedCount++;
                            continue;
                        }
                    }
                    failedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    App.LogDiagnosticEvent("REG_SET_FAIL", t.SubKeyPath + "/" + t.ValueName + ": " + ex.Message);
                }
            }

            PlayUiFeedbackSound(failedCount == 0);
            TxtPrivResult.Text = _isEnglish
                ? $"Privacy Update: {verifiedCount} values verified applied after atomic backup ({failedCount} failed)."
                : $"Gizlilik Güncellemesi: {verifiedCount} değer atomik yedekleme sonrası uygulanıp doğrulandı ({failedCount} başarısız).";
        }

        private void BtnRevertPrivacy_Click(object sender, RoutedEventArgs e)
        {
            var store = LoadPrivacyBackupStore(out bool corruptBackup);
            if (corruptBackup)
            {
                TxtPrivResult.Text = _isEnglish
                    ? "Warning: Previous privacy backup file was corrupted and archived (.bak). Cannot safely auto-revert without verified baseline."
                    : "Uyarı: Önceki gizlilik yedek dosyası bozulduğu için (.bak) olarak arşivlendi. Doğrulanmış yedek olmadan geri yükleme durduruldu.";
                return;
            }

            if (store.Snapshots.Count == 0)
            {
                TxtPrivResult.Text = _isEnglish
                    ? "No prior QOVX privacy changes recorded in backup store; your original Windows settings remain untouched."
                    : "Yedek deposunda daha önce QOVX tarafından değiştirilmiş bir kayıt bulunamadı; mevcut Windows ayarlarınız korunuyor.";
                return;
            }

            int restoredCount = 0;
            int skippedExternalCount = 0;
            int failedCount = 0;

            foreach (var kvp in store.Snapshots.ToList())
            {
                var snap = kvp.Value;
                try
                {
                    // Check if value was modified externally after QOVX applied it (Section 7)
                    using (RegistryKey? checkKey = Registry.CurrentUser.OpenSubKey(snap.SubKeyPath, writable: false))
                    {
                        object? currentValObj = checkKey?.GetValue(snap.ValueName);
                        if (currentValObj is int currentInt && currentInt != snap.AppliedTargetValue)
                        {
                            var overwriteConfirm = MessageBox.Show(this,
                                _isEnglish
                                    ? $"Registry value '{snap.ValueName}' was changed externally (Current: {currentInt}, QOVX set: {snap.AppliedTargetValue}).\n\nOverwrite external change with pre-QOVX backup?"
                                    : $"'{snap.ValueName}' kayıt defteri değeri harici olarak değiştirilmiş (Mevcut: {currentInt}, QOVX ayarı: {snap.AppliedTargetValue}).\n\nHarici değişikliğin üzerine QOVX öncesi yedek yazılsın mı?",
                                "QOVX - External Registry Change Detected",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);
                            if (overwriteConfirm != MessageBoxResult.Yes)
                            {
                                skippedExternalCount++;
                                continue;
                            }
                        }
                    }

                    if (!snap.KeyExisted || !snap.ValueExisted)
                    {
                        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(snap.SubKeyPath, writable: true);
                        key?.DeleteValue(snap.ValueName, throwOnMissingValue: false);
                        bool deletedOk = key?.GetValue(snap.ValueName) == null;
                        if (deletedOk)
                        {
                            restoredCount++;
                            store.Snapshots.Remove(kvp.Key);
                        }
                        else
                        {
                            failedCount++;
                        }
                    }
                    else
                    {
                        using RegistryKey key = Registry.CurrentUser.CreateSubKey(snap.SubKeyPath, writable: true);
                        var kind = (RegistryValueKind)snap.ValueKindInt;
                        if (kind == RegistryValueKind.DWord && int.TryParse(snap.RawDataString, NumberStyles.Any, CultureInfo.InvariantCulture, out int origInt))
                        {
                            key.SetValue(snap.ValueName, origInt, RegistryValueKind.DWord);
                        }
                        else if (kind == RegistryValueKind.QWord && long.TryParse(snap.RawDataString, NumberStyles.Any, CultureInfo.InvariantCulture, out long origLong))
                        {
                            key.SetValue(snap.ValueName, origLong, RegistryValueKind.QWord);
                        }
                        else if (snap.RawDataString != null)
                        {
                            key.SetValue(snap.ValueName, snap.RawDataString, kind);
                        }

                        string? verifyStr = Convert.ToString(key.GetValue(snap.ValueName), CultureInfo.InvariantCulture);
                        if (string.Equals(verifyStr, snap.RawDataString, StringComparison.OrdinalIgnoreCase))
                        {
                            restoredCount++;
                            store.Snapshots.Remove(kvp.Key);
                        }
                        else
                        {
                            failedCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    App.LogDiagnosticEvent("PRIV_REVERT", snap.SubKeyPath + "/" + snap.ValueName + ": " + ex.Message);
                }
            }

            SavePrivacyBackupStoreVerified(store);
            PlayUiFeedbackSound(failedCount == 0);

            TxtPrivResult.Text = _isEnglish
                ? $"State Restored: {restoredCount} Registry value(s) restored & verified ({skippedExternalCount} external kept, {failedCount} failed)."
                : $"Durum Geri Yüklendi: {restoredCount} kayıt defteri değeri geri yüklendi ve doğrulandı ({skippedExternalCount} harici korundu, {failedCount} başarısız).";
        }

        private void BtnOpenStartup_Click(object sender, RoutedEventArgs e)
        {
            if (!_isCaffeineActive)
            {
                uint prev = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                if (prev == 0)
                {
                    TxtPowerStatus.Text = _isEnglish
                        ? "Failed to enable Awake Lock: SetThreadExecutionState returned 0."
                        : "Uyku Kilidi etkinleştirilemedi: SetThreadExecutionState 0 döndürdü.";
                    return;
                }
                _isCaffeineActive = true;
                TxtBtnOpenStartup.Text = _isEnglish
                    ? "Disable Awake Lock (Active)"
                    : "Uyku Kilidini Kapat (Aktif)";
                TxtPowerStatus.Text = _isEnglish
                    ? "Awake Lock (Caffeine) is ACTIVE: Windows sleep and display turn-off are inhibited (automatically released on exit)."
                    : "Uyku Kilidi (Caffeine) AKTİF: Sistem ve ekran uykusu engellendi (uygulama kapatıldığında otomatik temizlenir).";
            }
            else
            {
                uint prev = SetThreadExecutionState(ES_CONTINUOUS);
                _isCaffeineActive = false;
                TxtBtnOpenStartup.Text = _isEnglish
                    ? "Enable Awake Lock (Keep Awake)"
                    : "Uyku Kilidini (Uyanık Tut) Başlat";
                TxtPowerStatus.Text = prev != 0
                    ? (_isEnglish ? "Awake Lock disabled. Normal Windows power timers restored." : "Uyku Kilidi kapatıldı. Standart Windows uyku zamanlayıcısı devrede.")
                    : (_isEnglish ? "Awake Lock state reset warning." : "Uyku Kilidi sıfırlama uyarısı.");
            }
            PlayUiFeedbackSound(true);
        }

        private async void BtnUnlockPower_Click(object sender, RoutedEventArgs e)
        {
            BtnUnlockPower.IsEnabled = false;
            try
            {
                const string ultimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
                string? targetGuid = null;

                // Step 1: Check existing power schemes via powercfg /list so we never create duplicate schemes on repeated clicks (Section 8)
                var listRes = await RunSystemCommandAsync("powercfg.exe", new[] { "/list" }, 5000);
                if (listRes.Status == CommandExecStatus.Success && !string.IsNullOrWhiteSpace(listRes.StdOut))
                {
                    foreach (string line in listRes.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.Contains(ultimateTemplateGuid, StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Nihai Performans", StringComparison.OrdinalIgnoreCase))
                        {
                            var m = Regex.Match(line, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                            if (m.Success)
                            {
                                targetGuid = m.Value;
                                break;
                            }
                        }
                    }
                }

                // Step 2: Only duplicate if not already present in powercfg /list
                if (string.IsNullOrEmpty(targetGuid))
                {
                    var dupRes = await RunSystemCommandAsync("powercfg.exe", new[] { "/duplicatescheme", ultimateTemplateGuid }, 5000);
                    if (dupRes.Status == CommandExecStatus.Success)
                    {
                        var m = Regex.Match(dupRes.StdOut ?? "", @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                        if (m.Success)
                            targetGuid = m.Value;
                    }
                }

                if (string.IsNullOrEmpty(targetGuid))
                {
                    TxtPowerStatus.Text = _isEnglish
                        ? "Ultimate Performance scheme is not supported on this hardware (e.g. Modern Standby / S0 low-power idle system)."
                        : "Nihai Performans güç şeması bu donanımda desteklenmiyor (Modern Standby / S0 güç mimarisi).";
                    return;
                }

                // Step 3: Activate the scheme and verify via powercfg /getactivescheme
                var setRes = await RunSystemCommandAsync("powercfg.exe", new[] { "/setactive", targetGuid }, 5000);
                var activeRes = await RunSystemCommandAsync("powercfg.exe", new[] { "/getactivescheme" }, 5000);

                if (setRes.Status == CommandExecStatus.Success &&
                    activeRes.Status == CommandExecStatus.Success &&
                    activeRes.StdOut.Contains(targetGuid, StringComparison.OrdinalIgnoreCase))
                {
                    TxtPowerStatus.Text = _isEnglish
                        ? $"Verified Active: Ultimate Performance power scheme is now active (GUID: {targetGuid})."
                        : $"Doğrulandı: Nihai Performans güç planı etkinleştirildi ve aktif şema olarak doğrulandı (GUID: {targetGuid}).";
                    PlayUiFeedbackSound(true);
                }
                else
                {
                    TxtPowerStatus.Text = _isEnglish
                        ? $"Could not verify activation of GUID {targetGuid}. Active scheme output: {activeRes.StdOut.Trim()}"
                        : $"GUID {targetGuid} etkinleştirmesi doğrulanamadı. Mevcut aktif şema: {activeRes.StdOut.Trim()}";
                }
            }
            finally
            {
                BtnUnlockPower.IsEnabled = true;
            }
        }

        private async void BtnRestoreBalancedPower_Click(object sender, RoutedEventArgs e)
        {
            BtnRestoreBalancedPower.IsEnabled = false;
            try
            {
                const string balancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
                var setRes = await RunSystemCommandAsync("powercfg.exe", new[] { "/setactive", balancedGuid }, 5000);
                var activeRes = await RunSystemCommandAsync("powercfg.exe", new[] { "/getactivescheme" }, 5000);

                if (setRes.Status == CommandExecStatus.Success &&
                    activeRes.Status == CommandExecStatus.Success &&
                    activeRes.StdOut.Contains(balancedGuid, StringComparison.OrdinalIgnoreCase))
                {
                    TxtPowerStatus.Text = _isEnglish
                        ? $"Verified Active: Standard Windows Balanced power scheme ({balancedGuid}) is now active."
                        : $"Doğrulandı: Standart Windows Dengeli (Balanced) güç şeması ({balancedGuid}) aktif edildi.";
                    PlayUiFeedbackSound(true);
                }
                else
                {
                    TxtPowerStatus.Text = _isEnglish
                        ? $"Failed to restore Balanced power plan (ExitCode: {setRes.ExitCode})."
                        : $"Dengeli güç planına geçiş başarısız oldu (Çıkış Kodu: {setRes.ExitCode}).";
                }
            }
            finally
            {
                BtnRestoreBalancedPower.IsEnabled = true;
            }
        }

        private async void BtnShutdown30_Click(object sender, RoutedEventArgs e) => await ScheduleShutdownSecondsAsync(1800, 30);
        private async void BtnShutdownTimer_Click(object sender, RoutedEventArgs e) => await ScheduleShutdownSecondsAsync(3600, 60);
        private async void BtnShutdown120_Click(object sender, RoutedEventArgs e) => await ScheduleShutdownSecondsAsync(7200, 120);

        private async Task ScheduleShutdownSecondsAsync(int seconds, int minutes)
        {
            var res = await RunSystemCommandAsync("shutdown.exe", new[] { "/s", "/t", seconds.ToString(CultureInfo.InvariantCulture) }, 5000);
            if (res.Status == CommandExecStatus.Success)
            {
                _qovxScheduledShutdownUtc = DateTime.UtcNow.AddSeconds(seconds);
                TxtPowerStatus.Text = _isEnglish
                    ? $"Verified: Auto-shutdown timer scheduled by QOVX for {minutes} minutes from now."
                    : $"Doğrulandı: Otomatik sistem kapatma zamanlayıcısı QOVX tarafından {minutes} dakika sonrasına kuruldu.";
                PlayUiFeedbackSound(true);
            }
            else
            {
                TxtPowerStatus.Text = _isEnglish
                    ? $"Shutdown timer could not be set (ExitCode: {res.ExitCode} — a timer may already be active; cancel it first)."
                    : $"Kapatma zamanlayıcısı kurulamadı (Çıkış Kodu: {res.ExitCode} — zaten kurulu bir zamanlayıcı olabilir, önce iptal edin).";
            }
        }

        private async void BtnCancelShutdown_Click(object sender, RoutedEventArgs e)
        {
            bool hasQovxTimer = _qovxScheduledShutdownUtc.HasValue && _qovxScheduledShutdownUtc.Value > DateTime.UtcNow;
            if (!hasQovxTimer)
            {
                var confirm = MessageBox.Show(this,
                    _isEnglish
                        ? "No active shutdown timer was started by QOVX in this session.\nDo you still want to run 'shutdown /a' to cancel any external system shutdown timer?"
                        : "Bu oturumda QOVX tarafından başlatılmış aktif bir kapatma zamanlayıcısı bulunmuyor.\nBaşka bir araç veya kullanıcı tarafından kurulmuş olabilecek kapatma zamanlayıcısını yine de iptal etmek istiyor musunuz?",
                    "QOVX - Shutdown Timer Confirmation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            var res = await RunSystemCommandAsync("shutdown.exe", new[] { "/a" }, 5000);
            if (res.Status == CommandExecStatus.Success)
            {
                _qovxScheduledShutdownUtc = null;
                TxtPowerStatus.Text = _isEnglish
                    ? "Scheduled auto-shutdown timer has been cancelled."
                    : "Planlanan otomatik kapatma zamanlayıcısı iptal edildi.";
                PlayUiFeedbackSound(true);
            }
            else
            {
                TxtPowerStatus.Text = _isEnglish
                    ? $"No active shutdown timer was found to cancel (Code: {res.ExitCode})."
                    : $"İptal edilecek aktif bir kapatma zamanlayıcısı bulunamadı (Kod: {res.ExitCode}).";
            }
        }

        private void BtnPinTop_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = !this.Topmost;
            UpdateHeaderButtonsLocalization();
            PlayUiFeedbackSound(false);
        }

        private void BtnSoundToggle_Click(object sender, RoutedEventArgs e)
        {
            _isSoundEnabled = !_isSoundEnabled;
            UpdateHeaderButtonsLocalization();
            if (_isSoundEnabled) PlayUiFeedbackSound(false);
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            _isLightTheme = !_isLightTheme;
            if (_isLightTheme)
            {
                Resources["BgDark"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9"));
                Resources["BgSidebar"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
                Resources["BgFooter"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
                Resources["CardDark"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                Resources["CardDarkHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
                Resources["BorderDark"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
                Resources["TextWhite"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));
                Resources["TextMuted"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569"));
            }
            else
            {
                Resources["BgDark"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#090B10"));
                Resources["BgSidebar"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10131C"));
                Resources["BgFooter"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B0D14"));
                Resources["CardDark"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141824"));
                Resources["CardDarkHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C2233"));
                Resources["BorderDark"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#262B38"));
                Resources["TextWhite"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F4F5"));
                Resources["TextMuted"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA"));
            }
            UpdateHeaderButtonsLocalization();
            PlayUiFeedbackSound(false);
        }

        private void BtnYouTube_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.youtube.com/@ALLAHVERDICAN",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("YOUTUBE_LINK", ex.Message);
            }
        }

        private void BtnLang_Click(object sender, RoutedEventArgs e)
        {
            _isEnglish = !_isEnglish;
            ApplyLocalization();
            UpdateRealHardwareMetrics();
            RenderRamLastReportText();
            RenderTopProcessesListBox();
            RenderDuplicateListBox();
            UpdateDiskMetricsCards();
            RenderDnsBenchmarkResults();
            RefreshNetworkAdaptersList();
            RefreshStartupAppsList();
            PlayUiFeedbackSound(false);
        }

        private void UpdateHeaderButtonsLocalization()
        {
            if (_isEnglish)
            {
                TxtPinTop.Text = this.Topmost ? "📌 Pin Top: ON" : "📌 Pin Top: OFF";
                TxtSoundLabel.Text = _isSoundEnabled ? "🔊 Sound: ON" : "🔇 Sound: OFF";
                TxtThemeLabel.Text = _isLightTheme ? "☀️ Theme: Light" : "🌙 Theme: Dark";
                TxtLangLabel.Text = "Lang: EN (Switch TR)";
            }
            else
            {
                TxtPinTop.Text = this.Topmost ? "📌 Üstte Tut: Açık" : "📌 Üstte Tut: Kapalı";
                TxtSoundLabel.Text = _isSoundEnabled ? "🔊 Ses: Açık" : "🔇 Ses: Kapalı";
                TxtThemeLabel.Text = _isLightTheme ? "☀️ Tema: Açık" : "🌙 Tema: Koyu";
                TxtLangLabel.Text = "Dil: TR (EN'ye Geç)";
            }
        }

        private void ApplyLocalization()
        {
            UpdateHeaderButtonsLocalization();

            if (_isEnglish)
            {
                TxtHeaderSubtitle.Text = "System & Network Control Suite v3.0.0";
                TxtZeroTelemetry.Text = "● Zero Data Collection • User-Initiated DNS/Web Only";
                NavTxtDash.Text = "System Overview & Boost";
                NavTxtRam.Text = "Memory (RAM) Manager";
                NavTxtDisk.Text = "Disk & Duplicate Cleaner";
                NavTxtNet.Text = "Network, DNS & Latency";
                NavTxtPriv.Text = "Privacy & Startup Apps";
                NavTxtStart.Text = "Power, Awake Lock & Tools";
                TxtSysHealthLabel.Text = "Available Memory Ratio";

                TxtDashTitle.Text = "System Performance & Control Center";
                TxtDashSubtitle.Text = "Inspect real-time local hardware metrics and execute verified system maintenance.";
                CardTxtRam.Text = "Physical Memory Load";
                CardTxtTrash.Text = "Current Temporary Cache";
                ValTempDesc.Text = "%TEMP%, Browser & Shader Caches";
                CardTxtDns.Text = "Real DNS Query Median";
                if (_lastDnsResults.Count == 0)
                {
                    ValDnsPing.Text = "Not Tested";
                    ValDnsProvider.Text = "Run benchmark in Network tab";
                }
                TxtBtnMaster.Text = "RUN QUICK MAINTENANCE & CACHE CLEANUP";
                TxtBoostStatus.Text = "Protects critical OS services while trimming user process working sets, deleting unlocked %TEMP% files, and flushing DNS.";

                TxtRamTitle.Text = "Safe Physical Memory (RAM) Working Set Manager";
                TxtRamSubtitle.Text = "Note: EmptyWorkingSet trims inactive pages from user process working sets rather than creating physical RAM. Session-0 and kernel services are excluded.";
                TxtLiveRam.Text = "Live Physical Memory Status:";
                ChkAutoRam.Content = "Auto-trim user apps when memory load exceeds 88% (Opt-in • 5 min cooldown)";
                TxtBtnPurgeRam.Text = "Trim User Process Working Sets";
                TxtBtnRefreshProc.Text = "Refresh Process List";
                TxtBtnTrimSelectedProc.Text = "Trim Selected Process Working Set";
                TxtTopProcHeader.Text = "Top Memory-Consuming Active Processes (Top 15 • PID & StartTime Verified)";

                TxtDiskTitle.Text = "Storage Cleanup & Verified SHA-256 Duplicate Manager";
                TxtDiskSubtitle.Text = "Skips symbolic links/junctions during cache cleanup; re-verifies duplicate hashes before sending to Windows Recycle Bin.";
                LblMetricCache.Text = "1. Current Cache Size";
                LblMetricFreed.Text = "2. Actually Freed Space";
                LblMetricDup.Text = "3. Duplicate Potential Savings";
                ChkUserTemp.Content = "User Temporary Directories (%TEMP%)";
                ChkWinTemp.Content = "Windows System Temp Directory (May Require Admin)";
                ChkUpdateCache.Content = "Downloads & Documents SHA-256 Duplicate Scan";
                ChkThumbnails.Content = "DirectX Shader Cache (D3DSCache)";
                ChkBrowserCache.Content = "Browser Caches (Cleaned Only When Browser Is Closed)";
                TxtBtnScanDisk.Text = "Preview Cache & Scan Duplicates";
                TxtBtnSelectDupFolder.Text = "Scan Custom Folder...";
                TxtBtnCleanDisk.Text = "Clean Selected Caches";
                TxtBtnCancelDiskOp.Text = "Stop Operation";
                TxtBtnDeleteSelectedDup.Text = "Verify & Move Selected Duplicate to Recycle Bin";
                TxtDupListHeader.Text = "Detected SHA-256 Exact Duplicates (Duplicate Path <== Original File Path)";

                TxtNetTitle.Text = "Network Diagnostics, Multi-Sample UDP/TCP DNS Benchmark & Repair";
                TxtNetSubtitle.Text = "Measures Min/Median/Max DNS resolution across 3 samples per resolver using random TxID, Question verification, and TCP/53 fallback.";
                TxtBtnTestPing.Text = "Start Multi-Sample DNS & ICMP Benchmark";
                TxtBtnFlushDns.Text = "Flush DNS & ARP Cache";
                TxtBtnResetWinsock.Text = "Reset Winsock & TCP/IP (Admin)";
                TxtAdapterHeader.Text = "Active Network Adapters, Local IPv4, DNS Servers & Default Gateway";

                TxtPrivTitle.Text = "State-Backed Privacy Settings & Startup Auditor";
                TxtPrivSubtitle.Text = "Backs up your existing HKCU Registry values to a local JSON store before applying changes so Revert restores your exact prior state.";
                ChkAdId.Content = "Disable Advertising ID Tracking (HKCU\\...\\AdvertisingInfo -> Enabled = 0)";
                ChkDiagnostic.Content = "Disable Xbox GameDVR Background Recording (GameDVR_Enabled & AppCaptureEnabled = 0)";
                ChkStartWeb.Content = "Disable Start Menu Bing Web Search Suggestions (DisableSearchBoxSuggestions = 1)";
                ChkTelemetryTailored.Content = "Disable Tailored Diagnostic Experiences (TailoredExperiencesWithDiagnosticDataEnabled = 0)";
                TxtBtnApplyPrivacy.Text = "Backup & Apply Selected Privacy Rules";
                TxtBtnRevertPrivacy.Text = "Restore Saved Pre-QOVX User State";
                TxtBtnRefreshStartup.Text = "Refresh Startup List";
                TxtStartupListHeader.Text = "Read-Only Startup Audit (HKCU/HKLM Run, RunOnce & Startup Folders)";

                TxtStartTitle.Text = "Verified Power Scheme Manager, Awake Lock & Shutdown Timer";
                TxtStartSubtitle.Text = "Queries existing schemes to avoid duplicate creation, verifies active power GUID, and releases Awake Lock on exit.";
                TxtPowerSectionHeader.Text = "Hardware Power Scheme & Display Awake Lock (Caffeine)";
                TxtBtnOpenStartup.Text = _isCaffeineActive ? "Disable Awake Lock (Active)" : "Enable Awake Lock (Keep Awake)";
                TxtBtnUnlockPower.Text = "Verify & Activate Ultimate Performance Plan";
                TxtBtnRestoreBalancedPower.Text = "Restore Standard Balanced Power Plan";
                TxtTimerSectionHeader.Text = "Automatic System Shutdown Timer";
                TxtBtnShutdown30.Text = "Shutdown in 30 Min";
                TxtBtnShutdown60.Text = "Auto-Shutdown in 60 Min";
                TxtBtnShutdown120.Text = "Shutdown in 120 Min";
                TxtBtnCancelShutdown.Text = "Cancel Shutdown Timer";

                TxtFooterLeft.Text = "QOVX v3.0.0 • Publisher: ALLAHVERDİ CAN • 100% Local Architecture";
                TxtFooterRight.Text = "Windows 10 / 11 x64 Native (.NET 10 LTS)";
            }
            else
            {
                TxtHeaderSubtitle.Text = "System & Network Control Suite v3.0.0";
                TxtZeroTelemetry.Text = "● Sıfır Veri Toplama • Yalnızca Kullanıcı İsteğiyle DNS/Web";
                NavTxtDash.Text = "Sistem Özeti & Hızlandır";
                NavTxtRam.Text = "Bellek (RAM) Yönetimi";
                NavTxtDisk.Text = "Disk & Kopya Temizleyici";
                NavTxtNet.Text = "Ağ, DNS & Gecikme";
                NavTxtPriv.Text = "Gizlilik & Başlangıç";
                NavTxtStart.Text = "Güç, Uyku Kilidi & Araçlar";
                TxtSysHealthLabel.Text = "Kullanılabilir Bellek Oranı";

                TxtDashTitle.Text = "Sistem Performans & Kontrol Paneli";
                TxtDashSubtitle.Text = "Gerçek donanım verilerini yerel olarak inceleyin ve güvenli sistem bakımını çalıştırın.";
                CardTxtRam.Text = "Fiziksel Bellek Yükü";
                CardTxtTrash.Text = "Mevcut Geçici Önbellek";
                ValTempDesc.Text = "%TEMP%, Tarayıcı ve Shader Önbelleği";
                CardTxtDns.Text = "Gerçek DNS Sorgu Medyanı";
                if (_lastDnsResults.Count == 0)
                {
                    ValDnsPing.Text = "Ölçülmedi";
                    ValDnsProvider.Text = "Test için Ağ sekmesini kullanın";
                }
                TxtBtnMaster.Text = "HIZLI BAKIM VE ÖNBELLEK TEMİZLİĞİNİ BAŞLAT";
                TxtBoostStatus.Text = "Kritik sistem süreçlerini koruyarak kullanıcı süreçlerinin çalışma kümelerini daraltır, kilitsiz %TEMP% dosyalarını siler ve DNS önbelleğini sıfırlar.";

                TxtRamTitle.Text = "Güvenli Bellek (RAM) Çalışma Kümesi Yönetimi";
                TxtRamSubtitle.Text = "Not: EmptyWorkingSet fiziksel RAM üretmez; kullanıcı uygulamalarının pasif bellek sayfalarını çalışma kümesinden daraltır. Oturum-0 ve çekirdek süreçleri korunur.";
                TxtLiveRam.Text = "Canlı Fiziksel Bellek Durumu:";
                ChkAutoRam.Content = "Bellek yükü %88'i aştığında otomatik daralt (İsteğe Bağlı • 5 dk soğuma süreli)";
                TxtBtnPurgeRam.Text = "Kullanıcı Süreçlerinin Çalışma Kümesini Daralt";
                TxtBtnRefreshProc.Text = "Süreç Listesini Yenile";
                TxtBtnTrimSelectedProc.Text = "Seçili Sürecin Çalışma Kümesini Daralt";
                TxtTopProcHeader.Text = "En Çok Bellek Tüketen Aktif Süreçler (İlk 15 • PID ve Başlangıç Zamanı Doğrulamalı)";

                TxtDiskTitle.Text = "Depolama Temizliği & Doğrulamalı SHA-256 Kopya Yöneticisi";
                TxtDiskSubtitle.Text = "Sembolik bağlantıları (Junction/Symlink) atlayarak önbellekleri temizler; kopya dosyaları yeniden doğrulayıp Geri Dönüşüm Kutusu'na taşır.";
                LblMetricCache.Text = "1. Mevcut Önbellek Boyutu";
                LblMetricFreed.Text = "2. Gerçekten Silinen Alan";
                LblMetricDup.Text = "3. Kopya Dosya Potansiyel Kazancı";
                ChkUserTemp.Content = "Kullanıcı Geçici Dizinleri (%TEMP%)";
                ChkWinTemp.Content = "Windows Temp Dizini (Yönetici İzni Gerekebilir)";
                ChkUpdateCache.Content = "İndirilenler & Belgeler SHA-256 Kopya Taraması";
                ChkThumbnails.Content = "DirectX Shader (D3DSCache) Önbelleği";
                ChkBrowserCache.Content = "Tarayıcı Önbellekleri (Yalnızca Kapalıysa Temizlenir)";
                TxtBtnScanDisk.Text = "Önizleme ve Kopyaları Tara";
                TxtBtnSelectDupFolder.Text = "Özel Klasör Seç & Kopya Ara";
                TxtBtnCleanDisk.Text = "Seçili Önbellekleri Temizle";
                TxtBtnCancelDiskOp.Text = "İşlemi Durdur";
                TxtBtnDeleteSelectedDup.Text = "Seçili Kopyayı Doğrula & Geri Dönüşüme Gönder";
                TxtDupListHeader.Text = "Tespit Edilen SHA-256 Kopya Dosyalar (Kopya Konumu ➔ Orijinal Dosya Konumu)";

                TxtNetTitle.Text = "Ağ Tanılama, Çoklu Örneklem UDP/TCP DNS Kıyaslama & Onarım";
                TxtNetSubtitle.Text = "Rastgele Transaction ID, QR/RCODE/Soru doğrulaması ve TCP/53 yedeklemesiyle 3'er örneklem üzerinden Min/Medyan/Maks DNS gecikmesini ölçün.";
                TxtBtnTestPing.Text = "Çoklu Örneklem DNS & ICMP Testini Başlat";
                TxtBtnFlushDns.Text = "DNS ve ARP Önbelleğini Sıfırla";
                TxtBtnResetWinsock.Text = "Winsock & TCP/IP Sıfırla (Yönetici)";
                TxtAdapterHeader.Text = "Aktif Ağ Adaptörleri, Yerel IPv4, DNS Sunucuları ve Ağ Geçidi Bilgileri";

                TxtPrivTitle.Text = "Durum Yedeklemeli Gizlilik Ayarları & Başlangıç Denetçisi";
                TxtPrivSubtitle.Text = "Değişiklik öncesi mevcut Registry değerleriniz yerel JSON yedeğine kaydedilir; geri alma işlemi önceki kişisel ayarlarınızı birebir geri yükler.";
                ChkAdId.Content = "Reklam Kimliği Takibini Kapat (HKCU\\...\\AdvertisingInfo -> Enabled = 0)";
                ChkDiagnostic.Content = "Xbox GameDVR Arka Plan Oyun/Ekran Kaydını Kapat (GameDVR_Enabled & AppCaptureEnabled = 0)";
                ChkStartWeb.Content = "Başlat Menüsü Bing Web Arama Önerilerini Kapat (DisableSearchBoxSuggestions = 1)";
                ChkTelemetryTailored.Content = "Tanılama Verisiyle Kişiselleştirilmiş İpuçlarını Kapat (TailoredExperiencesWithDiagnosticDataEnabled = 0)";
                TxtBtnApplyPrivacy.Text = "Yedekle ve Seçili Gizlilik Kurallarını Uygula";
                TxtBtnRevertPrivacy.Text = "Kaydedilen Önceki Kullanıcı Durumuna Geri Yükle";
                TxtBtnRefreshStartup.Text = "Başlangıç Listesini Yenile";
                TxtStartupListHeader.Text = "Salt Okunur Başlangıç Denetimi (HKCU/HKLM Run, RunOnce ve Startup Klasörleri)";

                TxtStartTitle.Text = "Doğrulamalı Güç Planı Yönetimi, Uyku Kilidi & Zamanlayıcı";
                TxtStartSubtitle.Text = "Mevcut güç şemalarını sorgular, mükerrer plan üretmeden Nihai Performans planını etkinleştirip doğrular ve çıkışta uyku kilidini temizler.";
                TxtPowerSectionHeader.Text = "Donanım Güç Planı ve Ekran Uyku Kilidi (Caffeine)";
                TxtBtnOpenStartup.Text = _isCaffeineActive ? "Uyku Kilidini Kapat (Aktif)" : "Uyku Kilidini (Uyanık Tut) Başlat";
                TxtBtnUnlockPower.Text = "Nihai Performans Planını Doğrula ve Etkinleştir";
                TxtBtnRestoreBalancedPower.Text = "Standart Dengeli (Balanced) Güç Planına Dön";
                TxtTimerSectionHeader.Text = "Otomatik Sistem Kapatma Zamanlayıcısı";
                TxtBtnShutdown30.Text = "30 Dk Sonra Kapat";
                TxtBtnShutdown60.Text = "60 Dk Sonra Otomatik Kapat";
                TxtBtnShutdown120.Text = "120 Dk Sonra Kapat";
                TxtBtnCancelShutdown.Text = "Zamanlayıcıyı İptal Et";

                TxtFooterLeft.Text = "QOVX v3.0.0 • Yayıncı: ALLAHVERDİ CAN • %100 Yerel Mimari";
                TxtFooterRight.Text = "Windows 10 / 11 x64 Native (.NET 10 LTS)";
            }
        }

        /// <summary>
        /// Executes a Windows system tool from Environment.SystemDirectory using ArgumentList, concurrent stream reading, and controlled timeout termination (Section 5).
        /// </summary>
        private static async Task<CommandExecResult> RunSystemCommandAsync(string exeName, IEnumerable<string> args, int timeoutMs = 5000)
        {
            var result = new CommandExecResult();
            try
            {
                string sysDir = Environment.SystemDirectory;
                string fullExePath = Path.Combine(sysDir, exeName);
                if (!File.Exists(fullExePath))
                    fullExePath = exeName;

                var psi = new ProcessStartInfo
                {
                    FileName = fullExePath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                foreach (string a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    result.Status = CommandExecStatus.Failed;
                    return result;
                }

                Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
                Task<string> errTask = proc.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(timeoutMs);
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                    result.StdOut = await outTask;
                    result.StdErr = await errTask;
                    result.ExitCode = proc.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync();
                    }
                    catch { }
                    try { result.StdOut = await outTask; } catch { }
                    try { result.StdErr = await errTask; } catch { }
                    result.Status = CommandExecStatus.TimedOut;
                    return result;
                }

                if (result.ExitCode == 0)
                {
                    result.Status = CommandExecStatus.Success;
                }
                else
                {
                    string combined = (result.StdOut + " " + result.StdErr).ToLowerInvariant();
                    if (result.ExitCode == 5 || combined.Contains("access is denied") || combined.Contains("erişim engellendi") || combined.Contains("elevation") || combined.Contains("administrator"))
                        result.Status = CommandExecStatus.AccessDenied;
                    else
                        result.Status = CommandExecStatus.Failed;
                }
            }
            catch (Win32Exception wex) when (wex.NativeErrorCode == 5 || wex.NativeErrorCode == 740)
            {
                result.Status = CommandExecStatus.AccessDenied;
            }
            catch (Exception ex)
            {
                App.LogDiagnosticEvent("SYS_CMD", $"{exeName}: {ex.Message}");
                result.Status = CommandExecStatus.Failed;
            }
            return result;
        }
    }
}

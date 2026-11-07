# QOVX - Windows System & Network Control Suite (v3.0.0.0)

- **Publisher:** ALLAHVERDİ CAN
- **Store Identity:** `ALLAHVERDCAN.QOVX`
- **Publisher ID:** `CN=80F86ABB-4ABB-4524-A032-0CE77FF05712`
- **Platform:** Windows 10 / Windows 11 (x64 Native WPF, .NET 10 LTS)
- **Microsoft Store Link:** https://apps.microsoft.com/detail/9P6PL8R5TLKN

---

## Technical Architecture & Engineering Highlights (v3.0.0.0)

1. **Thread-Safe & Kernel-Safe Memory Working-Set Manager:**
   - Uses `Interlocked.CompareExchange` concurrency guards so manual global trim, opt-in auto trim (`IsChecked="False"` by default, `>= 88%` threshold, 5-minute cooldown), and single-process trim never race.
   - Protects Session-0 services (`proc.SessionId == 0`) and critical Windows processes (`System`, `dwm`, `lsass`, `csrss`, `svchost`, `audiodg`, `MsMpEng`, `explorer`).
   - Uses a strongly-typed `ProcessRecord` model (`ProcessId`, `ProcessName`, `WorkingSetBytes`, `IsProtected`, `StartTimeTicks`) and verifies both `ProcessName` and `StartTime` before trimming a selected process to prevent acting on recycled PIDs.
   - Reports evaluated process count, successful `EmptyWorkingSet` API calls, protected/skipped count, and measured physical RAM before/after (`GlobalMemoryStatusEx`) separately.

2. **Safe Storage Cache Cleaner & 3-Metric Reporting:**
   - Resolves the Windows Temp path dynamically via `Environment.SpecialFolder.Windows`.
   - Skips symbolic links, junctions, and reparse points (`FileAttributes.ReparsePoint`) to prevent out-of-scope traversal and does not overwrite existing file attributes.
   - Dynamically discovers Chrome, Edge, and Brave profile directories (`Default`, `Profile *`), targeting only safe cache folders (`Cache\Cache_Data`, `Code Cache`, `GPUCache`) while skipping any browser that is currently running.
   - Separates 3 distinct metrics in the UI: **(1) Current Cache Size**, **(2) Actually Freed Space**, and **(3) Duplicate Potential Savings**.

3. **Verified SHA-256 Duplicate Manager with Recycle Bin Protection:**
   - Uses 64 KB buffered stream hashing (`IncrementalHash`) with per-chunk cancellation (`token.ThrowIfCancellationRequested()`), in-flight file modification detection (`LastWriteTimeUtc` and `Length` verified before and after hashing), and NTFS FileIndex/Hardlink detection (`GetFileInformationByHandle`) so hardlinks to the same MFT entry are not miscounted.
   - Re-verifies target file size and SHA-256 hash immediately before deletion, confirms with the user, and moves the duplicate to the **Windows Recycle Bin** (`SHFileOperation` with `FOF_ALLOWUNDO`).

4. **Multi-Sample RFC 1035 UDP/TCP DNS Benchmark & Deadlock-Free Process Runner:**
   - Clears prior DNS results immediately on test start.
   - Sends 3 RFC 1035 DNS A-record queries per provider using cryptographically random 16-bit Transaction IDs, verifies source IP/port, `QR`, `Opcode`, `RCODE`, and Question section (`example.com`), and falls back to **TCP Port 53** when the `TC` (Truncated) bit is set.
   - Reports Min, Median, Max (ms), failed sample count, and separate ICMP Ping RTT.
   - Executes system tools (`ipconfig.exe`, `arp.exe`, `netsh.exe`, `powercfg.exe`, `shutdown.exe`) from `Environment.SystemDirectory` using `ProcessStartInfo.ArgumentList`, concurrent `StandardOutput`/`StandardError` reading, and controlled timeout termination.

5. **Persistent State-Backed Reversible Privacy Shield:**
   - Captures pre-modification `HKCU` Registry state (`KeyExisted`, `ValueExisted`, `ValueKind`, `RawDataString`) to `%LOCALAPPDATA%\QOVX_Data\privacy_backup_v1.json` without overwriting baseline snapshots on repeated applies.
   - Re-reads Registry values to verify writes and restores the exact prior user configuration upon Revert.

6. **Verified Power Scheme Management & Clean Exit Lifecycle:**
   - Queries `powercfg /list` before duplicating so Ultimate Performance is never duplicated repeatedly, activates the scheme, and verifies activation via `powercfg /getactivescheme`.
   - Releases active `SetThreadExecutionState` Awake Lock and cancels background tasks cleanly when the window closes.

---

## Privacy Policy / Gizlilik Politikası
**Last Updated:** November 2026

### 1. Zero Data Collection (100% Local Execution)
QOVX does not collect, store, transmit, or share any personal information, telemetry, hardware identifiers, browsing history, or usage analytics. All memory working-set management, cache cleanup, SHA-256 duplicate scanning, and Registry privacy operations execute 100% locally on the user's device. Diagnostic logs stored in `%LOCALAPPDATA%\QOVX_Data\qovx_diagnostics.log` sanitize user profile paths (`%USERPROFILE%`) and never leave the device.

### 2. Network Usage & External Links
- No user registration, login, or third-party analytics SDK is included.
- Network packets are sent exclusively when the user manually clicks **Start Multi-Sample DNS & ICMP Benchmark** (sending standard DNS queries and ICMP echo requests to public resolvers `1.1.1.1`, `8.8.8.8`, `9.9.9.9`, `208.67.222.222`) or clicks the developer's official YouTube channel button (`https://www.youtube.com/@ALLAHVERDICAN`).

### 3. Contact & Publisher Information
- **Publisher:** ALLAHVERDİ CAN
- **Official Channel:** https://www.youtube.com/@ALLAHVERDICAN

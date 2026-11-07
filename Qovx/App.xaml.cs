using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Qovx
{
    /// <summary>
    /// Thread-safe local diagnostic logger with 512 KB log rotation and path sanitization.
    /// </summary>
    public static class Logger
    {
        private static readonly object SyncRoot = new object();
        private const long MaxLogFileBytes = 512 * 1024;

        public static string GetAppDataDirectory()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QOVX_Data");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string Sanitize(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            string clean = input.Replace("\r", " ").Replace("\n", " | ");

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
                clean = clean.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

            string userName = Environment.UserName;
            if (!string.IsNullOrEmpty(userName) && userName.Length > 2)
                clean = clean.Replace(userName, "%USERNAME%", StringComparison.OrdinalIgnoreCase);

            return clean;
        }

        public static void Log(string level, string category, string detail, Exception? ex = null)
        {
            string safeLevel = Sanitize(level);
            string safeCat = Sanitize(category);
            string safeDetail = Sanitize(detail);
            string stackPart = ex?.StackTrace != null
                ? " | Stack: " + Sanitize(ex.StackTrace)
                : string.Empty;

            string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC] [{safeLevel}] [{safeCat}] {safeDetail}{stackPart}{Environment.NewLine}";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (SyncRoot)
                    {
                        string logFile = Path.Combine(GetAppDataDirectory(), "qovx_diagnostics.log");
                        if (File.Exists(logFile))
                        {
                            var fi = new FileInfo(logFile);
                            if (fi.Length >= MaxLogFileBytes)
                            {
                                string archiveFile = Path.Combine(GetAppDataDirectory(), "qovx_diagnostics.old.log");
                                File.Move(logFile, archiveFile, overwrite: true);
                            }
                        }
                        File.AppendAllText(logFile, line);
                    }
                }
                catch
                {
                }
            });
        }

        public static void Info(string category, string detail) => Log("INFO", category, detail);
        public static void Warn(string category, string detail) => Log("WARN", category, detail);
        public static void Error(string category, string detail, Exception? ex = null) => Log("ERROR", category, detail, ex);
    }

    public partial class App : Application
    {
        public static string GetAppDataDirectory() => Logger.GetAppDataDirectory();

        public static void LogDiagnosticEvent(string category, string detail, Exception? ex = null)
        {
            Logger.Log(ex != null ? "ERROR" : "INFO", category, detail, ex);
        }

        private static bool IsFatalCorruptedStateException(Exception? ex)
        {
            return ex is OutOfMemoryException
                || ex is StackOverflowException
                || ex is AccessViolationException
                || ex is BadImageFormatException
                || ex is InvalidProgramException
                || ex is SEHException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            this.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Error("UI_DISPATCHER", e.Exception?.Message ?? "Unhandled UI exception", e.Exception);

            if (IsFatalCorruptedStateException(e.Exception))
            {
                e.Handled = false;
                Environment.FailFast("QOVX terminated due to an unrecoverable runtime state exception.", e.Exception);
                return;
            }

            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Logger.Error("APP_DOMAIN", ex.Message, ex);
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Logger.Error("TASK_SCHEDULER", e.Exception?.Message ?? "Unobserved task exception", e.Exception);
            if (!IsFatalCorruptedStateException(e.Exception?.InnerException))
            {
                e.SetObserved();
            }
        }
    }
}

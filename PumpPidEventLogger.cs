using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace FanControl.PumpPid
{
    public enum PidEventCategory
    {
        BumplessMode,
        SettlingTime,
        RangeClamp,
        ConfigChange
    }

    /// <summary>
    /// Потокобезопасный и межпроцессно-безопасный логгер событий PID-регулятора
    /// с автоматической ротацией лог-файла (> 512 КБ) и защитой от дублей [CONFIG_CHANGE].
    /// Используется совместно библиотекой плагина (FanControl.PumpPid.dll) и конфигуратором (PumpPidConfigurator.exe).
    /// </summary>
    public sealed class PumpPidEventLogger
    {
        public const long MaxLogFileBytes = 524288; // 512 KB
        private const string LogMutexName = @"LocalFanControl_PumpPid_LogMutex";

        private readonly Action<string> _pluginLogSink;
        private readonly string _logFilePath;
        private readonly object _fileLock = new object();
        private bool _enableFileLogging = true;

        public bool EnableFileLogging
        {
            get { return _enableFileLogging; }
            set { _enableFileLogging = value; }
        }

        public PumpPidEventLogger(Action<string> pluginLogSink, string logFilePath, bool enableFileLogging)
        {
            _pluginLogSink = pluginLogSink;
            _logFilePath = logFilePath;
            _enableFileLogging = enableFileLogging;
        }

        public void LogEvent(PidEventCategory category, string message)
        {
            string tag = "PID_EVENT";
            if (category == PidEventCategory.BumplessMode) tag = "MODE_BUMPLESS";
            else if (category == PidEventCategory.SettlingTime) tag = "SETTLING_TIME";
            else if (category == PidEventCategory.RangeClamp) tag = "RANGE_CLAMP";
            else if (category == PidEventCategory.ConfigChange) tag = "CONFIG_CHANGE";

            try
            {
                if (_pluginLogSink != null)
                {
                    _pluginLogSink("[PumpPID][" + tag + "] " + message);
                }
            }
            catch
            {
            }

            if (!_enableFileLogging || string.IsNullOrEmpty(_logFilePath))
                return;

            try
            {
                string payload = "[" + tag + "] " + message;
                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff} {1}{2}",
                    DateTime.Now,
                    payload,
                    Environment.NewLine);

                lock (_fileLock)
                {
                    using (Mutex mutex = new Mutex(false, LogMutexName))
                    {
                        bool acquired = false;
                        try
                        {
                            try
                            {
                                acquired = mutex.WaitOne(1500);
                            }
                            catch (AbandonedMutexException)
                            {
                                acquired = true;
                            }

                            if (category == PidEventCategory.ConfigChange && IsDuplicateLastConfigChange(_logFilePath, payload))
                            {
                                return;
                            }

                            RotateLogFileIfNeeded(_logFilePath);
                            File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                        }
                        finally
                        {
                            if (acquired)
                            {
                                try { mutex.ReleaseMutex(); } catch { }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void RotateLogFileIfNeeded(string logFilePath)
        {
            if (!File.Exists(logFilePath))
                return;

            FileInfo fi = new FileInfo(logFilePath);
            if (fi.Length > MaxLogFileBytes)
            {
                string oldPath = logFilePath + ".old";
                if (File.Exists(oldPath))
                {
                    File.Delete(oldPath);
                }
                File.Move(logFilePath, oldPath);
            }
        }

        /// <summary>
        /// Предотвращает дублирование записи [CONFIG_CHANGE], когда конфигуратор PumpPidConfigurator.exe
        /// уже записал изменение в PumpPidEvents.log, а спустя ~200 мс плагин в FanControl подхватил тот же файл по FileSystemWatcher.
        /// </summary>
        private static bool IsDuplicateLastConfigChange(string logFilePath, string payload)
        {
            try
            {
                if (!File.Exists(logFilePath))
                    return false;

                using (FileStream fs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length == 0)
                        return false;

                    int readBytes = (int)Math.Min(2048L, fs.Length);
                    fs.Seek(-readBytes, SeekOrigin.End);
                    byte[] buffer = new byte[readBytes];
                    int totalRead = 0;
                    while (totalRead < readBytes)
                    {
                        int n = fs.Read(buffer, totalRead, readBytes - totalRead);
                        if (n <= 0) break;
                        totalRead += n;
                    }

                    string tail = Encoding.UTF8.GetString(buffer, 0, totalRead).TrimEnd('\r', '\n', ' ');
                    if (string.IsNullOrEmpty(tail))
                        return false;

                    int lastNewLine = tail.LastIndexOf('\n');
                    string lastLine = (lastNewLine >= 0) ? tail.Substring(lastNewLine + 1).Trim() : tail.Trim();
                    return lastLine.EndsWith(payload, StringComparison.Ordinal);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
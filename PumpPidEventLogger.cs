using System;
using System.IO;
using FanControl.Plugins;

namespace FanControl.PumpPid
{
    public enum PidEventCategory
    {
        BumplessMode,
        SettlingTime,
        RangeClamp
    }

    /// <summary>
    /// Потокобезопасный логгер событий PID-регулятора с автоматической ротацией лог-файла (> 512 КБ).
    /// Дублирует ключевые события в штатный IPluginLogger самого FanControl
    /// и записывает подробный журнал в файл PumpPidEvents.log.
    /// </summary>
    public sealed class PumpPidEventLogger
    {
        private const long MaxLogFileBytes = 524288; // 512 KB
        private readonly IPluginLogger _pluginLogger;
        private readonly string _logFilePath;
        private readonly object _fileLock = new object();

        public bool EnableFileLogging { get; set; } = true;

        public PumpPidEventLogger(IPluginLogger pluginLogger, string logFilePath, bool enableFileLogging)
        {
            _pluginLogger = pluginLogger;
            _logFilePath = logFilePath;
            EnableFileLogging = enableFileLogging;
        }

        public void LogEvent(PidEventCategory category, string message)
        {
            string tag = "PID_EVENT";
            if (category == PidEventCategory.BumplessMode) tag = "MODE_BUMPLESS";
            else if (category == PidEventCategory.SettlingTime) tag = "SETTLING_TIME";
            else if (category == PidEventCategory.RangeClamp) tag = "RANGE_CLAMP";

            try
            {
                _pluginLogger?.Log($"[PumpPID][{tag}] {message}");
            }
            catch
            {
            }

            if (!EnableFileLogging || string.IsNullOrEmpty(_logFilePath))
                return;

            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}";
                lock (_fileLock)
                {
                    if (File.Exists(_logFilePath))
                    {
                        var fi = new FileInfo(_logFilePath);
                        if (fi.Length > MaxLogFileBytes)
                        {
                            string oldPath = _logFilePath + ".old";
                            if (File.Exists(oldPath))
                            {
                                File.Delete(oldPath);
                            }
                            File.Move(_logFilePath, oldPath);
                        }
                    }
                    File.AppendAllText(_logFilePath, line);
                }
            }
            catch
            {
            }
        }
    }
}
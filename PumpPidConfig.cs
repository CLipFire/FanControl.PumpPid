using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FanControl.PumpPid
{
    public sealed class PumpPidConfig
    {
        public string UiLanguage { get; set; }
        public string SourceSensorId { get; set; }
        public float SetpointCelsius { get; set; }
        public float Kp { get; set; }
        public float Ki { get; set; }
        public float Kd { get; set; }
        public float DerivativeFilterTau { get; set; }
        public float MinOutputPercent { get; set; }
        public float MaxOutputPercent { get; set; }
        public bool IsAutoMode { get; set; }
        public float ManualOutputPercent { get; set; }
        public bool EnableFileLogging { get; set; }
        public float SettlingBandCelsius { get; set; }

        public PumpPidConfig()
        {
            UiLanguage = "ru";
            SourceSensorId = "Core Average";
            SetpointCelsius = 60f;
            Kp = 0.2f;
            Ki = 0.4f;
            Kd = 1.2f;
            DerivativeFilterTau = 2f;
            MinOutputPercent = 25f;
            MaxOutputPercent = 75f;
            IsAutoMode = true;
            ManualOutputPercent = 25f;
            EnableFileLogging = true;
            SettlingBandCelsius = 0.5f;
        }
    }

    /// <summary>
    /// Единый менеджер конфигурации для плагина (FanControl.PumpPid.dll) и конфигуратора (PumpPidConfigurator.exe).
    /// Поддерживает горячую перезагрузку (FileSystemWatcher + проверка времени записи)
    /// и единую логику сравнения параметров (BuildConfigDiff) с записью в лог через PumpPidEventLogger.
    /// </summary>
    public sealed class PumpPidConfigManager : IDisposable
    {
        private readonly string _configPath;
        private readonly PumpPidEventLogger _eventLogger;
        private readonly object _syncLock = new object();
        private FileSystemWatcher _watcher;
        private DateTime _lastReloadTime = DateTime.MinValue;
        private DateTime _lastKnownFileWriteUtc = DateTime.MinValue;
        private PumpPidConfig _current = new PumpPidConfig();
        private bool _isInitialLoad = true;

        public PumpPidConfig Current
        {
            get
            {
                lock (_syncLock)
                {
                    return _current;
                }
            }
        }

        public PumpPidConfigManager(string configPath, PumpPidEventLogger eventLogger)
        {
            _configPath = configPath;
            _eventLogger = eventLogger;
        }

        public void Initialize()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    Save(_current);
                    _isInitialLoad = false;
                    _eventLogger.EnableFileLogging = _current.EnableFileLogging;
                    _eventLogger.LogEvent(
                        PidEventCategory.ConfigChange,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Created default configuration file '{0}': {1}",
                            _configPath,
                            FormatConfigSummary(_current)));
                }
                else
                {
                    ReloadFromFile();
                }

                string dir = Path.GetDirectoryName(_configPath);
                string fileName = Path.GetFileName(_configPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _watcher = new FileSystemWatcher(dir, fileName);
                    _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                    _watcher.Changed += OnConfigFileChanged;
                    _watcher.Created += OnConfigFileChanged;
                    _watcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception ex)
            {
                _eventLogger.LogEvent(PidEventCategory.ConfigChange, "Configuration initialization warning: " + ex.Message);
            }
        }

        public void CheckForExternalUpdates()
        {
            try
            {
                if (!File.Exists(_configPath))
                    return;

                DateTime writeUtc = File.GetLastWriteTimeUtc(_configPath);
                if (writeUtc > _lastKnownFileWriteUtc.AddMilliseconds(100))
                {
                    ReloadFromFile();
                }
            }
            catch
            {
            }
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            if ((DateTime.UtcNow - _lastReloadTime).TotalMilliseconds < 250)
                return;

            _lastReloadTime = DateTime.UtcNow;
            ReloadFromFile();
        }

        public void ReloadFromFile()
        {
            try
            {
                if (!File.Exists(_configPath))
                    return;

                _lastKnownFileWriteUtc = File.GetLastWriteTimeUtc(_configPath);
                string json = File.ReadAllText(_configPath, Encoding.UTF8);

                PumpPidConfig prev;
                PumpPidConfig loaded;
                bool wasInitialLoad;

                lock (_syncLock)
                {
                    prev = _current;
                    loaded = ParseJson(json, _current);
                    _current = loaded;
                    wasInitialLoad = _isInitialLoad;
                    _isInitialLoad = false;
                }

                if (wasInitialLoad)
                {
                    _eventLogger.EnableFileLogging = loaded.EnableFileLogging;
                    _eventLogger.LogEvent(
                        PidEventCategory.ConfigChange,
                        "Loaded initial configuration: " + FormatConfigSummary(loaded));
                }
                else
                {
                    List<string> changes = BuildConfigDiff(prev, loaded);
                    if (changes.Count > 0)
                    {
                        if (loaded.EnableFileLogging || prev.EnableFileLogging)
                        {
                            _eventLogger.EnableFileLogging = true;
                        }

                        _eventLogger.LogEvent(
                            PidEventCategory.ConfigChange,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Configuration updated [{0}] | Active: {1}",
                                string.Join(", ", changes.ToArray()),
                                FormatConfigSummary(loaded)));

                        _eventLogger.EnableFileLogging = loaded.EnableFileLogging;
                    }
                }
            }
            catch (Exception ex)
            {
                _eventLogger.LogEvent(PidEventCategory.ConfigChange, "Failed to read PumpPidConfig.json: " + ex.Message);
            }
        }

        public static List<string> BuildConfigDiff(PumpPidConfig prev, PumpPidConfig next)
        {
            List<string> changes = new List<string>();
            if (!string.Equals(prev.SourceSensorId ?? string.Empty, next.SourceSensorId ?? string.Empty, StringComparison.Ordinal))
                changes.Add(string.Format(CultureInfo.InvariantCulture, "Sensor: '{0}' -> '{1}'", prev.SourceSensorId, next.SourceSensorId));
            if (Math.Abs(prev.SetpointCelsius - next.SetpointCelsius) > 0.001f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "Setpoint: {0:F1}°C -> {1:F1}°C", prev.SetpointCelsius, next.SetpointCelsius));
            if (Math.Abs(prev.MinOutputPercent - next.MinOutputPercent) > 0.001f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "MinOutput: {0:F0}% -> {1:F0}%", prev.MinOutputPercent, next.MinOutputPercent));
            if (Math.Abs(prev.MaxOutputPercent - next.MaxOutputPercent) > 0.001f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "MaxOutput: {0:F0}% -> {1:F0}%", prev.MaxOutputPercent, next.MaxOutputPercent));
            if (Math.Abs(prev.Kp - next.Kp) > 0.0001f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "Kp: {0:F2} -> {1:F2}", prev.Kp, next.Kp));
            if (Math.Abs(prev.Ki - next.Ki) > 0.0001f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "Ki: {0:F2} -> {1:F2}", prev.Ki, next.Ki));
            if (Math.Abs(prev.Kd - next.Kd) > 0.0001f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "Kd: {0:F2} -> {1:F2}", prev.Kd, next.Kd));
            if (Math.Abs(prev.DerivativeFilterTau - next.DerivativeFilterTau) > 0.001f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "Tau: {0:F1}s -> {1:F1}s", prev.DerivativeFilterTau, next.DerivativeFilterTau));
            if (Math.Abs(prev.SettlingBandCelsius - next.SettlingBandCelsius) > 0.001f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "SettlingBand: ±{0:F2}°C -> ±{1:F2}°C", prev.SettlingBandCelsius, next.SettlingBandCelsius));
            if (prev.IsAutoMode != next.IsAutoMode)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "Mode: {0} -> {1}", prev.IsAutoMode ? "AUTO" : "MANUAL", next.IsAutoMode ? "AUTO" : "MANUAL"));
            if (Math.Abs(prev.ManualOutputPercent - next.ManualOutputPercent) > 0.01f)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "ManualOutput: {0:F1}% -> {1:F1}%", prev.ManualOutputPercent, next.ManualOutputPercent));
            if (prev.EnableFileLogging != next.EnableFileLogging)
                changes.Add(string.Format(CultureInfo.InvariantCulture, "EnableFileLogging: {0} -> {1}", prev.EnableFileLogging ? "true" : "false", next.EnableFileLogging ? "true" : "false"));
            if (!string.Equals(prev.UiLanguage ?? "ru", next.UiLanguage ?? "ru", StringComparison.OrdinalIgnoreCase))
                changes.Add(string.Format(CultureInfo.InvariantCulture, "UiLanguage: '{0}' -> '{1}'", prev.UiLanguage ?? "ru", next.UiLanguage ?? "ru"));
            return changes;
        }

        public static string FormatConfigSummary(PumpPidConfig cfg)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Sensor='{0}', Setpoint={1:F1}°C, Range=[{2:F0}..{3:F0}%], Kp={4:F2}, Ki={5:F2}, Kd={6:F2}, Tau={7:F1}s, Band=±{8:F2}°C, Mode={9}, Manual={10:F1}%, FileLog={11}",
                cfg.SourceSensorId,
                cfg.SetpointCelsius,
                cfg.MinOutputPercent,
                cfg.MaxOutputPercent,
                cfg.Kp,
                cfg.Ki,
                cfg.Kd,
                cfg.DerivativeFilterTau,
                cfg.SettlingBandCelsius,
                cfg.IsAutoMode ? "AUTO" : "MANUAL",
                cfg.ManualOutputPercent,
                cfg.EnableFileLogging ? "true" : "false");
        }

        public static string SerializeJson(PumpPidConfig cfg)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"UiLanguage\": \"{0}\",", EscapeString(cfg.UiLanguage ?? "ru")));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"SourceSensorId\": \"{0}\",", EscapeString(cfg.SourceSensorId)));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"SetpointCelsius\": {0:F1},", cfg.SetpointCelsius));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"Kp\": {0:F2},", cfg.Kp));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"Ki\": {0:F2},", cfg.Ki));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"Kd\": {0:F2},", cfg.Kd));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"DerivativeFilterTau\": {0:F1},", cfg.DerivativeFilterTau));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"MinOutputPercent\": {0:F0},", cfg.MinOutputPercent));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"MaxOutputPercent\": {0:F0},", cfg.MaxOutputPercent));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"IsAutoMode\": {0},", cfg.IsAutoMode ? "true" : "false"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"ManualOutputPercent\": {0:F1},", cfg.ManualOutputPercent));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"EnableFileLogging\": {0},", cfg.EnableFileLogging ? "true" : "false"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"SettlingBandCelsius\": {0:F2}", cfg.SettlingBandCelsius));
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Сохраняет конфигурацию и записывает дифф изменений через единый PumpPidEventLogger
        /// (с ротацией > 512 КБ, межпроцессным мьютексом и защитой от дублирования с плагином).
        /// </summary>
        public static void SaveAndLogChanges(string configPath, PumpPidConfig next, PumpPidEventLogger eventLogger)
        {
            bool hadExistingFile = File.Exists(configPath);
            string oldJson = hadExistingFile ? File.ReadAllText(configPath, Encoding.UTF8) : string.Empty;
            PumpPidConfig prev = hadExistingFile ? ParseJson(oldJson, new PumpPidConfig()) : new PumpPidConfig();

            File.WriteAllText(configPath, SerializeJson(next), Encoding.UTF8);

            if (eventLogger == null)
                return;

            List<string> diffs = hadExistingFile ? BuildConfigDiff(prev, next) : new List<string>();
            if (!hadExistingFile || diffs.Count > 0)
            {
                bool shouldLog = next.EnableFileLogging || (hadExistingFile && prev.EnableFileLogging);
                eventLogger.EnableFileLogging = shouldLog;
                if (shouldLog)
                {
                    string summary = FormatConfigSummary(next);
                    string message = diffs.Count > 0
                        ? string.Format(CultureInfo.InvariantCulture, "Configuration updated [{0}] | Active: {1}", string.Join(", ", diffs.ToArray()), summary)
                        : ("Configuration saved: " + summary);
                    eventLogger.LogEvent(PidEventCategory.ConfigChange, message);
                }
                eventLogger.EnableFileLogging = next.EnableFileLogging;
            }
        }

        public void Save(PumpPidConfig cfg)
        {
            try
            {
                File.WriteAllText(_configPath, SerializeJson(cfg), Encoding.UTF8);
                _lastKnownFileWriteUtc = File.GetLastWriteTimeUtc(_configPath);
            }
            catch (Exception ex)
            {
                _eventLogger.LogEvent(PidEventCategory.ConfigChange, "Failed to save PumpPidConfig.json: " + ex.Message);
            }
        }

        public static string EscapeString(string s)
        {
            return (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public static PumpPidConfig ParseJson(string json, PumpPidConfig fallback)
        {
            if (fallback == null) fallback = new PumpPidConfig();
            PumpPidConfig cfg = new PumpPidConfig
            {
                UiLanguage = ReadString(json, "UiLanguage", fallback.UiLanguage ?? "ru"),
                SourceSensorId = ReadString(json, "SourceSensorId", fallback.SourceSensorId),
                SetpointCelsius = ReadFloat(json, "SetpointCelsius", fallback.SetpointCelsius),
                Kp = ReadFloat(json, "Kp", fallback.Kp),
                Ki = ReadFloat(json, "Ki", fallback.Ki),
                Kd = ReadFloat(json, "Kd", fallback.Kd),
                DerivativeFilterTau = ReadFloat(json, "DerivativeFilterTau", fallback.DerivativeFilterTau),
                MinOutputPercent = ReadFloat(json, "MinOutputPercent", fallback.MinOutputPercent),
                MaxOutputPercent = ReadFloat(json, "MaxOutputPercent", fallback.MaxOutputPercent),
                IsAutoMode = ReadBool(json, "IsAutoMode", fallback.IsAutoMode),
                ManualOutputPercent = ReadFloat(json, "ManualOutputPercent", fallback.ManualOutputPercent),
                EnableFileLogging = ReadBool(json, "EnableFileLogging", fallback.EnableFileLogging),
                SettlingBandCelsius = ReadFloat(json, "SettlingBandCelsius", fallback.SettlingBandCelsius)
            };
            return cfg;
        }

        public static string ReadString(string json, string key, string defaultVal)
        {
            if (string.IsNullOrEmpty(json)) return defaultVal;
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : defaultVal;
        }

        public static float ReadFloat(string json, string key, float defaultVal)
        {
            if (string.IsNullOrEmpty(json)) return defaultVal;
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([-+]?[0-9]*\\.?[0-9]+)", RegexOptions.IgnoreCase);
            float v;
            if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return defaultVal;
        }

        public static bool ReadBool(string json, string key, bool defaultVal)
        {
            if (string.IsNullOrEmpty(json)) return defaultVal;
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            bool b;
            if (m.Success && bool.TryParse(m.Groups[1].Value, out b))
                return b;
            return defaultVal;
        }

        public void Dispose()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Changed -= OnConfigFileChanged;
                    _watcher.Created -= OnConfigFileChanged;
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            catch
            {
            }
        }
    }
}
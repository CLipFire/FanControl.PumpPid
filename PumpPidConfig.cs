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
        public string UiLanguage { get; set; } = "ru";
        public string SourceSensorId { get; set; } = "Core Average";
        public float SetpointCelsius { get; set; } = 60f;
        public float Kp { get; set; } = 0.4f;
        public float Ki { get; set; } = 0.8f;
        public float Kd { get; set; } = 1.8f;
        public float DerivativeFilterTau { get; set; } = 2f;
        public float MinOutputPercent { get; set; } = 25f;
        public float MaxOutputPercent { get; set; } = 75f;
        public bool IsAutoMode { get; set; } = true;
        public float ManualOutputPercent { get; set; } = 25f;
        public bool EnableFileLogging { get; set; } = true;
        public float SettlingBandCelsius { get; set; } = 0.3f;
    }

    /// <summary>
    /// Потокобезопасный менеджер конфигурации с горячей перезагрузкой (FileSystemWatcher + проверка времени записи).
    /// Фиксирует все изменения параметров конфигурации в английском логе [CONFIG_CHANGE].
    /// </summary>
    public sealed class PumpPidConfigManager : IDisposable
    {
        private readonly string _configPath;
        private readonly PumpPidEventLogger _eventLogger;
        private readonly object _syncLock = new object();
        private FileSystemWatcher? _watcher;
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

                string? dir = Path.GetDirectoryName(_configPath);
                string fileName = Path.GetFileName(_configPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _watcher = new FileSystemWatcher(dir, fileName)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                    };
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
                        if (loaded.EnableFileLogging)
                        {
                            _eventLogger.EnableFileLogging = true;
                        }

                        _eventLogger.LogEvent(
                            PidEventCategory.ConfigChange,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Configuration updated [{0}] | Active: {1}",
                                string.Join(", ", changes),
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
            var changes = new List<string>();
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

        public void Save(PumpPidConfig cfg)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"UiLanguage\": \"{EscapeString(cfg.UiLanguage ?? "ru")}\",");
                sb.AppendLine($"  \"SourceSensorId\": \"{EscapeString(cfg.SourceSensorId)}\",");
                sb.AppendLine($"  \"SetpointCelsius\": {cfg.SetpointCelsius.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"Kp\": {cfg.Kp.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"Ki\": {cfg.Ki.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"Kd\": {cfg.Kd.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"DerivativeFilterTau\": {cfg.DerivativeFilterTau.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"MinOutputPercent\": {cfg.MinOutputPercent.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"MaxOutputPercent\": {cfg.MaxOutputPercent.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"IsAutoMode\": {(cfg.IsAutoMode ? "true" : "false")},");
                sb.AppendLine($"  \"ManualOutputPercent\": {cfg.ManualOutputPercent.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"EnableFileLogging\": {(cfg.EnableFileLogging ? "true" : "false")},");
                sb.AppendLine($"  \"SettlingBandCelsius\": {cfg.SettlingBandCelsius.ToString(CultureInfo.InvariantCulture)}");
                sb.AppendLine("}");
                File.WriteAllText(_configPath, sb.ToString(), Encoding.UTF8);
                _lastKnownFileWriteUtc = File.GetLastWriteTimeUtc(_configPath);
            }
            catch (Exception ex)
            {
                _eventLogger.LogEvent(PidEventCategory.ConfigChange, "Failed to save PumpPidConfig.json: " + ex.Message);
            }
        }

        public static string EscapeString(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static PumpPidConfig ParseJson(string json, PumpPidConfig fallback)
        {
            var cfg = new PumpPidConfig
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

        private static string ReadString(string json, string key, string defaultVal)
        {
            var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : defaultVal;
        }

        private static float ReadFloat(string json, string key, float defaultVal)
        {
            var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*([-+]?[0-9]*\\.?[0-9]+)", RegexOptions.IgnoreCase);
            if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                return v;
            return defaultVal;
        }

        private static bool ReadBool(string json, string key, bool defaultVal)
        {
            var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (m.Success && bool.TryParse(m.Groups[1].Value, out bool b))
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
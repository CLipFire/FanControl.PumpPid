using System;
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
        public bool AllowInputTempBridgeCard { get; set; } = false;
    }

    /// <summary>
    /// Потокобезопасный менеджер конфигурации с горячей перезагрузкой (FileSystemWatcher + проверка времени записи).
    /// Позволяет менять датчик, уставку, диапазон Min..Max и коэффициенты из окна PumpPidConfigurator.exe налету!
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
                    _eventLogger.LogEvent(PidEventCategory.SettlingTime, $"Создан файл конфигурации по умолчанию: {_configPath}");
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
                _eventLogger.LogEvent(PidEventCategory.SettlingTime, $"Предупреждение конфига: {ex.Message}");
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
                PumpPidConfig loaded = ParseJson(json, _current);

                lock (_syncLock)
                {
                    _current = loaded;
                }

                _eventLogger.EnableFileLogging = loaded.EnableFileLogging;
                _eventLogger.LogEvent(
                    PidEventCategory.SettlingTime,
                    $"Конфиг применен: Sensor='{loaded.SourceSensorId}', Setpoint={loaded.SetpointCelsius:F1}°C, " +
                    $"Range=[{loaded.MinOutputPercent:F0}..{loaded.MaxOutputPercent:F0}%], " +
                    $"Kp={loaded.Kp:F2}, Ki={loaded.Ki:F2}, Kd={loaded.Kd:F2}, Mode={(loaded.IsAutoMode ? "AUTO" : "MANUAL")}");
            }
            catch (Exception ex)
            {
                _eventLogger.LogEvent(PidEventCategory.SettlingTime, $"Не удалось прочитать PumpPidConfig.json: {ex.Message}");
            }
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
                sb.AppendLine($"  \"SettlingBandCelsius\": {cfg.SettlingBandCelsius.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"AllowInputTempBridgeCard\": {(cfg.AllowInputTempBridgeCard ? "true" : "false")}");
                sb.AppendLine("}");
                File.WriteAllText(_configPath, sb.ToString(), Encoding.UTF8);
                _lastKnownFileWriteUtc = File.GetLastWriteTimeUtc(_configPath);
            }
            catch (Exception ex)
            {
                _eventLogger.LogEvent(PidEventCategory.SettlingTime, $"Ошибка сохранения конфига: {ex.Message}");
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
                SettlingBandCelsius = ReadFloat(json, "SettlingBandCelsius", fallback.SettlingBandCelsius),
                AllowInputTempBridgeCard = ReadBool(json, "AllowInputTempBridgeCard", fallback.AllowInputTempBridgeCard)
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
using System;
using System.IO;
using Newtonsoft.Json;

namespace FanControl.PidPump
{
    public class PidSettings
    {
        public string SelectedSensorId { get; set; } = string.Empty;
        public float TargetTemperature { get; set; } = 45.0f;
        public float Kp { get; set; } = 2.5f;
        public float Ki { get; set; } = 0.15f;
        public float Kd { get; set; } = 1.0f;
        public float MinOutput { get; set; } = 20.0f;
        public float MaxOutput { get; set; } = 100.0f;
        public float ManualOutput { get; set; } = 50.0f;
        public bool IsAutoMode { get; set; } = true;

        private static readonly string ConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Plugins",
            "PidPumpPlugin.json"
        );

        public static PidSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    return JsonConvert.DeserializeObject<PidSettings>(json) ?? new PidSettings();
                }
            }
            catch
            {
                // Если не удалось прочитать, возвращаем настройки по умолчанию
            }

            return new PidSettings();
        }

        public void Save()
        {
            try
            {
                string? directory = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
                // Ошибки записи конфигурации не должны прерывать работу
            }
        }
    }
}

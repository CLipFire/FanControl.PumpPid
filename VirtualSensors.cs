using System;
using FanControl.Plugins;

namespace FanControl.PumpPid
{
    /// <summary>
    /// Виртуальный выходной датчик PID-регулятора помпы.
    /// Выбирается как "Источник температуры" в линейной кривой (0°C = 0% .. 100°C = 100%) в FanControl.
    /// </summary>
    public sealed class PumpPidOutputSensor : IPluginSensor
    {
        private float? _value;

        public string Id { get; }
        public string Name { get; }
        public float? Value => _value;

        public PumpPidOutputSensor(string id, string name, float initialValue)
        {
            Id = id;
            Name = name;
            _value = initialValue;
        }

        public void SetOutput(float outputPercent)
        {
            _value = outputPercent;
        }

        public void Update()
        {
        }
    }

    /// <summary>
    /// Вспомогательный входной канал температуры (IPluginControlSensor).
    /// По умолчанию ОТКЛЮЧЕН (AllowInputTempBridgeCard = false в PumpPidConfig.json), чтобы исключить
    /// случайное замыкание петли обратной связи, если пользователь привяжет к этой карточке кривую PID Out.
    /// </summary>
    public sealed class PumpPidInputTempBridgeSensor : IPluginControlSensor
    {
        private float? _receivedTempCelsius;

        public string Id { get; }
        public string Name { get; }
        public float? Value => _receivedTempCelsius;
        public bool HasActiveInput => _receivedTempCelsius.HasValue;

        public PumpPidInputTempBridgeSensor(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public void Set(float val)
        {
            _receivedTempCelsius = val;
        }

        public void Reset()
        {
            _receivedTempCelsius = null;
        }

        public void Update()
        {
        }
    }
}
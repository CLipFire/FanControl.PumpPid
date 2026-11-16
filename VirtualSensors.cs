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
}
using System;
using FanControl.Plugins;

namespace FanControl.PidPump
{
    public class PidPumpSensor : IPluginSensor
    {
        private readonly PidController _controller;
        private readonly PidSettings _settings;
        private DateTime _lastUpdate = DateTime.UtcNow;
        private float? _value = 0f;

        public string Id => "PidPump_Output_Control";
        public string Name => "PID Pump Target Speed";
        public float? Value => _value;

        public PidPumpSensor(PidController controller, PidSettings settings)
        {
            _controller = controller;
            _settings = settings;
        }

        public void Update()
        {
            // Обновление состояния вызывается из PidPumpPlugin.Update()
        }

        public void ProcessMeasurement(float? currentTemp)
        {
            DateTime now = DateTime.UtcNow;
            float deltaTime = (float)(now - _lastUpdate).TotalSeconds;
            _lastUpdate = now;

            if (currentTemp == null || float.IsNaN(currentTemp.Value))
            {
                // При отсутствии данных держим помпу на безопасном максимальном значении
                _value = _settings.MaxOutput;
                return;
            }

            if (!_settings.IsAutoMode)
            {
                _value = _settings.ManualOutput;
                return;
            }

            _value = _controller.Compute(currentTemp.Value, deltaTime);
        }
    }
}

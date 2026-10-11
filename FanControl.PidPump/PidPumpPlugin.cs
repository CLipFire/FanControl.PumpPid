using System;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using FanControl.Plugins;

namespace FanControl.PidPump
{
    public class PidPumpPlugin : IPlugin
    {
        private readonly PidSettings _settings;
        private readonly PidController _controller;
        private PidPumpSensor? _sensor;
        private IPluginSensorsContainer? _container;
        private IPluginLogger? _logger;
        private Thread? _uiThread;
        private PidSettingsWindow? _windowInstance;

        public string Name => "PID Pump Controller";

        public PidPumpPlugin()
        {
            _settings = PidSettings.Load();
            _controller = new PidController(
                _settings.Kp,
                _settings.Ki,
                _settings.Kd,
                _settings.MinOutput,
                _settings.MaxOutput,
                _settings.TargetTemperature
            );

            if (!_settings.IsAutoMode)
            {
                _controller.SwitchToManual(_settings.ManualOutput);
            }
        }

        public void Initialize()
        {
            // Инициализация при старте FanControl
        }

        public void Load(IPluginSensorsContainer container)
        {
            _container = container;
            _sensor = new PidPumpSensor(_controller, _settings);
            container.TempSensors.Add(_sensor);

            // Запускаем окно настроек в отдельном STA потоке, чтобы не блокировать основной UI и цикл обновления FanControl
            StartSettingsWindowThread();
        }

        public void Update()
        {
            if (_sensor == null || _container == null)
            {
                return;
            }

            // Поиск сенсора, заданного пользователем в конфигурации
            IPluginSensor? sourceSensor = _container.TempSensors
                .FirstOrDefault(s => s.Id == _settings.SelectedSensorId && s != _sensor);

            float? tempValue = sourceSensor?.Value;
            _sensor.ProcessMeasurement(tempValue);
        }

        public void Close()
        {
            _settings.Save();

            if (_windowInstance != null)
            {
                _windowInstance.Dispatcher.Invoke(() =>
                {
                    _windowInstance.Close();
                });
            }
        }

        private void StartSettingsWindowThread()
        {
            _uiThread = new Thread(() =>
            {
                _windowInstance = new PidSettingsWindow(_settings, _controller, _container);
                _windowInstance.Closed += (s, e) => Dispatcher.CurrentDispatcher.InvokeShutdown();
                _windowInstance.Show();
                Dispatcher.Run();
            });

            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.IsBackground = true;
            _uiThread.Start();
        }
    }
}

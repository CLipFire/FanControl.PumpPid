using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using FanControl.Plugins;

namespace FanControl.PidPump
{
    public partial class PidSettingsWindow : Window
    {
        private readonly PidSettings _settings;
        private readonly PidController _controller;
        private readonly IPluginSensorsContainer? _container;

        public PidSettingsWindow(PidSettings settings, PidController controller, IPluginSensorsContainer? container)
        {
            InitializeComponent();
            _settings = settings;
            _controller = controller;
            _container = container;

            LoadSensors();
            LoadFields();
        }

        private void LoadSensors()
        {
            if (_container == null) return;

            CmbSensors.Items.Clear();
            foreach (IPluginSensor sensor in _container.TempSensors)
            {
                if (sensor.Id == "PidPump_Output_Control") continue;

                int index = CmbSensors.Items.Add(new SensorItem { Id = sensor.Id, DisplayName = sensor.Name });
                if (sensor.Id == _settings.SelectedSensorId)
                {
                    CmbSensors.SelectedIndex = index;
                }
            }

            if (CmbSensors.SelectedIndex == -1 && CmbSensors.Items.Count > 0)
            {
                CmbSensors.SelectedIndex = 0;
            }
        }

        private void LoadFields()
        {
            TxtTargetTemp.Text = _settings.TargetTemperature.ToString(CultureInfo.InvariantCulture);
            TxtKp.Text = _settings.Kp.ToString(CultureInfo.InvariantCulture);
            TxtKi.Text = _settings.Ki.ToString(CultureInfo.InvariantCulture);
            TxtKd.Text = _settings.Kd.ToString(CultureInfo.InvariantCulture);
            TxtMinOutput.Text = _settings.MinOutput.ToString(CultureInfo.InvariantCulture);
            TxtMaxOutput.Text = _settings.MaxOutput.ToString(CultureInfo.InvariantCulture);
            TxtManualSpeed.Text = _settings.ManualOutput.ToString(CultureInfo.InvariantCulture);
            ChkAutoMode.IsChecked = _settings.IsAutoMode;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (!float.TryParse(TxtTargetTemp.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float targetTemp) ||
                !float.TryParse(TxtKp.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float kp) ||
                !float.TryParse(TxtKi.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float ki) ||
                !float.TryParse(TxtKd.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float kd) ||
                !float.TryParse(TxtMinOutput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float minOut) ||
                !float.TryParse(TxtMaxOutput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float maxOut) ||
                !float.TryParse(TxtManualSpeed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float manualSpeed))
            {
                MessageBox.Show("Please enter valid numeric values.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.TargetTemperature = targetTemp;
            _settings.Kp = kp;
            _settings.Ki = ki;
            _settings.Kd = kd;
            _settings.MinOutput = minOut;
            _settings.MaxOutput = maxOut;
            _settings.ManualOutput = manualSpeed;
            _settings.IsAutoMode = ChkAutoMode.IsChecked ?? true;

            if (CmbSensors.SelectedItem is SensorItem selectedItem)
            {
                _settings.SelectedSensorId = selectedItem.Id;
            }

            // Обновление параметров контроллера
            _controller.Kp = _settings.Kp;
            _controller.Ki = _settings.Ki;
            _controller.Kd = _settings.Kd;
            _controller.Setpoint = _settings.TargetTemperature;
            _controller.MinOutput = _settings.MinOutput;
            _controller.MaxOutput = _settings.MaxOutput;

            if (!_settings.IsAutoMode)
            {
                _controller.SwitchToManual(_settings.ManualOutput);
            }
            else
            {
                // Bumpless transfer с сохранением текущего значения
                _controller.SwitchToAuto(_settings.TargetTemperature, _controller.LastOutput);
            }

            _settings.Save();
            MessageBox.Show("Settings applied and saved successfully.", "FanControl PID Pump", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnSensorSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CmbSensors.SelectedItem is SensorItem item)
            {
                _settings.SelectedSensorId = item.Id;
            }
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            bool isAuto = ChkAutoMode.IsChecked ?? true;
            TxtManualSpeed.IsEnabled = !isAuto;
        }

        private class SensorItem
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;

            public override string ToString() => DisplayName;
        }
    }
}

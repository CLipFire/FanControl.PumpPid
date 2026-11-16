using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using FanControl.Plugins;

namespace FanControl.PumpPid
{
    internal sealed class DiscoveredTempSensor
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsCustom { get; set; }
        public object Target { get; set; } = null!;
        public PropertyInfo ValueProp { get; set; } = null!;

        public float? ReadValue()
        {
            try
            {
                object? raw = ValueProp.GetValue(Target, null);
                if (raw == null) return null;
                if (raw is float f) return float.IsNaN(f) ? (float?)null : f;
                if (raw is double d) return double.IsNaN(d) ? (float?)null : (float)d;
                if (raw is int i) return i;
                if (float.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                    return float.IsNaN(parsed) ? (float?)null : parsed;
            }
            catch
            {
            }
            return null;
        }
    }

    /// <summary>
    /// Основной плагин PID-регулятора водяной помпы для FanControl.
    /// Автоматически обнаруживает как системные датчики (CPU, GPU, плата), так и все
    /// ПОЛЬЗОВАТЕЛЬСКИЕ датчики FanControl (Time Average, Mix, Offset, File и др.)
    /// и синхронизируется с графическим окном настройки PumpPidConfigurator.exe!
    /// </summary>
    public class PumpPidPlugin : IPlugin2
    {
        private readonly IPluginLogger? _logger;
        private readonly BumplessPidController _pid = new BumplessPidController();
        private PumpPidEventLogger? _eventLogger;
        private PumpPidConfigManager? _configManager;
        private IPluginSensorsContainer? _container;

        private readonly List<DiscoveredTempSensor> _discoveredSensors = new List<DiscoveredTempSensor>();
        private DateTime _pluginStartUtc = DateTime.UtcNow;
        private DateTime _lastDiscoveryUtc = DateTime.MinValue;
        private DateTime _lastTelemetryWriteUtc = DateTime.MinValue;
        private string _statusFilePath = string.Empty;
        private string _lastLoggedActiveSensor = string.Empty;
        private bool _initialDiscoveryCompleted;

        private PumpPidOutputSensor? _pidOutputTempProxy;
        private PumpPidInputTempBridgeSensor? _inputTempBridgeCard;

        public string Name => "Pump Bumpless PID Controller";

        public PumpPidPlugin()
        {
        }

        public PumpPidPlugin(IPluginLogger logger)
        {
            _logger = logger;
        }

        public void Initialize()
        {
            try
            {
                InitializeCore();
            }
            catch (Exception ex)
            {
                try { _logger?.Log($"[PumpPID] Ошибка Initialize: {ex}"); } catch { }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InitializeCore()
        {
            _pluginStartUtc = DateTime.UtcNow;
            _initialDiscoveryCompleted = false;

            string assemblyLocation = Assembly.GetExecutingAssembly().Location;
            string assemblyDir = !string.IsNullOrEmpty(assemblyLocation)
                ? (Path.GetDirectoryName(assemblyLocation) ?? AppDomain.CurrentDomain.BaseDirectory)
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");

            string configPath = Path.Combine(assemblyDir, "PumpPidConfig.json");
            string logPath = Path.Combine(assemblyDir, "PumpPidEvents.log");
            _statusFilePath = Path.Combine(assemblyDir, "PumpPidLiveStatus.json");

            _eventLogger = new PumpPidEventLogger(_logger!, logPath, enableFileLogging: true);
            _configManager = new PumpPidConfigManager(configPath, _eventLogger);
            _configManager.Initialize();

            var cfg = _configManager.Current;
            float startOutput = BumplessPidController.Clamp(cfg.ManualOutputPercent, cfg.MinOutputPercent, cfg.MaxOutputPercent);
            _pid.InitializeState(startOutput, cfg);

            _eventLogger.LogEvent(
                PidEventCategory.SettlingTime,
                $"Плагин инициализирован. Целевой датчик: '{cfg.SourceSensorId}', Диапазон=[{cfg.MinOutputPercent}..{cfg.MaxOutputPercent}%]");
        }

        public void Load(IPluginSensorsContainer _container)
        {
            try
            {
                LoadCore(_container);
            }
            catch (Exception ex)
            {
                try { _logger?.Log($"[PumpPID] Ошибка Load: {ex}"); } catch { }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LoadCore(IPluginSensorsContainer _container)
        {
            this._container = _container;
            _initialDiscoveryCompleted = false;
            var cfg = _configManager?.Current ?? new PumpPidConfig();
            float initialOut = BumplessPidController.Clamp(cfg.MinOutputPercent, cfg.MinOutputPercent, cfg.MaxOutputPercent);

            // 1. Единственный выходной сигнал PID-регулятора в TempSensors (выбирается как "Источник температуры" в линейной кривой 0..100°C -> 0..100%)
            _pidOutputTempProxy = new PumpPidOutputSensor(
                "pump_pid_output_signal",
                "Pump PID Output (%)",
                initialOut);
            _container.TempSensors.Add(_pidOutputTempProxy);

            // 2. Вспомогательная карточка-мост входа температуры создается ТОЛЬКО если явно включен флаг AllowInputTempBridgeCard = true в PumpPidConfig.json
            if (cfg.AllowInputTempBridgeCard)
            {
                _inputTempBridgeCard = new PumpPidInputTempBridgeSensor(
                    "pump_pid_input_temp_bridge",
                    "PID: Вход температуры (кривая 0-100)");
                _container.ControlSensors.Add(_inputTempBridgeCard);
            }
            else
            {
                _inputTempBridgeCard = null;
            }
        }

        public void Update()
        {
            try
            {
                UpdateCore();
            }
            catch (Exception ex)
            {
                try { _logger?.Log($"[PumpPID] Ошибка Update: {ex.Message}"); } catch { }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateCore()
        {
            if (_container == null || _configManager == null)
                return;

            DateTime nowUtc = DateTime.UtcNow;

            // Проверяем, не сохранил ли пользователь новые настройки в окне PumpPidConfigurator.exe
            if ((nowUtc - _lastTelemetryWriteUtc).TotalSeconds >= 0.45)
            {
                _configManager.CheckForExternalUpdates();
            }

            // Сканируем память ТОЛЬКО при старте (пока датчики еще не загружены), чтобы вообще не нагружать FanControl!
            if (!_initialDiscoveryCompleted && (nowUtc - _lastDiscoveryUtc).TotalSeconds >= 3.0)
            {
                _lastDiscoveryUtc = nowUtc;
                DiscoverInternalFanControlSensors();
                if (_discoveredSensors.Count > 5 && (nowUtc - _pluginStartUtc).TotalSeconds >= 8.0)
                {
                    _initialDiscoveryCompleted = true;
                }
            }

            var cfg = _configManager.Current;
            float actualTemp = cfg.SetpointCelsius;
            string activeSensorLabel = "Ожидание датчика (используется уставка)";

            // Приоритет 1 (только при явном флаге AllowInputTempBridgeCard = true в конфиге): чтение через вспомогательную карточку-мост
            if (cfg.AllowInputTempBridgeCard &&
                _inputTempBridgeCard != null &&
                _inputTempBridgeCard.HasActiveInput &&
                _inputTempBridgeCard.Value.HasValue &&
                _inputTempBridgeCard.Value.Value >= 5.0f &&
                _inputTempBridgeCard.Value.Value <= 115.0f &&
                Math.Abs(_inputTempBridgeCard.Value.Value - (_pidOutputTempProxy?.Value ?? -999f)) > 0.5f)
            {
                actualTemp = _inputTempBridgeCard.Value.Value;
                activeSensorLabel = "Карточка FanControl: PID Вход температуры";
            }
            else
            {
                // Приоритет 2 (Основной): Прямое чтение выбранного датчика (пользовательского или системного) из памяти FanControl
                DiscoveredTempSensor? matched = FindBestMatchingSensor(cfg.SourceSensorId);
                float? sensorVal = matched?.ReadValue();
                if (matched != null && sensorVal.HasValue)
                {
                    actualTemp = sensorVal.Value;
                    string prefix = matched.IsCustom ? "[Пользовательский] " : "";
                    activeSensorLabel = $"{prefix}{matched.Name} ({matched.Id})";

                    if (!string.Equals(_lastLoggedActiveSensor, activeSensorLabel, StringComparison.Ordinal))
                    {
                        _lastLoggedActiveSensor = activeSensorLabel;
                        _eventLogger?.LogEvent(
                            PidEventCategory.SettlingTime,
                            $"Подключен датчик температуры: {activeSensorLabel} = {actualTemp:F1}°C");
                    }
                }
                else
                {
                    // Приоритет 3: Проверка _container.TempSensors
                    IPluginSensor? containerSensor = _container.TempSensors.FirstOrDefault(s =>
                        s != null &&
                        s != _pidOutputTempProxy &&
                        s.Value.HasValue &&
                        (string.IsNullOrEmpty(cfg.SourceSensorId) ||
                         string.Equals(s.Id, cfg.SourceSensorId, StringComparison.OrdinalIgnoreCase) ||
                         (s.Name != null && s.Name.IndexOf(cfg.SourceSensorId, StringComparison.OrdinalIgnoreCase) >= 0)));

                    if (containerSensor != null && containerSensor.Value.HasValue)
                    {
                        actualTemp = containerSensor.Value.Value;
                        activeSensorLabel = $"{containerSensor.Name} ({containerSensor.Id})";
                    }
                }
            }

            float outputPercent = _pid.Compute(actualTemp, cfg, _eventLogger);

            _pidOutputTempProxy?.SetOutput(outputPercent);

            if ((nowUtc - _lastTelemetryWriteUtc).TotalSeconds >= 0.45)
            {
                _lastTelemetryWriteUtc = nowUtc;
                WriteLiveTelemetryStatus(actualTemp, activeSensorLabel, outputPercent, cfg);
            }
        }

        private DiscoveredTempSensor? FindBestMatchingSensor(string requestedKey)
        {
            if (_discoveredSensors.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(requestedKey))
            {
                // 1. Точное совпадение по Id или Name
                var exact = _discoveredSensors.FirstOrDefault(s =>
                    (string.Equals(s.Id, requestedKey, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.Name, requestedKey, StringComparison.OrdinalIgnoreCase)) &&
                    s.ReadValue().HasValue);
                if (exact != null) return exact;

                // 2. Частичное вхождение в Name или Id
                var partial = _discoveredSensors.FirstOrDefault(s =>
                    ((s.Name != null && s.Name.IndexOf(requestedKey, StringComparison.OrdinalIgnoreCase) >= 0) ||
                     (s.Id != null && s.Id.IndexOf(requestedKey, StringComparison.OrdinalIgnoreCase) >= 0)) &&
                    s.ReadValue().HasValue);
                if (partial != null) return partial;
            }

            // 3. Сначала ищем пользовательские датчики (Time Avg, Mix, Core Average), затем системные CPU/GPU
            var customFirst = _discoveredSensors.FirstOrDefault(s => s.IsCustom && s.ReadValue().HasValue);
            if (customFirst != null) return customFirst;

            string[] priorityKeywords = new[] { "Core Average", "Time Avg", "Mix", "CPU", "/intelcpu/", "/amdcpu/", "Package", "GPU" };
            foreach (string kw in priorityKeywords)
            {
                var fallback = _discoveredSensors.FirstOrDefault(s =>
                    ((s.Name != null && s.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) ||
                     (s.Id != null && s.Id.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)) &&
                    s.ReadValue().HasValue);
                if (fallback != null) return fallback;
            }

            return _discoveredSensors.FirstOrDefault(s => s.ReadValue().HasValue);
        }

        /// <summary>
        /// Проверяет, принадлежит ли тип к визуальному дереву WPF (DispatcherObject / DependencyObject / Window).
        /// Любые такие объекты СТРОГО пропускаются при сканировании из фонового потока, чтобы никогда
        /// не блокировать UI-поток FanControl и не вызывать черное окно при разворачивании из трея!
        /// </summary>
        private static bool IsWpfUiObject(Type type)
        {
            Type? cur = type;
            while (cur != null && cur != typeof(object))
            {
                string fn = cur.FullName ?? string.Empty;
                if (fn == "System.Windows.Window" ||
                    fn == "System.Windows.Media.Visual" ||
                    fn == "System.Windows.DependencyObject" ||
                    fn == "System.Windows.Threading.DispatcherObject")
                {
                    return true;
                }
                cur = cur.BaseType;
            }
            return false;
        }

        /// <summary>
        /// Безопасный полевой (Field-Only) сканер памяти для обнаружения системных и пользовательских датчиков.
        /// НЕ вызывает геттеры свойств UI/ViewModel и НЕ трогает визуальные объекты WPF (Window / Dispatcher).
        /// </summary>
        private void DiscoverInternalFanControlSensors()
        {
            try
            {
                var foundByKey = new Dictionary<string, DiscoveredTempSensor>(StringComparer.OrdinalIgnoreCase);
                var visited = new HashSet<object>();
                var queue = new Queue<KeyValuePair<object, string>>();

                if (_container != null)
                {
                    queue.Enqueue(new KeyValuePair<object, string>(_container, "container"));
                }

                // Берем только полевые ссылки из Application.Current (НЕ трогая MainWindow/Dispatcher!) и статические поля сервисов FanControl
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = asm.GetName().Name ?? string.Empty;
                    if (asmName.StartsWith("PresentationFramework", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Type? appType = asm.GetType("System.Windows.Application", false);
                            FieldInfo? appField = appType?.GetField("_appInstance", BindingFlags.Static | BindingFlags.NonPublic);
                            object? appObj = appField?.GetValue(null);
                            if (appObj != null)
                            {
                                // Из объекта FanControl.App берем ТОЛЬКО его собственные поля (сервисы/контейнер), не трогая базовые поля WPF!
                                foreach (FieldInfo f in appObj.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                {
                                    try
                                    {
                                        object? child = f.GetValue(appObj);
                                        if (child != null && !IsWpfUiObject(child.GetType()))
                                        {
                                            queue.Enqueue(new KeyValuePair<object, string>(child, f.Name));
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                    else if (asmName.StartsWith("FanControl", StringComparison.OrdinalIgnoreCase) &&
                             !asmName.Equals("FanControl.PumpPid", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            foreach (Type t in asm.GetTypes())
                            {
                                foreach (FieldInfo sf in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                                {
                                    try
                                    {
                                        object? val = sf.GetValue(null);
                                        if (val != null && !IsWpfUiObject(val.GetType()))
                                        {
                                            queue.Enqueue(new KeyValuePair<object, string>(val, sf.Name));
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }

                int steps = 0;
                while (queue.Count > 0 && steps < 3500)
                {
                    var currentPair = queue.Dequeue();
                    object obj = currentPair.Key;
                    string contextHint = currentPair.Value;

                    if (obj == null || !visited.Add(obj))
                        continue;

                    if (ReferenceEquals(obj, _pidOutputTempProxy) ||
                        ReferenceEquals(obj, _inputTempBridgeCard) ||
                        ReferenceEquals(obj, this))
                    {
                        continue;
                    }

                    Type type = obj.GetType();
                    // СТРОГАЯ ЗАЩИТА UI: никогда не заходим внутрь визуальных объектов WPF (Window, Visual, DependencyObject, DispatcherObject)
                    if (IsWpfUiObject(type))
                        continue;

                    steps++;
                    string typeFullName = type.FullName ?? type.Name;

                    // 1. Проверяем, является ли сам объект датчиком температуры
                    TryRegisterTempSensorObject(obj, type, typeFullName, contextHint, foundByKey);

                    // 2. Если это коллекция — проверяем элементы
                    if (obj is IEnumerable enumerable && !(obj is string))
                    {
                        try
                        {
                            foreach (object? item in enumerable)
                            {
                                if (item == null) continue;
                                Type itemType = item.GetType();
                                if (itemType.IsValueType && itemType.Name.IndexOf("KeyValuePair", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    try
                                    {
                                        FieldInfo? valF = itemType.GetField("value", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                        object? kvVal = valF?.GetValue(item);
                                        if (kvVal != null && !visited.Contains(kvVal) && !IsWpfUiObject(kvVal.GetType()))
                                        {
                                            queue.Enqueue(new KeyValuePair<object, string>(kvVal, contextHint));
                                        }
                                    }
                                    catch { }
                                }
                                else if (!itemType.IsPrimitive && !(item is string) && !visited.Contains(item) && !IsWpfUiObject(itemType))
                                {
                                    queue.Enqueue(new KeyValuePair<object, string>(item, contextHint));
                                }
                            }
                        }
                        catch { }
                    }

                    // 3. Обходим ТОЛЬКО поля (FieldInfo) доменных объектов и контейнеров (НЕ вызывая геттеры свойств!)
                    if (typeFullName.StartsWith("FanControl", StringComparison.OrdinalIgnoreCase) ||
                        typeFullName.StartsWith("LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase) ||
                        typeFullName.StartsWith("SimpleInjector", StringComparison.OrdinalIgnoreCase) ||
                        typeFullName.StartsWith("Autofac", StringComparison.OrdinalIgnoreCase) ||
                        typeFullName.StartsWith("Microsoft.Extensions", StringComparison.OrdinalIgnoreCase))
                    {
                        Type? curType = type;
                        int depth = 0;
                        while (curType != null && curType != typeof(object) && depth < 4)
                        {
                            depth++;
                            FieldInfo[] fields;
                            try
                            {
                                fields = curType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            }
                            catch
                            {
                                break;
                            }

                            foreach (FieldInfo f in fields)
                            {
                                if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string))
                                    continue;

                                try
                                {
                                    object? child = f.GetValue(obj);
                                    if (child != null && !visited.Contains(child) && !IsWpfUiObject(child.GetType()))
                                    {
                                        queue.Enqueue(new KeyValuePair<object, string>(child, f.Name));
                                    }
                                }
                                catch { }
                            }
                            curType = curType.BaseType;
                        }
                    }
                }

                if (foundByKey.Count > 0)
                {
                    _discoveredSensors.Clear();
                    _discoveredSensors.AddRange(
                        foundByKey.Values
                            .OrderByDescending(s => s.IsCustom)
                            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch
            {
            }
        }

        private static PropertyInfo? FindPropertyAnywhere(Type type, string propName)
        {
            Type? cur = type;
            while (cur != null && cur != typeof(object))
            {
                PropertyInfo? p = cur.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null) return p;
                cur = cur.BaseType;
            }
            foreach (Type iface in type.GetInterfaces())
            {
                PropertyInfo? ip = iface.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
                if (ip != null) return ip;
            }
            return null;
        }

        private void TryRegisterTempSensorObject(
            object obj,
            Type type,
            string typeFullName,
            string contextHint,
            Dictionary<string, DiscoveredTempSensor> foundByKey)
        {
            try
            {
                string shortTypeName = type.Name ?? string.Empty;

                // Не трогаем ViewModel и UI-классы, работаем только с самими объектами датчиков!
                if (shortTypeName.IndexOf("ViewModel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Service", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Factory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("SensorFan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("FanSensor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("FanSpeed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("FanCurve", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Graph", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Linear", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Flat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Sync", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shortTypeName.IndexOf("Command", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                PropertyInfo? valProp = FindPropertyAnywhere(type, "Value");
                PropertyInfo? nameProp = FindPropertyAnywhere(type, "Name");
                if (valProp == null || nameProp == null)
                    return;

                Type valType = valProp.PropertyType;
                if (valType != typeof(float?) && valType != typeof(float) && valType != typeof(double) && valType != typeof(double?))
                    return;

                // Исключаем кривые управления вентиляторами
                if (FindPropertyAnywhere(type, "MinSpeed") != null ||
                    FindPropertyAnywhere(type, "MaxSpeed") != null ||
                    FindPropertyAnywhere(type, "FanSensors") != null)
                {
                    return;
                }

                bool isTemperature = false;
                bool isCustomSensor = false;

                PropertyInfo? sensorTypeProp = FindPropertyAnywhere(type, "SensorType");
                if (sensorTypeProp != null)
                {
                    string st = Convert.ToString(sensorTypeProp.GetValue(obj, null), CultureInfo.InvariantCulture) ?? string.Empty;
                    if (string.Equals(st, "Temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        isTemperature = true;
                        isCustomSensor = false;
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    bool looksLikeSensor =
                        shortTypeName.IndexOf("Temp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shortTypeName.IndexOf("Sensor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shortTypeName.IndexOf("Mix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shortTypeName.IndexOf("TimeAverage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shortTypeName.IndexOf("Average", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shortTypeName.IndexOf("Offset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shortTypeName.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shortTypeName.IndexOf("File", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        contextHint.IndexOf("Temp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        contextHint.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        contextHint.IndexOf("Mix", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!looksLikeSensor)
                        return;

                    isTemperature = true;
                    isCustomSensor = true;
                }

                if (!isTemperature)
                    return;

                string? name = Convert.ToString(nameProp.GetValue(obj, null), CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(name) ||
                    name!.IndexOf("Pump PID", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("PID:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Distance to TjMax", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("TjMax", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Resolution", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Thermal Margin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Critical", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Threshold", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                PropertyInfo? idProp =
                    FindPropertyAnywhere(type, "Id") ??
                    FindPropertyAnywhere(type, "Identifier");

                string id = idProp != null
                    ? (Convert.ToString(idProp.GetValue(obj, null), CultureInfo.InvariantCulture) ?? string.Empty)
                    : name;

                if (string.IsNullOrWhiteSpace(id))
                    id = name;

                if (id.IndexOf("/fan/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("/control/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("/load/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("/clock/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("/voltage/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("/power/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("/data/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("/factor/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("/throughput/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("pump_pid_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("tjmax", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("distance", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                if (id.StartsWith("/", StringComparison.Ordinal) ||
                    id.StartsWith("NVApi", StringComparison.OrdinalIgnoreCase) ||
                    id.StartsWith("ADL", StringComparison.OrdinalIgnoreCase) ||
                    id.StartsWith("lhm", StringComparison.OrdinalIgnoreCase))
                {
                    isCustomSensor = false;
                }

                var candidate = new DiscoveredTempSensor
                {
                    Id = id,
                    Name = name,
                    IsCustom = isCustomSensor,
                    Target = obj,
                    ValueProp = valProp
                };

                float? currentVal = candidate.ReadValue();
                if (!currentVal.HasValue || currentVal.Value < -10f || currentVal.Value > 135f)
                    return;

                // Канонизация пути датчика LibreHardwareMonitor (устранение дублей вида /lpc/nct6687d/temperature/0 и /lpc/nct6687d/0/temperature/0)
                string canonicalId = Regex.Replace(id.Trim(), @"/0(/temperature/)", "$1", RegexOptions.IgnoreCase);
                string dedupKey = $"{name.Trim()}|{canonicalId}";

                if (!foundByKey.TryGetValue(dedupKey, out DiscoveredTempSensor? existing) ||
                    (candidate.IsCustom && !existing.IsCustom) ||
                    (candidate.IsCustom == existing.IsCustom && candidate.Id.Length < existing.Id.Length))
                {
                    foundByKey[dedupKey] = candidate;
                }
            }
            catch
            {
            }
        }

        private void WriteLiveTelemetryStatus(float actualTemp, string activeSensorLabel, float outputPercent, PumpPidConfig cfg)
        {
            if (string.IsNullOrEmpty(_statusFilePath))
                return;

            try
            {
                long utcMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"UpdatedAt\": \"{DateTime.Now:HH:mm:ss.fff}\",");
                sb.AppendLine($"  \"UpdatedAtUnixMs\": {utcMs.ToString(CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"ActiveSensor\": \"{PumpPidConfigManager.EscapeString(activeSensorLabel)}\",");
                sb.AppendLine($"  \"ActualTempCelsius\": {actualTemp.ToString("F2", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"SetpointCelsius\": {cfg.SetpointCelsius.ToString("F2", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"OutputPercent\": {outputPercent.ToString("F1", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"MinOutputPercent\": {cfg.MinOutputPercent.ToString("F1", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"MaxOutputPercent\": {cfg.MaxOutputPercent.ToString("F1", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"PTerm\": {_pid.CurrentP.ToString("F2", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"ITerm\": {_pid.CurrentI.ToString("F2", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"DTerm\": {_pid.CurrentD.ToString("F2", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"IsAutoMode\": {(cfg.IsAutoMode ? "true" : "false")},");
                sb.AppendLine("  \"Sensors\": [");

                var validSensors = _discoveredSensors
                    .Select(s => new { s.Id, s.Name, s.IsCustom, Val = s.ReadValue() })
                    .Where(x => x.Val.HasValue)
                    .ToList();

                for (int i = 0; i < validSensors.Count; i++)
                {
                    var s = validSensors[i];
                    string comma = (i < validSensors.Count - 1) ? "," : "";
                    sb.AppendLine($"    {{ \"Id\": \"{PumpPidConfigManager.EscapeString(s.Id)}\", \"Name\": \"{PumpPidConfigManager.EscapeString(s.Name)}\", \"Value\": {s.Val!.Value.ToString("F1", CultureInfo.InvariantCulture)}, \"Custom\": {(s.IsCustom ? "true" : "false")} }}{comma}");
                }

                sb.AppendLine("  ]");
                sb.AppendLine("}");

                File.WriteAllText(_statusFilePath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        public void Close()
        {
            try
            {
                CloseCore();
            }
            catch
            {
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CloseCore()
        {
            _eventLogger?.LogEvent(PidEventCategory.SettlingTime, "Плагин выгружен (Close).");
            _configManager?.Dispose();
            _configManager = null;
            _discoveredSensors.Clear();
            _container = null;
        }
    }
}
using System;

namespace FanControl.PumpPid
{
    /// <summary>
    /// Ядро PID-регулятора для водяной помпы с безударным переходом (Bumpless Transfer),
    /// защитой от интегрального насыщения (Dynamic Anti-Windup), жестким ограничением выхода,
    /// анти-дребезгом логов насыщения и автоматическим замером времени выхода температуры на уставку.
    /// </summary>
    public sealed class BumplessPidController
    {
        private enum ClampState
        {
            None,
            Min,
            Max
        }

        private float _integralState;
        private float? _prevTemp;
        private float _filteredDerivative;
        private string _lastSensorId = string.Empty;
        private bool _wasAutoMode = true;
        private float _lastKp;
        private float _lastSetpoint;
        private float _lastOutput;
        private DateTime _lastTickUtc = DateTime.UtcNow;

        private ClampState _candidateClampState = ClampState.None;
        private DateTime _candidateClampSinceUtc = DateTime.UtcNow;
        private ClampState _loggedClampState = ClampState.None;
        private DateTime? _loggedClampEnteredUtc;
        private DateTime _lastClampLogUtc = DateTime.MinValue;

        private bool _isSettled = true;
        private DateTime? _transitionStartUtc;
        private float _transitionStartTemp;
        private float _inBandStreakSeconds;
        private float _peakAbsError;

        public float CurrentP { get; private set; }
        public float CurrentI => _integralState;
        public float CurrentD { get; private set; }
        public float LastOutput => _lastOutput;

        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public void InitializeState(float initialOutputPercent, PumpPidConfig config)
        {
            _integralState = initialOutputPercent;
            _lastOutput = initialOutputPercent;
            _lastKp = config.Kp;
            _lastSetpoint = config.SetpointCelsius;
            _lastSensorId = config.SourceSensorId ?? string.Empty;
            _wasAutoMode = config.IsAutoMode;
            _prevTemp = null;
            _filteredDerivative = 0f;
            _lastTickUtc = DateTime.UtcNow;
            _candidateClampState = ClampState.None;
            _loggedClampState = ClampState.None;
            _loggedClampEnteredUtc = null;
            _isSettled = true;
        }

        public float Compute(float actualTemp, PumpPidConfig config, PumpPidEventLogger? eventLogger = null)
        {
            DateTime now = DateTime.UtcNow;
            float dt = (float)(now - _lastTickUtc).TotalSeconds;
            _lastTickUtc = now;

            if (dt <= 0.01f || dt > 10.0f)
            {
                dt = 1.0f;
            }

            float minOut = Clamp(config.MinOutputPercent, 0f, 100f);
            float maxOut = Clamp(config.MaxOutputPercent, minOut, 100f);
            float band = Math.Max(0.1f, config.SettlingBandCelsius);

            float error = actualTemp - config.SetpointCelsius;
            float absError = Math.Abs(error);
            float pTerm = config.Kp * error;

            // Сброс дифференциала при смене целевого датчика или нефизичном скачке (> 15°C за 1 тик)
            string currentSensorId = config.SourceSensorId ?? string.Empty;
            bool sensorChanged = !string.Equals(_lastSensorId, currentSensorId, StringComparison.OrdinalIgnoreCase);
            if (sensorChanged)
            {
                _lastSensorId = currentSensorId;
                _prevTemp = actualTemp;
                _filteredDerivative = 0f;
            }

            float rawDerivative = 0f;
            if (_prevTemp.HasValue)
            {
                float deltaTemp = actualTemp - _prevTemp.Value;
                if (Math.Abs(deltaTemp) > 15.0f)
                {
                    _filteredDerivative = 0f;
                    rawDerivative = 0f;
                }
                else
                {
                    rawDerivative = Clamp(deltaTemp / dt, -10.0f, 10.0f);
                }
            }
            _prevTemp = actualTemp;

            float tau = Math.Max(0f, config.DerivativeFilterTau);
            float alpha = dt / (tau + dt);
            _filteredDerivative += alpha * (rawDerivative - _filteredDerivative);
            float dTerm = config.Kd * _filteredDerivative;

            CurrentP = pTerm;
            CurrentD = dTerm;

            TrackSettlingTime(now, dt, actualTemp, error, absError, band, config, eventLogger);

            // 1. РУЧНОЙ РЕЖИМ
            if (!config.IsAutoMode)
            {
                float manualClamped = Clamp(config.ManualOutputPercent, minOut, maxOut);
                _integralState = manualClamped - pTerm - dTerm;

                if (_wasAutoMode)
                {
                    eventLogger?.LogEvent(
                        PidEventCategory.BumplessMode,
                        $"Переключение AUTO -> MANUAL ({manualClamped:F1}%). Интегратор переведен в режим слежения: I_state={_integralState:F1}%, P={pTerm:F1}%, D={dTerm:F1}%.");
                }

                _wasAutoMode = false;
                _lastKp = config.Kp;
                _lastOutput = manualClamped;
                return manualClamped;
            }

            // 2. АВТОМАТИЧЕСКИЙ РЕЖИМ (PID) — Истинный безударный переход (пропуск шага интегрирования на тике ресинхронизации)
            bool skipIntegralStep = false;

            if (!_wasAutoMode)
            {
                float holdOutput = Clamp(_lastOutput, minOut, maxOut);
                _integralState = holdOutput - pTerm - dTerm;
                _wasAutoMode = true;
                _lastKp = config.Kp;
                skipIntegralStep = true;

                eventLogger?.LogEvent(
                    PidEventCategory.BumplessMode,
                    $"Безударный переход MANUAL -> AUTO на точке {holdOutput:F1}%. Предзаряд I_state={_integralState:F1}% (P={pTerm:F1}%, D={dTerm:F1}%). Скачок: 0.0%.");
            }
            else if (Math.Abs(_lastKp - config.Kp) > 0.0001f)
            {
                float holdOutput = Clamp(_lastOutput, minOut, maxOut);
                _integralState = holdOutput - pTerm - dTerm;
                skipIntegralStep = true;

                eventLogger?.LogEvent(
                    PidEventCategory.BumplessMode,
                    $"Безударная коррекция интегратора при смене Kp ({_lastKp:F2} -> {config.Kp:F2}). Удержан выход U_out={holdOutput:F1}%, новый I_state={_integralState:F1}%. Скачок: 0.0%.");

                _lastKp = config.Kp;
            }

            if (!skipIntegralStep)
            {
                _integralState += config.Ki * error * dt;
            }

            // Динамический Anti-Windup
            float minIntegral = minOut - pTerm - dTerm;
            float maxIntegral = maxOut - pTerm - dTerm;
            if (minIntegral <= maxIntegral)
            {
                _integralState = Clamp(_integralState, minIntegral, maxIntegral);
            }

            float rawOutput = pTerm + _integralState + dTerm;
            float clampedOutput = Clamp(rawOutput, minOut, maxOut);

            TrackRangeSaturation(now, rawOutput, clampedOutput, minOut, maxOut, actualTemp, eventLogger);

            _lastOutput = clampedOutput;
            return clampedOutput;
        }

        private void TrackSettlingTime(
            DateTime now,
            float dt,
            float actualTemp,
            float error,
            float absError,
            float band,
            PumpPidConfig config,
            PumpPidEventLogger? eventLogger)
        {
            if (Math.Abs(_lastSetpoint - config.SetpointCelsius) > 0.01f)
            {
                eventLogger?.LogEvent(
                    PidEventCategory.SettlingTime,
                    $"Уставка изменена: {_lastSetpoint:F1}°C -> {config.SetpointCelsius:F1}°C (текущая T={actualTemp:F2}°C, e={error:+0.00;-0.00}°C). Запущен отсчет времени выхода на уставку.");

                _lastSetpoint = config.SetpointCelsius;
                _transitionStartUtc = now;
                _transitionStartTemp = actualTemp;
                _inBandStreakSeconds = 0f;
                _peakAbsError = absError;
                _isSettled = false;
            }
            else if (_isSettled && absError > band * 1.4f)
            {
                _isSettled = false;
                _transitionStartUtc = now;
                _transitionStartTemp = actualTemp;
                _inBandStreakSeconds = 0f;
                _peakAbsError = absError;

                eventLogger?.LogEvent(
                    PidEventCategory.SettlingTime,
                    $"Температура вышла из коридора уставки (±{band:F2}°C): T={actualTemp:F2}°C при Setpoint={config.SetpointCelsius:F1}°C. Запущен секундомер стабилизации.");
            }

            if (!_isSettled)
            {
                _peakAbsError = Math.Max(_peakAbsError, absError);
                if (absError <= band)
                {
                    _inBandStreakSeconds += dt;
                    if (_inBandStreakSeconds >= 3.0f && _transitionStartUtc.HasValue)
                    {
                        double elapsedSec = Math.Max(1.0, (now - _transitionStartUtc.Value).TotalSeconds);
                        float deltaT = Math.Abs(actualTemp - _transitionStartTemp);
                        double speedDegPerSec = deltaT / elapsedSec;
                        _isSettled = true;
                        _transitionStartUtc = null;

                        eventLogger?.LogEvent(
                            PidEventCategory.SettlingTime,
                            $"Выход на уставку {config.SetpointCelsius:F1}°C завершен за {elapsedSec:F1} с (ΔT={deltaT:F2}°C, скорость={speedDegPerSec:F3} °C/с, пик ошибки={_peakAbsError:F2}°C).");
                    }
                }
                else
                {
                    _inBandStreakSeconds = 0f;
                }
            }
        }

        private void TrackRangeSaturation(
            DateTime now,
            float rawOutput,
            float clampedOutput,
            float minOut,
            float maxOut,
            float actualTemp,
            PumpPidEventLogger? eventLogger)
        {
            ClampState nextState = ClampState.None;
            if (clampedOutput <= minOut + 0.05f && rawOutput <= minOut + 0.05f)
            {
                nextState = ClampState.Min;
            }
            else if (clampedOutput >= maxOut - 0.05f && rawOutput >= maxOut - 0.05f)
            {
                nextState = ClampState.Max;
            }

            // Отслеживаем длительность непрерывного пребывания в состоянии nextState (Debounce от лог-флуда)
            if (nextState != _candidateClampState)
            {
                _candidateClampState = nextState;
                _candidateClampSinceUtc = now;
                return;
            }

            if (nextState == _loggedClampState)
                return;

            double stateHoldSec = (now - _candidateClampSinceUtc).TotalSeconds;

            if (nextState == ClampState.Min || nextState == ClampState.Max)
            {
                // Логируем вход в насыщение только если упор держится непрерывно >= 2.5 с и прошло >= 5 с с прошлой записи
                if (stateHoldSec >= 2.5 && (now - _lastClampLogUtc).TotalSeconds >= 5.0)
                {
                    _loggedClampState = nextState;
                    _loggedClampEnteredUtc = _candidateClampSinceUtc;
                    _lastClampLogUtc = now;

                    if (nextState == ClampState.Min)
                    {
                        eventLogger?.LogEvent(
                            PidEventCategory.RangeClamp,
                            $"Достигнута нижняя граница диапазона Min ({minOut:F1}%). Сработал Anti-Windup (I_state={_integralState:F1}%, T={actualTemp:F2}°C).");
                    }
                    else
                    {
                        eventLogger?.LogEvent(
                            PidEventCategory.RangeClamp,
                            $"Достигнута верхняя граница диапазона Max ({maxOut:F1}%). Сработал Anti-Windup (I_state={_integralState:F1}%, T={actualTemp:F2}°C).");
                    }
                }
            }
            else if (nextState == ClampState.None && _loggedClampState != ClampState.None)
            {
                // Логируем выход из насыщения, когда выход стабильно вернулся в рабочий диапазон >= 1.5 с
                if (stateHoldSec >= 1.5)
                {
                    double durSec = _loggedClampEnteredUtc.HasValue
                        ? Math.Max(1.0, (now - _loggedClampEnteredUtc.Value).TotalSeconds)
                        : stateHoldSec;

                    eventLogger?.LogEvent(
                        PidEventCategory.RangeClamp,
                        $"Выход из насыщения {_loggedClampState} после {durSec:F1} с на границе. Восстановлено линейное регулирование (U_out={clampedOutput:F1}%).");

                    _loggedClampState = ClampState.None;
                    _loggedClampEnteredUtc = null;
                    _lastClampLogUtc = now;
                }
            }
        }
    }
}
using System;
using System.Globalization;

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
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Switched AUTO -> MANUAL ({0:F1}%). Integrator tracking active: I_state={1:F1}%, P={2:F1}%, D={3:F1}%.",
                            manualClamped, _integralState, pTerm, dTerm));
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
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Bumpless transfer MANUAL -> AUTO at {0:F1}%. Preloaded I_state={1:F1}% (P={2:F1}%, D={3:F1}%). Output jump: 0.0%.",
                        holdOutput, _integralState, pTerm, dTerm));
            }
            else if (Math.Abs(_lastKp - config.Kp) > 0.0001f)
            {
                float holdOutput = Clamp(_lastOutput, minOut, maxOut);
                _integralState = holdOutput - pTerm - dTerm;
                skipIntegralStep = true;

                eventLogger?.LogEvent(
                    PidEventCategory.BumplessMode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Bumpless integrator resync on Kp change ({0:F2} -> {1:F2}). Held U_out={2:F1}%, new I_state={3:F1}%. Output jump: 0.0%.",
                        _lastKp, config.Kp, holdOutput, _integralState));

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
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Setpoint changed: {0:F1}°C -> {1:F1}°C (current T={2:F2}°C, error={3:+0.00;-0.00;0.00}°C). Started settling timer.",
                        _lastSetpoint, config.SetpointCelsius, actualTemp, error));

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
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Temperature left settling band (±{0:F2}°C): T={1:F2}°C at Setpoint={2:F1}°C. Started stabilization timer.",
                        band, actualTemp, config.SetpointCelsius));
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
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Settled at setpoint {0:F1}°C in {1:F1} s (dT={2:F2}°C, rate={3:F3} °C/s, peak error={4:F2}°C).",
                                config.SetpointCelsius, elapsedSec, deltaT, speedDegPerSec, _peakAbsError));
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
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Reached lower output limit Min ({0:F1}%). Anti-Windup active (I_state={1:F1}%, T={2:F2}°C).",
                                minOut, _integralState, actualTemp));
                    }
                    else
                    {
                        eventLogger?.LogEvent(
                            PidEventCategory.RangeClamp,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Reached upper output limit Max ({0:F1}%). Anti-Windup active (I_state={1:F1}%, T={2:F2}°C).",
                                maxOut, _integralState, actualTemp));
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
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Exited {0} saturation after {1:F1} s at limit. Linear PID control restored (U_out={2:F1}%).",
                            _loggedClampState, durSec, clampedOutput));

                    _loggedClampState = ClampState.None;
                    _loggedClampEnteredUtc = null;
                    _lastClampLogUtc = now;
                }
            }
        }
    }
}
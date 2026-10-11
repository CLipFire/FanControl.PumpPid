using System;

namespace FanControl.PidPump
{
    public class PidController
    {
        private float _integral;
        private float _lastMeasurement;
        private bool _isFirstRun = true;
        private float _derivativeFilter;
        private const float DerivativeFilterConstant = 0.8f; // Фильтр низких частот для производной

        public float Kp { get; set; }
        public float Ki { get; set; }
        public float Kd { get; set; }
        public float Setpoint { get; set; }
        public float MinOutput { get; set; }
        public float MaxOutput { get; set; }

        public bool IsAutoMode { get; private set; } = true;
        public float LastOutput { get; private set; }

        public PidController(float kp, float ki, float kd, float minOutput, float maxOutput, float setpoint)
        {
            Kp = kp;
            Ki = ki;
            Kd = kd;
            MinOutput = minOutput;
            MaxOutput = maxOutput;
            Setpoint = setpoint;
            LastOutput = minOutput;
        }

        /// <summary>
        /// Безударный переход в автоматический режим.
        /// Инициализирует интегральную составляющую так, чтобы выходное значение не прыгало при переключении.
        /// </summary>
        public void SwitchToAuto(float currentMeasurement, float currentOutput)
        {
            IsAutoMode = true;
            _lastMeasurement = currentMeasurement;
            _derivativeFilter = 0f;

            float error = currentMeasurement - Setpoint;
            // Bumpless transfer: I = Output - P
            _integral = Math.Clamp(currentOutput - (Kp * error), MinOutput, MaxOutput);
            LastOutput = Math.Clamp(currentOutput, MinOutput, MaxOutput);
        }

        /// <summary>
        /// Переход в ручной режим без скачка.
        /// </summary>
        public void SwitchToManual(float manualOutput)
        {
            IsAutoMode = false;
            LastOutput = Math.Clamp(manualOutput, MinOutput, MaxOutput);
        }

        /// <summary>
        /// Вычисление очередного управляющего воздействия ПИД.
        /// </summary>
        /// <param name="measurement">Текущее измеренное значение температуры</param>
        /// <param name="deltaTime">Прошедшее время в секундах с прошлого шага</param>
        /// <returns>Процент мощности помпы (MinOutput .. MaxOutput)</returns>
        public float Compute(float measurement, float deltaTime)
        {
            if (!IsAutoMode)
            {
                return LastOutput;
            }

            if (deltaTime <= 0.0001f)
            {
                deltaTime = 1.0f;
            }

            if (_isFirstRun)
            {
                _lastMeasurement = measurement;
                _isFirstRun = false;
            }

            // Ошибка: если температура выше уставки, требуется больше мощности охлаждения (Direct Acting для помпы)
            float error = measurement - Setpoint;

            // Интегральная часть
            _integral += Ki * error * deltaTime;
            // Anti-windup (зажим интегральной составляющей)
            _integral = Math.Clamp(_integral, MinOutput, MaxOutput);

            // Derivative on Measurement: производная по изменению показаний, а не по ошибке.
            // Предотвращает скачок (derivative kick) при смене уставки (Setpoint).
            float deltaMeasurement = (measurement - _lastMeasurement) / deltaTime;
            _derivativeFilter = (DerivativeFilterConstant * _derivativeFilter) + ((1.0f - DerivativeFilterConstant) * deltaMeasurement);

            float pTerm = Kp * error;
            float iTerm = _integral;
            float dTerm = Kd * _derivativeFilter;

            float output = pTerm + iTerm + dTerm;

            // Зажим выхода в допустимые пределы
            output = Math.Clamp(output, MinOutput, MaxOutput);

            _lastMeasurement = measurement;
            LastOutput = output;

            return output;
        }

        public void Reset(float currentOutput)
        {
            _integral = Math.Clamp(currentOutput, MinOutput, MaxOutput);
            _isFirstRun = true;
            _derivativeFilter = 0f;
            LastOutput = _integral;
        }
    }
}

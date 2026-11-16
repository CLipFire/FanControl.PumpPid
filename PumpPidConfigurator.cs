using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FanControl.PumpPid.Configurator
{
    public sealed class SensorChoiceItem
    {
        public static bool IsEnglishUi;

        public string Id { get; set; }
        public string Name { get; set; }
        public float Value { get; set; }
        public bool IsCustom { get; set; }

        public override string ToString()
        {
            string tag = IsCustom
                ? (IsEnglishUi ? "★ [Custom] " : "★ [Пользовательский] ")
                : (IsEnglishUi ? "[System] " : "[Системный] ");
            if (string.IsNullOrEmpty(Name)) return tag + (Id ?? "");
            if (string.Equals(Name, Id, StringComparison.OrdinalIgnoreCase))
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}{1}  [{2:F1} °C]", tag, Name, Value);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}  [{2:F1} °C]  ({3})", tag, Name, Value, Id);
        }
    }

    public sealed class TelemetryPoint
    {
        public double TimeSec { get; set; }
        public float ActualTemp { get; set; }
        public float SetpointTemp { get; set; }
        public float OutputPercent { get; set; }
        public float IntegralPercent { get; set; }
        public float MinLimit { get; set; }
        public float MaxLimit { get; set; }
    }

    /// <summary>
    /// Двухканальный аппаратный осциллограф GDI+ с двойной буферизацией (DoubleBuffered),
    /// вертикальной сеткой секунд (5с / 10с), поддержкой RU/EN локализации и интерактивным
    /// выделением интервала мышью [t1..t2] для расчета Δt (с), ΔT (°C), °C/с, ΔU (%) и %/с.
    /// </summary>
    public sealed class PidOscilloscopeControl : Control
    {
        private readonly List<TelemetryPoint> _points = new List<TelemetryPoint>();
        private int _gridStepSeconds = 5;
        private bool _isEnglish;
        private bool _isDragging;
        private int _dragStartIndex = -1;
        private int _dragEndIndex = -1;

        public event Action<string> OnMeasurementChanged;

        public int GridStepSeconds
        {
            get { return _gridStepSeconds; }
            set
            {
                _gridStepSeconds = (value <= 5) ? 5 : 10;
                Invalidate();
            }
        }

        public bool IsEnglish
        {
            get { return _isEnglish; }
            set
            {
                _isEnglish = value;
                UpdateMeasurementText();
                Invalidate();
            }
        }

        public bool HasSelection
        {
            get { return _dragStartIndex >= 0 && _dragEndIndex >= 0 && _dragStartIndex != _dragEndIndex; }
        }

        public PidOscilloscopeControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(9, 14, 26);
            Cursor = Cursors.Cross;
        }

        public string GetDefaultMeasureHint()
        {
            return _isEnglish
                ? "Hold Left Mouse Button on chart and drag horizontally to measure Δt (s) and rate (°C/s, %/s)"
                : "Зажмите левую кнопку мыши на графике и протяните по горизонтали для замера времени Δt (с) и скорости (°C/с, %/с)";
        }

        public void AddSample(TelemetryPoint pt)
        {
            _points.Add(pt);
            if (_points.Count > 90)
            {
                _points.RemoveAt(0);
                if (_dragStartIndex > 0) _dragStartIndex--;
                if (_dragEndIndex > 0) _dragEndIndex--;
            }
            Invalidate();
        }

        public void ClearHistory()
        {
            _points.Clear();
            ClearSelection();
            Invalidate();
        }

        public void ClearSelection()
        {
            _isDragging = false;
            _dragStartIndex = -1;
            _dragEndIndex = -1;
            if (OnMeasurementChanged != null)
            {
                OnMeasurementChanged(GetDefaultMeasureHint());
            }
            Invalidate();
        }

        private Rectangle GetPlotArea()
        {
            int left = 52;
            int right = Math.Max(left + 100, Width - 18);
            int top = 26;
            int bottom = Math.Max(top + 120, Height - 26);
            return new Rectangle(left, top, right - left, bottom - top);
        }

        private int XToSampleIndex(int mouseX)
        {
            if (_points.Count < 2) return -1;
            Rectangle area = GetPlotArea();
            float ratio = (float)(mouseX - area.Left) / Math.Max(1f, (float)area.Width);
            if (ratio < 0f) ratio = 0f;
            if (ratio > 1f) ratio = 1f;
            int idx = (int)Math.Round(ratio * (_points.Count - 1));
            if (idx < 0) idx = 0;
            if (idx >= _points.Count) idx = _points.Count - 1;
            return idx;
        }

        private float IndexToX(int idx, Rectangle area)
        {
            if (_points.Count <= 1) return area.Left;
            return area.Left + ((float)idx / (_points.Count - 1)) * area.Width;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && _points.Count >= 2)
            {
                _isDragging = true;
                _dragStartIndex = XToSampleIndex(e.X);
                _dragEndIndex = _dragStartIndex;
                UpdateMeasurementText();
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                ClearSelection();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging && _points.Count >= 2)
            {
                _dragEndIndex = XToSampleIndex(e.X);
                UpdateMeasurementText();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_isDragging)
            {
                _isDragging = false;
                UpdateMeasurementText();
                Invalidate();
            }
        }

        public void UpdateMeasurementText()
        {
            if (OnMeasurementChanged == null) return;
            if (!HasSelection || _points.Count < 2)
            {
                OnMeasurementChanged(GetDefaultMeasureHint());
                return;
            }

            int i1 = Math.Min(_dragStartIndex, _dragEndIndex);
            int i2 = Math.Max(_dragStartIndex, _dragEndIndex);
            if (i1 < 0 || i2 >= _points.Count || i1 == i2) return;

            TelemetryPoint p1 = _points[i1];
            TelemetryPoint p2 = _points[i2];

            double dt = Math.Max(0.1, p2.TimeSec - p1.TimeSec);
            float dT = p2.ActualTemp - p1.ActualTemp;
            double rateT = dT / dt;
            float dU = p2.OutputPercent - p1.OutputPercent;
            double rateU = dU / dt;

            string fmt = _isEnglish
                ? "INTERVAL: Δt = {0:F1} s  |  ΔT = {1:+0.00;-0.00;0.00}°C ({2:F1}->{3:F1}°C, rate {4:+0.000;-0.000;0.000} °C/s)  |  ΔU = {5:+0.0;-0.0;0.0}% ({6:F1}->{7:F1}%, rate {8:+0.00;-0.00;0.00} %/s)"
                : "ЗАМЕР ИНТЕРВАЛА: Δt = {0:F1} с  |  ΔT = {1:+0.00;-0.00;0.00}°C ({2:F1}->{3:F1}°C, скор. {4:+0.000;-0.000;0.000} °C/с)  |  ΔU = {5:+0.0;-0.0;0.0}% ({6:F1}->{7:F1}%, скор. {8:+0.00;-0.00;0.00} %/с)";

            string msg = string.Format(
                CultureInfo.InvariantCulture,
                fmt,
                dt, dT, p1.ActualTemp, p2.ActualTemp, rateT, dU, p1.OutputPercent, p2.OutputPercent, rateU);
            OnMeasurementChanged(msg);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            Rectangle totalArea = GetPlotArea();
            int gap = 22;
            int paneHeight = (totalArea.Height - gap) / 2;
            Rectangle topPane = new Rectangle(totalArea.Left, totalArea.Top, totalArea.Width, paneHeight);
            Rectangle botPane = new Rectangle(totalArea.Left, totalArea.Top + paneHeight + gap, totalArea.Width, paneHeight);

            using (Pen borderPen = new Pen(Color.FromArgb(30, 41, 59), 1f))
            {
                g.DrawRectangle(borderPen, topPane);
                g.DrawRectangle(borderPen, botPane);
            }

            using (Font headerFont = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (Brush skyBrush = new SolidBrush(Color.FromArgb(56, 189, 248)))
            using (Brush emeraldBrush = new SolidBrush(Color.FromArgb(52, 211, 153)))
            {
                string ch1 = _isEnglish
                    ? "CHANNEL 1: SENSOR TEMPERATURE (°C) — cyan  |  SETPOINT (°C) — yellow dashed"
                    : "КАНАЛ 1: ТЕМПЕРАТУРА ДАТЧИКА (°C) — голубая  |  УСТАВКА (°C) — желтый пунктир";
                string ch2 = _isEnglish
                    ? "CHANNEL 2: PUMP CONTROL OUTPUT (%) — green  |  LIMITS [MIN..MAX] — rose dashed"
                    : "КАНАЛ 2: УПРАВЛЯЮЩИЙ СИГНАЛ ПОМПЫ (%) — зеленая  |  ЛИМИТЫ [MIN..MAX] — розовый пунктир";

                g.DrawString(ch1, headerFont, skyBrush, topPane.Left, topPane.Top - 17);
                g.DrawString(ch2, headerFont, emeraldBrush, botPane.Left, botPane.Top - 17);
            }

            if (_points.Count < 2)
            {
                using (Font waitFont = new Font("Segoe UI", 9.5f, FontStyle.Regular))
                using (Brush grayBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                {
                    string waitMsg = _isEnglish
                        ? "Collecting live telemetry samples from FanControl (waiting 2+ seconds)..."
                        : "Накопление точек телеметрии из FanControl (ожидание 2+ секунд)...";
                    g.DrawString(waitMsg, waitFont, grayBrush, topPane.Left + 20, topPane.Top + topPane.Height / 2 - 10);
                }
                return;
            }

            float minTemp = 1000f;
            float maxTemp = -1000f;
            for (int i = 0; i < _points.Count; i++)
            {
                TelemetryPoint pt = _points[i];
                if (pt.ActualTemp < minTemp) minTemp = pt.ActualTemp;
                if (pt.ActualTemp > maxTemp) maxTemp = pt.ActualTemp;
                if (pt.SetpointTemp < minTemp) minTemp = pt.SetpointTemp;
                if (pt.SetpointTemp > maxTemp) maxTemp = pt.SetpointTemp;
            }
            float pad = Math.Max(2.5f, (maxTemp - minTemp) * 0.25f);
            minTemp = (float)Math.Floor(minTemp - pad);
            maxTemp = (float)Math.Ceiling(maxTemp + pad);
            if (maxTemp - minTemp < 5f) maxTemp = minTemp + 5f;

            // 1. ВЕРТИКАЛЬНАЯ СЕТКА СЕКУНД
            string secUnit = _isEnglish ? "s" : "с";
            using (Pen vGridPen = new Pen(Color.FromArgb(26, 38, 58), 1f))
            using (Font axisFont = new Font("Consolas", 7.5f, FontStyle.Regular))
            using (Brush axisBrush = new SolidBrush(Color.FromArgb(100, 116, 139)))
            {
                vGridPen.DashStyle = DashStyle.Dot;
                for (int idx = 0; idx < _points.Count; idx++)
                {
                    int secInt = (int)Math.Round(_points[idx].TimeSec);
                    if (secInt % _gridStepSeconds == 0)
                    {
                        float x = IndexToX(idx, totalArea);
                        g.DrawLine(vGridPen, x, topPane.Top, x, topPane.Bottom);
                        g.DrawLine(vGridPen, x, botPane.Top, x, botPane.Bottom);
                        g.DrawString(secInt.ToString(CultureInfo.InvariantCulture) + secUnit, axisFont, axisBrush, x - 12, botPane.Bottom + 4);
                    }
                }

                for (int k = 0; k <= 4; k++)
                {
                    float frac = (float)k / 4f;
                    float y = topPane.Bottom - frac * topPane.Height;
                    float valT = minTemp + frac * (maxTemp - minTemp);
                    g.DrawLine(vGridPen, topPane.Left, y, topPane.Right, y);
                    g.DrawString(string.Format(CultureInfo.InvariantCulture, "{0:F1}°", valT), axisFont, axisBrush, 4, y - 6);
                }

                for (int k = 0; k <= 4; k++)
                {
                    float frac = (float)k / 4f;
                    float y = botPane.Bottom - frac * botPane.Height;
                    float valU = frac * 100f;
                    g.DrawLine(vGridPen, botPane.Left, y, botPane.Right, y);
                    g.DrawString(string.Format(CultureInfo.InvariantCulture, "{0:F0}%", valU), axisFont, axisBrush, 8, y - 6);
                }
            }

            // 2. ЛИНИИ ЛИМИТОВ MIN / MAX НА НИЖНЕМ ГРАФИКЕ
            TelemetryPoint latest = _points[_points.Count - 1];
            float yMinLim = botPane.Bottom - (BumplessClamp(latest.MinLimit, 0f, 100f) / 100f) * botPane.Height;
            float yMaxLim = botPane.Bottom - (BumplessClamp(latest.MaxLimit, 0f, 100f) / 100f) * botPane.Height;

            using (Brush clampZoneBrush = new SolidBrush(Color.FromArgb(22, 244, 63, 94)))
            using (Pen limitPen = new Pen(Color.FromArgb(180, 251, 113, 133), 1.2f))
            {
                limitPen.DashStyle = DashStyle.Dash;
                if (yMaxLim > botPane.Top)
                {
                    g.FillRectangle(clampZoneBrush, botPane.Left, botPane.Top, botPane.Width, yMaxLim - botPane.Top);
                }
                if (botPane.Bottom > yMinLim)
                {
                    g.FillRectangle(clampZoneBrush, botPane.Left, yMinLim, botPane.Width, botPane.Bottom - yMinLim);
                }
                g.DrawLine(limitPen, botPane.Left, yMaxLim, botPane.Right, yMaxLim);
                g.DrawLine(limitPen, botPane.Left, yMinLim, botPane.Right, yMinLim);
            }

            // 3. КРИВЫЕ ТЕМПЕРАТУРЫ, УСТАВКИ И ВЫХОДА ПОМПЫ
            PointF[] tempPts = new PointF[_points.Count];
            PointF[] spPts = new PointF[_points.Count];
            PointF[] outPts = new PointF[_points.Count];
            PointF[] iPts = new PointF[_points.Count];

            for (int i = 0; i < _points.Count; i++)
            {
                TelemetryPoint pt = _points[i];
                float x = IndexToX(i, totalArea);

                float normT = (pt.ActualTemp - minTemp) / Math.Max(0.1f, maxTemp - minTemp);
                float normSp = (pt.SetpointTemp - minTemp) / Math.Max(0.1f, maxTemp - minTemp);
                tempPts[i] = new PointF(x, topPane.Bottom - BumplessClamp(normT, 0f, 1f) * topPane.Height);
                spPts[i] = new PointF(x, topPane.Bottom - BumplessClamp(normSp, 0f, 1f) * topPane.Height);

                float normOut = BumplessClamp(pt.OutputPercent, 0f, 100f) / 100f;
                float normI = BumplessClamp(pt.IntegralPercent, 0f, 100f) / 100f;
                outPts[i] = new PointF(x, botPane.Bottom - normOut * botPane.Height);
                iPts[i] = new PointF(x, botPane.Bottom - normI * botPane.Height);
            }

            using (Pen spPen = new Pen(Color.FromArgb(251, 191, 36), 1.6f))
            using (Pen tempPen = new Pen(Color.FromArgb(56, 189, 248), 2.2f))
            using (Pen iPen = new Pen(Color.FromArgb(150, 167, 139, 250), 1.3f))
            using (Pen outPen = new Pen(Color.FromArgb(52, 211, 153), 2.2f))
            {
                spPen.DashStyle = DashStyle.Dash;
                iPen.DashStyle = DashStyle.Dot;

                g.DrawLines(spPen, spPts);
                g.DrawLines(tempPen, tempPts);
                g.DrawLines(iPen, iPts);
                g.DrawLines(outPen, outPts);
            }

            // 4. ВЫДЕЛЕННЫЙ МЫШЬЮ ИНТЕРВАЛ ЗАМЕРА [t1 .. t2]
            if (HasSelection)
            {
                int i1 = Math.Min(_dragStartIndex, _dragEndIndex);
                int i2 = Math.Max(_dragStartIndex, _dragEndIndex);
                float x1 = IndexToX(i1, totalArea);
                float x2 = IndexToX(i2, totalArea);
                float selW = Math.Max(2f, x2 - x1);

                using (Brush selFill = new SolidBrush(Color.FromArgb(40, 56, 189, 248)))
                using (Pen selLine = new Pen(Color.FromArgb(220, 56, 189, 248), 1.5f))
                {
                    selLine.DashStyle = DashStyle.DashDot;
                    g.FillRectangle(selFill, x1, topPane.Top, selW, topPane.Height);
                    g.FillRectangle(selFill, x1, botPane.Top, selW, botPane.Height);

                    g.DrawLine(selLine, x1, topPane.Top, x1, botPane.Bottom);
                    g.DrawLine(selLine, x2, topPane.Top, x2, botPane.Bottom);
                }

                using (Brush dotBrush = new SolidBrush(Color.FromArgb(251, 191, 36)))
                {
                    g.FillEllipse(dotBrush, tempPts[i1].X - 4, tempPts[i1].Y - 4, 8, 8);
                    g.FillEllipse(dotBrush, tempPts[i2].X - 4, tempPts[i2].Y - 4, 8, 8);
                    g.FillEllipse(dotBrush, outPts[i1].X - 4, outPts[i1].Y - 4, 8, 8);
                    g.FillEllipse(dotBrush, outPts[i2].X - 4, outPts[i2].Y - 4, 8, 8);
                }
            }
        }

        private static float BumplessClamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }

    public sealed class MainForm : Form
    {
        private string _pluginsDir;
        private string _configPath;
        private string _statusPath;
        private string _logPath;

        private bool _isEnglish;
        private Button _btnLangRu;
        private Button _btnLangEn;

        private Label _lblSec1Title;
        private Label _lblSensorSub;
        private Button _btnRefreshSensors;
        private Label _lblSensorFilter;

        private Label _lblSec2Title;
        private Label _lblSetpointTitle;
        private Label _lblMinOutTitle;
        private Label _lblMaxOutTitle;

        private Label _lblSec3Title;
        private Label _lblKpTitle;
        private Label _lblKiTitle;
        private Label _lblKdTitle;
        private Label _lblTauTitle;

        private Button _btnApply;
        private Label _lblTelHeader;
        private Label _lblOscHeader;
        private Button _btnClearSel;

        private Label _lblLiveTemp;
        private Label _lblLiveOutput;
        private Label _lblLiveSensorName;
        private Label _lblLivePidTerms;
        private Panel _barOutputFill;
        private Panel _barOutputTrack;

        private ComboBox _cmbSensors;
        private TextBox _txtCustomSensor;
        private NumericUpDown _numSetpoint;
        private TrackBar _trkSetpoint;

        private NumericUpDown _numMinOut;
        private NumericUpDown _numMaxOut;

        private NumericUpDown _numKp;
        private NumericUpDown _numKi;
        private NumericUpDown _numKd;
        private NumericUpDown _numTau;

        private RadioButton _rbAuto;
        private RadioButton _rbManual;
        private NumericUpDown _numManualOut;

        private PidOscilloscopeControl _oscilloscope;
        private Label _lblMeasureBanner;
        private Button _btnPauseChart;
        private Button _btnGridStep;
        private bool _chartPaused;
        private string _lastStatusTimestamp = string.Empty;
        private long _firstSampleUnixMs;
        private long _lastSampleUnixMs;
        private double _sampleCounterSec;
        private float _lastOutPct;
        private string _lastActiveSensor = string.Empty;
        private string _lastUpdatedAt = string.Empty;
        private string _lastSavedTimeStr = string.Empty;
        private bool _allowInputTempBridgeCard;
        private DateTime _lastComboRefreshUtc = DateTime.MinValue;
        private long _lastLogFileLength = -1;
        private DateTime _lastLogWriteUtc = DateTime.MinValue;

        private TextBox _txtLog;
        private Label _lblSaveStatus;
        private Timer _pollTimer;
        private bool _suppressEvents;
        private int _lastSensorCount = -1;

        public MainForm(string pluginsDir)
        {
            SetPluginsDirectory(pluginsDir);

            Text = "Настройка PID-регулятора помпы и Осциллограф — FanControl";
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(1240, 770);
            MinimumSize = new Size(1160, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            BuildUi();
            LoadConfigIntoUi();
            ApplyLanguage(_isEnglish, false);
            PollLiveStatus();

            _pollTimer = new Timer();
            _pollTimer.Interval = 650;
            _pollTimer.Tick += delegate { PollLiveStatus(); };
            _pollTimer.Start();
        }

        private void BuildUi()
        {
            int leftW = 500;
            int rightX = 524;
            int rightW = 684;

            // =========================================================================
            // ЛЕВАЯ КОЛОНКА: НАСТРОЙКИ ДАТЧИКА, УСТАВКИ, ДИАПАЗОНА MIN..MAX И КОЭФФИЦИЕНТОВ
            // =========================================================================
            int yLeft = 14;

            // 1. ВЫБОР ДАТЧИКА ТЕМПЕРАТУРЫ
            Panel pnlSensor = CreateCardPanel(14, yLeft, leftW, 128);
            _lblSec1Title = CreateLabel("1. ВЫБОР ДАТЧИКА ТЕМПЕРАТУРЫ (ИСТОЧНИК)", 12, 10, 460, 18,
                new Font("Segoe UI", 9f, FontStyle.Bold), Color.FromArgb(56, 189, 248));
            pnlSensor.Controls.Add(_lblSec1Title);

            _lblSensorSub = CreateLabel("Датчики, обнаруженные в запущенном FanControl:", 12, 32, 340, 18,
                Font, Color.FromArgb(203, 213, 225));
            pnlSensor.Controls.Add(_lblSensorSub);

            _btnRefreshSensors = new Button();
            _btnRefreshSensors.Text = "Обновить список";
            _btnRefreshSensors.Location = new Point(358, 28);
            _btnRefreshSensors.Size = new Size(128, 24);
            _btnRefreshSensors.FlatStyle = FlatStyle.Flat;
            _btnRefreshSensors.BackColor = Color.FromArgb(51, 65, 85);
            _btnRefreshSensors.ForeColor = Color.White;
            _btnRefreshSensors.Click += delegate { _lastSensorCount = -1; PollLiveStatus(); };
            pnlSensor.Controls.Add(_btnRefreshSensors);
            _btnRefreshSensors.BringToFront();

            _cmbSensors = new ComboBox();
            _cmbSensors.Location = new Point(12, 58);
            _cmbSensors.Size = new Size(474, 26);
            _cmbSensors.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSensors.BackColor = Color.FromArgb(15, 23, 42);
            _cmbSensors.ForeColor = Color.White;
            _cmbSensors.SelectedIndexChanged += OnSensorComboSelected;
            pnlSensor.Controls.Add(_cmbSensors);

            _lblSensorFilter = CreateLabel("Фильтр / ID датчика:", 12, 94, 130, 18,
                new Font("Segoe UI", 8.5f), Color.FromArgb(148, 163, 184));
            pnlSensor.Controls.Add(_lblSensorFilter);

            _txtCustomSensor = new TextBox();
            _txtCustomSensor.Location = new Point(144, 92);
            _txtCustomSensor.Size = new Size(342, 22);
            _txtCustomSensor.BackColor = Color.FromArgb(15, 23, 42);
            _txtCustomSensor.ForeColor = Color.FromArgb(125, 211, 252);
            _txtCustomSensor.BorderStyle = BorderStyle.FixedSingle;
            pnlSensor.Controls.Add(_txtCustomSensor);

            Controls.Add(pnlSensor);
            yLeft += 138;

            // 2. УСТАВКА ТЕМПЕРАТУРЫ (°C) И ДИАПАЗОН ОБОРОТОВ ПОМПЫ (MIN % .. MAX %)
            Panel pnlRange = CreateCardPanel(14, yLeft, leftW, 146);
            _lblSec2Title = CreateLabel("2. УСТАВКА (°C) И ДИАПАЗОН ПОМПЫ (MIN % — MAX %)", 12, 10, 470, 18,
                new Font("Segoe UI", 9f, FontStyle.Bold), Color.FromArgb(56, 189, 248));
            pnlRange.Controls.Add(_lblSec2Title);

            _lblSetpointTitle = CreateLabel("Целевая уставка (°C):", 12, 40, 150, 20, Font, Color.White);
            pnlRange.Controls.Add(_lblSetpointTitle);

            _numSetpoint = CreateNumeric(166, 37, 74, 15m, 95m, 0.5m, 1);
            _numSetpoint.ValueChanged += delegate
            {
                if (_suppressEvents) return;
                int trkVal = Math.Max(15, Math.Min(95, (int)Math.Round(_numSetpoint.Value)));
                _trkSetpoint.Value = trkVal;
                SaveConfigFromUi();
            };
            pnlRange.Controls.Add(_numSetpoint);

            _trkSetpoint = new TrackBar();
            _trkSetpoint.Location = new Point(246, 33);
            _trkSetpoint.Size = new Size(240, 34);
            _trkSetpoint.Minimum = 15;
            _trkSetpoint.Maximum = 95;
            _trkSetpoint.TickFrequency = 5;
            _trkSetpoint.ValueChanged += delegate
            {
                if (_suppressEvents) return;
                _suppressEvents = true;
                _numSetpoint.Value = _trkSetpoint.Value;
                _suppressEvents = false;
                SaveConfigFromUi();
            };
            pnlRange.Controls.Add(_trkSetpoint);

            _lblMinOutTitle = CreateLabel("Мин. обороты (Min %):", 12, 92, 155, 20, Font, Color.FromArgb(251, 191, 36));
            pnlRange.Controls.Add(_lblMinOutTitle);

            _numMinOut = CreateNumeric(168, 89, 72, 0m, 100m, 1m, 0);
            _numMinOut.ValueChanged += delegate
            {
                if (_suppressEvents) return;
                if (_numMinOut.Value > _numMaxOut.Value) _numMaxOut.Value = _numMinOut.Value;
                SaveConfigFromUi();
            };
            pnlRange.Controls.Add(_numMinOut);

            _lblMaxOutTitle = CreateLabel("Макс. обороты (Max %):", 252, 92, 160, 20, Font, Color.FromArgb(52, 211, 153));
            pnlRange.Controls.Add(_lblMaxOutTitle);

            _numMaxOut = CreateNumeric(414, 89, 72, 0m, 100m, 1m, 0);
            _numMaxOut.ValueChanged += delegate
            {
                if (_suppressEvents) return;
                if (_numMaxOut.Value < _numMinOut.Value) _numMinOut.Value = _numMaxOut.Value;
                SaveConfigFromUi();
            };
            pnlRange.Controls.Add(_numMaxOut);

            Controls.Add(pnlRange);
            yLeft += 156;

            // 3. КОЭФФИЦИЕНТЫ PID И РЕЖИМ АВТО / РУЧНОЙ
            Panel pnlPid = CreateCardPanel(14, yLeft, leftW, 164);
            _lblSec3Title = CreateLabel("3. КОЭФФИЦИЕНТЫ PID И РЕЖИМ (БЕЗУДАРНЫЙ ПЕРЕХОД)", 12, 10, 470, 18,
                new Font("Segoe UI", 9f, FontStyle.Bold), Color.FromArgb(56, 189, 248));
            pnlPid.Controls.Add(_lblSec3Title);

            _lblKpTitle = CreateLabel("Kp (Проп.):", 12, 40, 75, 20, Font, Color.White);
            pnlPid.Controls.Add(_lblKpTitle);
            _numKp = CreateNumeric(90, 37, 68, 0.05m, 50m, 0.05m, 2);
            _numKp.ValueChanged += delegate { if (!_suppressEvents) SaveConfigFromUi(); };
            pnlPid.Controls.Add(_numKp);

            _lblKiTitle = CreateLabel("Ki (Инт.):", 172, 40, 68, 20, Font, Color.White);
            pnlPid.Controls.Add(_lblKiTitle);
            _numKi = CreateNumeric(242, 37, 68, 0.0m, 20m, 0.05m, 2);
            _numKi.ValueChanged += delegate { if (!_suppressEvents) SaveConfigFromUi(); };
            pnlPid.Controls.Add(_numKi);

            _lblKdTitle = CreateLabel("Kd (Диф.):", 324, 40, 72, 20, Font, Color.White);
            pnlPid.Controls.Add(_lblKdTitle);
            _numKd = CreateNumeric(400, 37, 86, 0.0m, 30m, 0.1m, 2);
            _numKd.ValueChanged += delegate { if (!_suppressEvents) SaveConfigFromUi(); };
            pnlPid.Controls.Add(_numKd);

            _lblTauTitle = CreateLabel("Фильтр дифференциала Tau (сек):", 12, 76, 225, 20, Font, Color.FromArgb(203, 213, 225));
            pnlPid.Controls.Add(_lblTauTitle);
            _numTau = CreateNumeric(242, 73, 68, 0.1m, 20m, 0.2m, 1);
            _numTau.ValueChanged += delegate { if (!_suppressEvents) SaveConfigFromUi(); };
            pnlPid.Controls.Add(_numTau);

            _rbAuto = new RadioButton();
            _rbAuto.Text = "АВТО (PID-регулятор)";
            _rbAuto.Location = new Point(12, 114);
            _rbAuto.Size = new Size(190, 24);
            _rbAuto.ForeColor = Color.FromArgb(52, 211, 153);
            _rbAuto.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _rbAuto.CheckedChanged += delegate { if (!_suppressEvents) SaveConfigFromUi(); };
            pnlPid.Controls.Add(_rbAuto);

            _rbManual = new RadioButton();
            _rbManual.Text = "РУЧНОЙ РЕЖИМ (%):";
            _rbManual.Location = new Point(210, 114);
            _rbManual.Size = new Size(180, 24);
            _rbManual.ForeColor = Color.FromArgb(251, 191, 36);
            _rbManual.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            pnlPid.Controls.Add(_rbManual);

            _numManualOut = CreateNumeric(400, 113, 86, 0m, 100m, 1m, 1);
            _numManualOut.ValueChanged += delegate { if (!_suppressEvents) SaveConfigFromUi(); };
            pnlPid.Controls.Add(_numManualOut);

            Controls.Add(pnlPid);
            yLeft += 174;

            // Кнопка применения + Журнал событий слева внизу
            _btnApply = new Button();
            _btnApply.Text = "ПРИМЕНИТЬ НАСТРОЙКИ К FANCONTROL (МГНОВЕННО)";
            _btnApply.Location = new Point(14, yLeft);
            _btnApply.Size = new Size(leftW, 34);
            _btnApply.FlatStyle = FlatStyle.Flat;
            _btnApply.BackColor = Color.FromArgb(2, 132, 199);
            _btnApply.ForeColor = Color.White;
            _btnApply.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _btnApply.Click += delegate { SaveConfigFromUi(); };
            Controls.Add(_btnApply);
            yLeft += 38;

            _lblSaveStatus = CreateLabel("Готово к работе. Все изменения сохраняются автоматически.", 14, yLeft, leftW, 18,
                new Font("Segoe UI", 8.2f), Color.FromArgb(52, 211, 153));
            Controls.Add(_lblSaveStatus);
            yLeft += 22;

            _txtLog = new TextBox();
            _txtLog.Location = new Point(14, yLeft);
            _txtLog.Size = new Size(leftW, 170);
            _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(9, 14, 26);
            _txtLog.ForeColor = Color.FromArgb(148, 163, 184);
            _txtLog.Font = new Font("Consolas", 8.2f);
            _txtLog.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_txtLog);

            // =========================================================================
            // ПРАВАЯ КОЛОНКА: ТЕЛЕМЕТРИЯ + ПЕРЕКЛЮЧАТЕЛЬ ЯЗЫКА (RU/EN) + ОСЦИЛЛОГРАФ
            // =========================================================================
            int yRight = 14;

            Panel pnlTelemetry = CreateCardPanel(rightX, yRight, rightW, 108);
            pnlTelemetry.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _lblTelHeader = CreateLabel("ТЕЛЕМЕТРИЯ РЕАЛЬНОГО ВРЕМЕНИ ИЗ FANCONTROL", 14, 8, 420, 18,
                new Font("Segoe UI", 8.5f, FontStyle.Bold), Color.FromArgb(56, 189, 248));
            pnlTelemetry.Controls.Add(_lblTelHeader);

            // Переключатель языка RU | EN в правом верхнем углу карточки телеметрии
            Label lblLangTag = CreateLabel("Lang:", 530, 8, 42, 18,
                new Font("Segoe UI", 8.2f, FontStyle.Bold), Color.FromArgb(148, 163, 184));
            lblLangTag.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            pnlTelemetry.Controls.Add(lblLangTag);

            _btnLangRu = new Button();
            _btnLangRu.Text = "RU";
            _btnLangRu.Location = new Point(574, 5);
            _btnLangRu.Size = new Size(44, 22);
            _btnLangRu.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnLangRu.FlatStyle = FlatStyle.Flat;
            _btnLangRu.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnLangRu.Click += delegate { ApplyLanguage(false, true); };
            pnlTelemetry.Controls.Add(_btnLangRu);
            _btnLangRu.BringToFront();

            _btnLangEn = new Button();
            _btnLangEn.Text = "EN";
            _btnLangEn.Location = new Point(622, 5);
            _btnLangEn.Size = new Size(44, 22);
            _btnLangEn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnLangEn.FlatStyle = FlatStyle.Flat;
            _btnLangEn.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            _btnLangEn.Click += delegate { ApplyLanguage(true, true); };
            pnlTelemetry.Controls.Add(_btnLangEn);
            _btnLangEn.BringToFront();

            _lblLiveTemp = CreateLabel("--.- °C", 14, 28, 170, 34,
                new Font("Segoe UI", 19f, FontStyle.Bold), Color.FromArgb(251, 191, 36));
            pnlTelemetry.Controls.Add(_lblLiveTemp);

            _lblLiveOutput = CreateLabel("Помпа: --.- %", 190, 28, 250, 34,
                new Font("Segoe UI", 18f, FontStyle.Bold), Color.FromArgb(52, 211, 153));
            pnlTelemetry.Controls.Add(_lblLiveOutput);

            _lblLiveSensorName = CreateLabel("Активный датчик: ожидание данных от FanControl...", 14, 64, 650, 18,
                new Font("Segoe UI", 8.5f, FontStyle.Regular), Color.FromArgb(148, 163, 184));
            pnlTelemetry.Controls.Add(_lblLiveSensorName);

            _lblLivePidTerms = CreateLabel("PID: P = 0.0% | I = 0.0% | D = 0.0%", 14, 84, 420, 18,
                new Font("Consolas", 8.8f, FontStyle.Regular), Color.FromArgb(203, 213, 225));
            pnlTelemetry.Controls.Add(_lblLivePidTerms);

            _barOutputTrack = new Panel();
            _barOutputTrack.Location = new Point(440, 86);
            _barOutputTrack.Size = new Size(226, 12);
            _barOutputTrack.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _barOutputTrack.BackColor = Color.FromArgb(51, 65, 85);
            _barOutputFill = new Panel();
            _barOutputFill.Location = new Point(0, 0);
            _barOutputFill.Size = new Size(80, 12);
            _barOutputFill.BackColor = Color.FromArgb(16, 185, 129);
            _barOutputTrack.Controls.Add(_barOutputFill);
            pnlTelemetry.Controls.Add(_barOutputTrack);

            Controls.Add(pnlTelemetry);
            yRight += 118;

            // Панель управления осциллографом и плашка замера интервала мышью
            Panel pnlChartCard = CreateCardPanel(rightX, yRight, rightW, 580);
            pnlChartCard.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            _lblOscHeader = CreateLabel("ОСЦИЛЛОГРАФ ПЕРЕХОДНЫХ ПРОЦЕССОВ", 14, 9, 315, 20,
                new Font("Segoe UI", 9f, FontStyle.Bold), Color.FromArgb(56, 189, 248));
            pnlChartCard.Controls.Add(_lblOscHeader);

            _btnGridStep = new Button();
            _btnGridStep.Text = "Сетка: 5 сек";
            _btnGridStep.Location = new Point(344, 6);
            _btnGridStep.Size = new Size(104, 24);
            _btnGridStep.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnGridStep.FlatStyle = FlatStyle.Flat;
            _btnGridStep.BackColor = Color.FromArgb(51, 65, 85);
            _btnGridStep.ForeColor = Color.White;
            _btnGridStep.Click += delegate
            {
                int next = (_oscilloscope.GridStepSeconds == 5) ? 10 : 5;
                _oscilloscope.GridStepSeconds = next;
                UpdateGridStepButtonText();
            };
            pnlChartCard.Controls.Add(_btnGridStep);
            _btnGridStep.BringToFront();

            _btnPauseChart = new Button();
            _btnPauseChart.Text = "Пауза графика";
            _btnPauseChart.Location = new Point(454, 6);
            _btnPauseChart.Size = new Size(110, 24);
            _btnPauseChart.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnPauseChart.FlatStyle = FlatStyle.Flat;
            _btnPauseChart.BackColor = Color.FromArgb(51, 65, 85);
            _btnPauseChart.ForeColor = Color.White;
            _btnPauseChart.Click += delegate
            {
                _chartPaused = !_chartPaused;
                UpdatePauseButtonText();
                _btnPauseChart.BackColor = _chartPaused ? Color.FromArgb(180, 83, 9) : Color.FromArgb(51, 65, 85);
            };
            pnlChartCard.Controls.Add(_btnPauseChart);
            _btnPauseChart.BringToFront();

            _btnClearSel = new Button();
            _btnClearSel.Text = "Сброс замера";
            _btnClearSel.Location = new Point(570, 6);
            _btnClearSel.Size = new Size(100, 24);
            _btnClearSel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnClearSel.FlatStyle = FlatStyle.Flat;
            _btnClearSel.BackColor = Color.FromArgb(51, 65, 85);
            _btnClearSel.ForeColor = Color.White;
            _btnClearSel.Click += delegate { _oscilloscope.ClearSelection(); };
            pnlChartCard.Controls.Add(_btnClearSel);
            _btnClearSel.BringToFront();

            _lblMeasureBanner = CreateLabel(
                "Зажмите левую кнопку мыши на графике и протяните по горизонтали для замера времени Δt (с) и скорости (°C/с, %/с)",
                14, 34, 656, 20,
                new Font("Consolas", 8.5f, FontStyle.Bold), Color.FromArgb(125, 211, 252));
            _lblMeasureBanner.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            pnlChartCard.Controls.Add(_lblMeasureBanner);

            _oscilloscope = new PidOscilloscopeControl();
            _oscilloscope.Location = new Point(12, 58);
            _oscilloscope.Size = new Size(660, 508);
            _oscilloscope.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _oscilloscope.OnMeasurementChanged += delegate(string text)
            {
                _lblMeasureBanner.Text = text;
            };
            pnlChartCard.Controls.Add(_oscilloscope);

            Controls.Add(pnlChartCard);
        }

        private void UpdateGridStepButtonText()
        {
            int step = (_oscilloscope != null) ? _oscilloscope.GridStepSeconds : 5;
            _btnGridStep.Text = _isEnglish
                ? ("Grid: " + step.ToString(CultureInfo.InvariantCulture) + " sec")
                : ("Сетка: " + step.ToString(CultureInfo.InvariantCulture) + " сек");
        }

        private void UpdatePauseButtonText()
        {
            if (_isEnglish)
            {
                _btnPauseChart.Text = _chartPaused ? "Resume Chart" : "Pause Chart";
            }
            else
            {
                _btnPauseChart.Text = _chartPaused ? "Пуск графика" : "Пауза графика";
            }
        }

        private void ApplyLanguage(bool isEnglish, bool saveToConfig)
        {
            _isEnglish = isEnglish;
            SensorChoiceItem.IsEnglishUi = isEnglish;

            _btnLangRu.BackColor = !isEnglish ? Color.FromArgb(2, 132, 199) : Color.FromArgb(51, 65, 85);
            _btnLangRu.ForeColor = Color.White;
            _btnLangEn.BackColor = isEnglish ? Color.FromArgb(2, 132, 199) : Color.FromArgb(51, 65, 85);
            _btnLangEn.ForeColor = Color.White;

            if (isEnglish)
            {
                Text = "Pump PID Controller & Oscilloscope Configurator — FanControl";
                _lblSec1Title.Text = "1. TEMPERATURE SENSOR SELECTION (SOURCE)";
                _lblSensorSub.Text = "Sensors discovered in running FanControl:";
                _btnRefreshSensors.Text = "Refresh List";
                _lblSensorFilter.Text = "Sensor ID / Filter:";

                _lblSec2Title.Text = "2. SETPOINT (°C) & PUMP SPEED RANGE (MIN % — MAX %)";
                _lblSetpointTitle.Text = "Target Setpoint (°C):";
                _lblMinOutTitle.Text = "Min Pump Speed (%):";
                _lblMaxOutTitle.Text = "Max Pump Speed (%):";

                _lblSec3Title.Text = "3. PID GAINS & MODE (BUMPLESS TRANSFER)";
                _lblKpTitle.Text = "Kp (Prop.):";
                _lblKiTitle.Text = "Ki (Int.):";
                _lblKdTitle.Text = "Kd (Deriv.):";
                _lblTauTitle.Text = "Derivative Filter Tau (sec):";
                _rbAuto.Text = "AUTO (PID Controller)";
                _rbManual.Text = "MANUAL MODE (%):";

                _btnApply.Text = "APPLY SETTINGS TO FANCONTROL (INSTANT HOT-RELOAD)";
                _lblTelHeader.Text = "LIVE TELEMETRY FROM FANCONTROL";
                _lblOscHeader.Text = "TRANSIENT OSCILLOSCOPE";
                _btnClearSel.Text = "Reset Measure";
            }
            else
            {
                Text = "Настройка PID-регулятора помпы и Осциллограф — FanControl";
                _lblSec1Title.Text = "1. ВЫБОР ДАТЧИКА ТЕМПЕРАТУРЫ (ИСТОЧНИК)";
                _lblSensorSub.Text = "Датчики, обнаруженные в запущенном FanControl:";
                _btnRefreshSensors.Text = "Обновить список";
                _lblSensorFilter.Text = "Фильтр / ID датчика:";

                _lblSec2Title.Text = "2. УСТАВКА (°C) И ДИАПАЗОН ПОМПЫ (MIN % — MAX %)";
                _lblSetpointTitle.Text = "Целевая уставка (°C):";
                _lblMinOutTitle.Text = "Мин. обороты (Min %):";
                _lblMaxOutTitle.Text = "Макс. обороты (Max %):";

                _lblSec3Title.Text = "3. КОЭФФИЦИЕНТЫ PID И РЕЖИМ (БЕЗУДАРНЫЙ ПЕРЕХОД)";
                _lblKpTitle.Text = "Kp (Проп.):";
                _lblKiTitle.Text = "Ki (Инт.):";
                _lblKdTitle.Text = "Kd (Диф.):";
                _lblTauTitle.Text = "Фильтр дифференциала Tau (сек):";
                _rbAuto.Text = "АВТО (PID-регулятор)";
                _rbManual.Text = "РУЧНОЙ РЕЖИМ (%):";

                _btnApply.Text = "ПРИМЕНИТЬ НАСТРОЙКИ К FANCONTROL (МГНОВЕННО)";
                _lblTelHeader.Text = "ТЕЛЕМЕТРИЯ РЕАЛЬНОГО ВРЕМЕНИ ИЗ FANCONTROL";
                _lblOscHeader.Text = "ОСЦИЛЛОГРАФ ПЕРЕХОДНЫХ ПРОЦЕССОВ";
                _btnClearSel.Text = "Сброс замера";
            }

            UpdateSaveStatusText();
            UpdateGridStepButtonText();
            UpdatePauseButtonText();

            if (_oscilloscope != null)
            {
                _oscilloscope.IsEnglish = isEnglish;
            }

            // Refresh ComboBox items text so [Пользовательский] <-> [Custom] updates immediately
            if (_cmbSensors != null && _cmbSensors.Items.Count > 0)
            {
                _suppressEvents = true;
                int sel = _cmbSensors.SelectedIndex;
                object[] copy = new object[_cmbSensors.Items.Count];
                _cmbSensors.Items.CopyTo(copy, 0);
                _cmbSensors.Items.Clear();
                _cmbSensors.Items.AddRange(copy);
                _cmbSensors.SelectedIndex = sel;
                _suppressEvents = false;
            }

            UpdateLiveTelemetryLabels();

            if (saveToConfig && !_suppressEvents)
            {
                SaveConfigFromUi();
            }
        }

        private void UpdateSaveStatusText()
        {
            if (string.IsNullOrEmpty(_lastSavedTimeStr))
            {
                _lblSaveStatus.Text = _isEnglish
                    ? "Ready. All changes are saved and applied automatically."
                    : "Готово к работе. Все изменения сохраняются автоматически.";
            }
            else
            {
                _lblSaveStatus.Text = _isEnglish
                    ? ("Saved at " + _lastSavedTimeStr + " (applied to FanControl)")
                    : ("Сохранено в " + _lastSavedTimeStr + " (применено к FanControl)");
            }
        }

        private void UpdateLiveTelemetryLabels()
        {
            if (string.IsNullOrEmpty(_lastUpdatedAt))
            {
                _lblLiveOutput.Text = _isEnglish ? "Pump: --.- %" : "Помпа: --.- %";
                _lblLiveSensorName.Text = _isEnglish
                    ? "Active sensor: waiting for telemetry from FanControl..."
                    : "Активный датчик: ожидание данных от FanControl...";
                return;
            }

            _lblLiveOutput.Text = string.Format(
                CultureInfo.InvariantCulture,
                _isEnglish ? "Pump: {0:F1} %" : "Помпа: {0:F1} %",
                _lastOutPct);

            string displaySensor = _lastActiveSensor ?? "";
            if (_isEnglish)
            {
                displaySensor = displaySensor.Replace("[Пользовательский]", "[Custom]");
                _lblLiveSensorName.Text = "Active sensor: " + displaySensor + "  (updated at " + _lastUpdatedAt + ")";
            }
            else
            {
                _lblLiveSensorName.Text = "Активный датчик: " + displaySensor + "  (обновлено в " + _lastUpdatedAt + ")";
            }
        }

        private void OnSensorComboSelected(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            SensorChoiceItem item = _cmbSensors.SelectedItem as SensorChoiceItem;
            if (item != null && !string.IsNullOrEmpty(item.Id))
            {
                _txtCustomSensor.Text = item.Id;
                SaveConfigFromUi();
            }
        }

        private void LoadConfigIntoUi()
        {
            try
            {
                _suppressEvents = true;

                // Значения по умолчанию (Setpoint=60°C, Min=25%, Max=75%, Kp=0.4, Ki=0.8, Kd=1.8, Tau=2.0)
                _txtCustomSensor.Text = "Core Average";
                _numSetpoint.Value = 60m;
                _trkSetpoint.Value = 60;
                _numMinOut.Value = 25m;
                _numMaxOut.Value = 75m;
                _numKp.Value = 0.40m;
                _numKi.Value = 0.80m;
                _numKd.Value = 1.80m;
                _numTau.Value = 2.0m;
                _rbAuto.Checked = true;
                _rbManual.Checked = false;
                _numManualOut.Value = 50m;
                _allowInputTempBridgeCard = false;

                if (!File.Exists(_configPath)) return;
                string json = File.ReadAllText(_configPath, Encoding.UTF8);

                string lang = ReadJsonString(json, "UiLanguage", "ru");
                _isEnglish = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);

                _txtCustomSensor.Text = ReadJsonString(json, "SourceSensorId", "Core Average");

                decimal sp = ClampDec((decimal)ReadJsonFloat(json, "SetpointCelsius", 60f), 15m, 95m);
                _numSetpoint.Value = sp;
                _trkSetpoint.Value = (int)Math.Round(sp);

                _numMinOut.Value = ClampDec((decimal)ReadJsonFloat(json, "MinOutputPercent", 25f), 0m, 100m);
                _numMaxOut.Value = ClampDec((decimal)ReadJsonFloat(json, "MaxOutputPercent", 75f), 0m, 100m);

                _numKp.Value = ClampDec((decimal)ReadJsonFloat(json, "Kp", 0.4f), 0.05m, 50m);
                _numKi.Value = ClampDec((decimal)ReadJsonFloat(json, "Ki", 0.8f), 0m, 20m);
                _numKd.Value = ClampDec((decimal)ReadJsonFloat(json, "Kd", 1.8f), 0m, 30m);
                _numTau.Value = ClampDec((decimal)ReadJsonFloat(json, "DerivativeFilterTau", 2.0f), 0.1m, 20m);

                bool isAuto = ReadJsonBool(json, "IsAutoMode", true);
                _rbAuto.Checked = isAuto;
                _rbManual.Checked = !isAuto;
                _numManualOut.Value = ClampDec((decimal)ReadJsonFloat(json, "ManualOutputPercent", 50f), 0m, 100m);
                _allowInputTempBridgeCard = ReadJsonBool(json, "AllowInputTempBridgeCard", false);
            }
            catch
            {
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void SaveConfigFromUi()
        {
            try
            {
                string sensorId = (_txtCustomSensor.Text ?? "Core Average").Trim();
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"UiLanguage\": \"{0}\",", _isEnglish ? "en" : "ru"));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"SourceSensorId\": \"{0}\",", EscapeJson(sensorId)));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"SetpointCelsius\": {0:F1},", _numSetpoint.Value));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"Kp\": {0:F2},", _numKp.Value));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"Ki\": {0:F2},", _numKi.Value));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"Kd\": {0:F2},", _numKd.Value));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"DerivativeFilterTau\": {0:F1},", _numTau.Value));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"MinOutputPercent\": {0:F0},", _numMinOut.Value));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"MaxOutputPercent\": {0:F0},", _numMaxOut.Value));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"IsAutoMode\": {0},", _rbAuto.Checked ? "true" : "false"));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"ManualOutputPercent\": {0:F1},", _numManualOut.Value));
                sb.AppendLine("  \"EnableFileLogging\": true,");
                sb.AppendLine("  \"SettlingBandCelsius\": 0.3,");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"AllowInputTempBridgeCard\": {0}", _allowInputTempBridgeCard ? "true" : "false"));
                sb.AppendLine("}");

                File.WriteAllText(_configPath, sb.ToString(), Encoding.UTF8);
                _lastSavedTimeStr = DateTime.Now.ToString("HH:mm:ss");
                UpdateSaveStatusText();
            }
            catch (Exception ex)
            {
                _lblSaveStatus.Text = (_isEnglish ? "Error: " : "Ошибка: ") + ex.Message;
            }
        }

        private void SetPluginsDirectory(string pluginsDir)
        {
            _pluginsDir = pluginsDir;
            _configPath = Path.Combine(pluginsDir, "PumpPidConfig.json");
            _statusPath = Path.Combine(pluginsDir, "PumpPidLiveStatus.json");
            _logPath = Path.Combine(pluginsDir, "PumpPidEvents.log");
        }

        private void PollLiveStatus()
        {
            try
            {
                // Если пользователь запустил FanControl уже после открытия конфигуратора — автоматически подхватываем путь к его папке Plugins
                if (!File.Exists(_statusPath))
                {
                    string detected = TryGetRunningFanControlPluginsDir();
                    if (!string.IsNullOrEmpty(detected) &&
                        !string.Equals(detected, _pluginsDir, StringComparison.OrdinalIgnoreCase) &&
                        Directory.Exists(detected))
                    {
                        SetPluginsDirectory(detected);
                        UnblockDownloadedFilesInFolder(detected);
                        LoadConfigIntoUi();
                    }
                }

                if (File.Exists(_statusPath))
                {
                    string json = File.ReadAllText(_statusPath, Encoding.UTF8);
                    float actualTemp = ReadJsonFloat(json, "ActualTempCelsius", 0f);
                    float setpointTemp = ReadJsonFloat(json, "SetpointCelsius", (float)_numSetpoint.Value);
                    float outPct = ReadJsonFloat(json, "OutputPercent", 0f);
                    float minLim = ReadJsonFloat(json, "MinOutputPercent", (float)_numMinOut.Value);
                    float maxLim = ReadJsonFloat(json, "MaxOutputPercent", (float)_numMaxOut.Value);
                    float p = ReadJsonFloat(json, "PTerm", 0f);
                    float i = ReadJsonFloat(json, "ITerm", 0f);
                    float d = ReadJsonFloat(json, "DTerm", 0f);
                    string activeSensor = ReadJsonString(json, "ActiveSensor", "");
                    string updatedAt = ReadJsonString(json, "UpdatedAt", "");
                    long updatedAtUnixMs = ReadJsonLong(json, "UpdatedAtUnixMs", 0L);

                    _lastOutPct = outPct;
                    _lastActiveSensor = activeSensor;
                    _lastUpdatedAt = updatedAt;

                    _lblLiveTemp.Text = string.Format(CultureInfo.InvariantCulture, "{0:F1} °C", actualTemp);
                    UpdateLiveTelemetryLabels();
                    _lblLivePidTerms.Text = string.Format(CultureInfo.InvariantCulture,
                        "PID: P = {0:+0.0;-0.0;0.0}% | I = {1:F1}% | D = {2:+0.0;-0.0;0.0}%", p, i, d);

                    int w = Math.Max(4, Math.Min(226, (int)Math.Round(outPct / 100f * 226f)));
                    _barOutputFill.Width = w;

                    bool isNewSample = false;
                    double sampleTimeSec = _sampleCounterSec;

                    if (updatedAtUnixMs > 0L)
                    {
                        if (updatedAtUnixMs != _lastSampleUnixMs)
                        {
                            if (_firstSampleUnixMs <= 0L || updatedAtUnixMs < _firstSampleUnixMs)
                            {
                                _firstSampleUnixMs = updatedAtUnixMs;
                            }
                            _lastSampleUnixMs = updatedAtUnixMs;
                            _lastStatusTimestamp = updatedAt;
                            sampleTimeSec = (updatedAtUnixMs - _firstSampleUnixMs) / 1000.0;
                            _sampleCounterSec = sampleTimeSec;
                            isNewSample = true;
                        }
                    }
                    else if (!string.IsNullOrEmpty(updatedAt) && updatedAt != _lastStatusTimestamp)
                    {
                        _lastStatusTimestamp = updatedAt;
                        _sampleCounterSec += 0.65;
                        sampleTimeSec = _sampleCounterSec;
                        isNewSample = true;
                    }

                    if (!_chartPaused && isNewSample)
                    {
                        _oscilloscope.AddSample(new TelemetryPoint
                        {
                            TimeSec = sampleTimeSec,
                            ActualTemp = actualTemp,
                            SetpointTemp = setpointTemp,
                            OutputPercent = outPct,
                            IntegralPercent = i,
                            MinLimit = minLim,
                            MaxLimit = maxLim
                        });
                    }

                    // Парсим список найденных датчиков (системных и пользовательских)
                    var matches = Regex.Matches(json, "\\{\\s*\"Id\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"Name\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"Value\"\\s*:\\s*([-+]?[0-9]*\\.?[0-9]+)(?:\\s*,\\s*\"Custom\"\\s*:\\s*(true|false))?\\s*\\}");
                    if (matches.Count > 0 && !_cmbSensors.DroppedDown)
                    {
                        if (matches.Count != _lastSensorCount)
                        {
                            _lastSensorCount = matches.Count;
                            _lastComboRefreshUtc = DateTime.UtcNow;
                            _suppressEvents = true;
                            string currentCustom = (_txtCustomSensor.Text ?? "").Trim();
                            _cmbSensors.Items.Clear();
                            int selectedIdx = -1;

                            for (int idx = 0; idx < matches.Count; idx++)
                            {
                                Match m = matches[idx];
                                float val = 0f;
                                float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
                                bool isCust = m.Groups.Count > 4 && string.Equals(m.Groups[4].Value, "true", StringComparison.OrdinalIgnoreCase);
                                var item = new SensorChoiceItem
                                {
                                    Id = m.Groups[1].Value,
                                    Name = m.Groups[2].Value,
                                    Value = val,
                                    IsCustom = isCust
                                };
                                _cmbSensors.Items.Add(item);
                                if (string.Equals(item.Id, currentCustom, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(item.Name, currentCustom, StringComparison.OrdinalIgnoreCase) ||
                                    item.Name.IndexOf(currentCustom, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    selectedIdx = idx;
                                }
                            }

                            if (selectedIdx >= 0) _cmbSensors.SelectedIndex = selectedIdx;
                            _suppressEvents = false;
                        }
                        else if ((DateTime.UtcNow - _lastComboRefreshUtc).TotalSeconds >= 2.0 && _cmbSensors.Items.Count == matches.Count)
                        {
                            // Плавное обновление живых значений градусов [XX.X °C] в выпадающем списке каждые 2 с
                            _lastComboRefreshUtc = DateTime.UtcNow;
                            bool anyChanged = false;
                            for (int idx = 0; idx < matches.Count; idx++)
                            {
                                SensorChoiceItem existingItem = _cmbSensors.Items[idx] as SensorChoiceItem;
                                if (existingItem != null)
                                {
                                    float val = 0f;
                                    if (float.TryParse(matches[idx].Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val) &&
                                        Math.Abs(existingItem.Value - val) >= 0.1f)
                                    {
                                        existingItem.Value = val;
                                        anyChanged = true;
                                    }
                                }
                            }

                            if (anyChanged)
                            {
                                _suppressEvents = true;
                                int sel = _cmbSensors.SelectedIndex;
                                object[] copy = new object[_cmbSensors.Items.Count];
                                _cmbSensors.Items.CopyTo(copy, 0);
                                _cmbSensors.Items.Clear();
                                _cmbSensors.Items.AddRange(copy);
                                _cmbSensors.SelectedIndex = sel;
                                _suppressEvents = false;
                            }
                        }
                    }
                }

                // Читаем только последние 8 КБ лог-файла и только при изменении его размера/времени записи
                if (File.Exists(_logPath))
                {
                    var fi = new FileInfo(_logPath);
                    if (fi.Length != _lastLogFileLength || fi.LastWriteTimeUtc != _lastLogWriteUtc)
                    {
                        _lastLogFileLength = fi.Length;
                        _lastLogWriteUtc = fi.LastWriteTimeUtc;
                        _txtLog.Text = ReadTailLogLines(_logPath, 10, 8192);
                    }
                }
            }
            catch
            {
            }
        }

        private static string ReadTailLogLines(string filePath, int maxLines, int tailBytes)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length == 0) return string.Empty;
                    long startPos = Math.Max(0L, fs.Length - tailBytes);
                    fs.Seek(startPos, SeekOrigin.Begin);
                    int count = (int)(fs.Length - startPos);
                    byte[] buffer = new byte[count];
                    int read = fs.Read(buffer, 0, count);
                    string chunk = Encoding.UTF8.GetString(buffer, 0, read);
                    string[] rawLines = chunk.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    int firstIdx = (startPos > 0 && rawLines.Length > 1) ? 1 : 0;
                    int takeFrom = Math.Max(firstIdx, rawLines.Length - maxLines);
                    var sb = new StringBuilder();
                    for (int k = rawLines.Length - 1; k >= takeFrom; k--)
                    {
                        sb.AppendLine(rawLines[k]);
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Panel CreateCardPanel(int x, int y, int w, int h)
        {
            var p = new Panel();
            p.Location = new Point(x, y);
            p.Size = new Size(w, h);
            p.BackColor = Color.FromArgb(30, 41, 59);
            p.BorderStyle = BorderStyle.FixedSingle;
            return p;
        }

        private static Label CreateLabel(string text, int x, int y, int w, int h, Font font, Color color)
        {
            var l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(w, h);
            l.Font = font;
            l.ForeColor = color;
            return l;
        }

        private static NumericUpDown CreateNumeric(int x, int y, int w, decimal min, decimal max, decimal inc, int decimals)
        {
            var n = new NumericUpDown();
            n.Location = new Point(x, y);
            n.Size = new Size(w, 24);
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = inc;
            n.DecimalPlaces = decimals;
            n.BackColor = Color.FromArgb(15, 23, 42);
            n.ForeColor = Color.White;
            return n;
        }

        private static decimal ClampDec(decimal val, decimal min, decimal max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        private static string EscapeJson(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ReadJsonString(string json, string key, string def)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : def;
        }

        private static float ReadJsonFloat(string json, string key, float def)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([-+]?[0-9]*\\.?[0-9]+)", RegexOptions.IgnoreCase);
            float v;
            if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return def;
        }

        private static long ReadJsonLong(string json, string key, long def)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([0-9]+)", RegexOptions.IgnoreCase);
            long v;
            if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return v;
            return def;
        }

        private static bool ReadJsonBool(string json, string key, bool def)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            bool b;
            if (m.Success && bool.TryParse(m.Groups[1].Value, out b))
                return b;
            return def;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        private static void UnblockDownloadedFilesInFolder(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                foreach (string file in Directory.GetFiles(folder))
                {
                    try
                    {
                        DeleteFile(file + ":Zone.Identifier");
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Автоматически находит папку FanControlPlugins по запущенному процессу FanControl.exe
        /// (использует Win32 QueryFullProcessImageName с правами PROCESS_QUERY_LIMITED_INFORMATION,
        /// поэтому работает даже если FanControl запущен от имени Администратора, а конфигуратор — обычно).
        /// </summary>
        private static string TryGetRunningFanControlPluginsDir()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("FanControl");
                for (int i = 0; i < procs.Length; i++)
                {
                    string exePath = null;
                    try
                    {
                        if (procs[i].MainModule != null)
                        {
                            exePath = procs[i].MainModule.FileName;
                        }
                    }
                    catch
                    {
                    }

                    if (string.IsNullOrEmpty(exePath))
                    {
                        IntPtr hProc = IntPtr.Zero;
                        try
                        {
                            // PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
                            hProc = OpenProcess(0x1000, false, procs[i].Id);
                            if (hProc != IntPtr.Zero)
                            {
                                var sb = new StringBuilder(1024);
                                int cap = sb.Capacity;
                                if (QueryFullProcessImageName(hProc, 0, sb, ref cap))
                                {
                                    exePath = sb.ToString();
                                }
                            }
                        }
                        catch
                        {
                        }
                        finally
                        {
                            if (hProc != IntPtr.Zero) CloseHandle(hProc);
                        }
                    }

                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        string fcDir = Path.GetDirectoryName(exePath);
                        if (!string.IsNullOrEmpty(fcDir))
                        {
                            string pluginsDir = Path.Combine(fcDir, "Plugins");
                            if (Directory.Exists(pluginsDir))
                            {
                                return pluginsDir;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static string ResolveFanControlPluginsFolder(string[] args)
        {
            // 1. Если передан явный путь через аргумент командной строки
            if (args != null && args.Length > 0 && !string.IsNullOrEmpty(args[0]) && Directory.Exists(args[0]))
            {
                return args[0];
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

            // 2. Если сам .exe лежит прямо внутри папки FanControlPlugins (рядом с родительским FanControl.exe или PumpPidLiveStatus.json)
            DirectoryInfo parentDir = Directory.GetParent(baseDir);
            if (File.Exists(Path.Combine(baseDir, "PumpPidLiveStatus.json")) ||
                (parentDir != null && File.Exists(Path.Combine(parentDir.FullName, "FanControl.exe"))))
            {
                return baseDir;
            }

            // 3. Если .exe лежит в корневой папке FanControl рядом с FanControl.exe
            if (File.Exists(Path.Combine(baseDir, "FanControl.exe")) && Directory.Exists(Path.Combine(baseDir, "Plugins")))
            {
                return Path.Combine(baseDir, "Plugins");
            }

            // 4. Автоматический поиск по запущенному в памяти процессу FanControl.exe (работает из любой папки без .bat!)
            string runningPluginsDir = TryGetRunningFanControlPluginsDir();
            if (!string.IsNullOrEmpty(runningPluginsDir))
            {
                return runningPluginsDir;
            }

            // 5. Проверка стандартных путей установки FanControl
            string[] commonDirs = new[]
            {
                @"C:\Program Files\FanControl\Plugins",
                @"C:\Program Files (x86)\FanControl\Plugins",
                @"D:\Program Files\FanControl\Plugins",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FanControl", "Plugins")
            };
            for (int i = 0; i < commonDirs.Length; i++)
            {
                if (Directory.Exists(commonDirs[i]) &&
                    (File.Exists(Path.Combine(commonDirs[i], "FanControl.PumpPid.dll")) || File.Exists(Path.Combine(commonDirs[i], "PumpPidConfig.json"))))
                {
                    return commonDirs[i];
                }
            }

            return baseDir;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string dir = ResolveFanControlPluginsFolder(args);

            // Автоматически снимаем блокировку Windows (Zone.Identifier) со скачанных из интернета файлов плагина
            UnblockDownloadedFilesInFolder(dir);

            Application.Run(new MainForm(dir));
        }
    }
}
# FanControl.PumpPid — Liquid Cooling Pump PID Controller & Oscilloscope for [FanControl](https://github.com/Rem0o/FanControl.Releases)

[![Platform](https://img.shields.io/badge/FanControl-Plugin%20(IPlugin2)-0284c7)](https://github.com/Rem0o/FanControl.Releases)
[![Runtime](https://img.shields.io/badge/.NET-Standard%202.0%20%7C%20.NET%204.8%20%2F%208.0-10b981)](#)
[![GUI](https://img.shields.io/badge/GUI%20Configurator-RU%20%7C%20EN%20%2B%20Oscilloscope-f59e0b)](#)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> 🔗 **Официальный репозиторий FanControl на GitHub / Official FanControl GitHub Repository:**
> **[https://github.com/Rem0o/FanControl.Releases](https://github.com/Rem0o/FanControl.Releases)** *(веб-сайт: [getfancontrol.com](https://getfancontrol.com/))*
>
> **Язык документации / Documentation Language:**
> 🇷🇺 **[Русская версия (Подробное руководство ниже)](#-русская-версия--руководство-по-установке-и-настройке)** | 🇬🇧 **[English Version (Full Guide below)](#-english-version--installation--configuration-guide)**

---

# 🇷🇺 Русская версия — Руководство по установке и настройке

**`FanControl.PumpPid`** — это специализированный плагин для **[FanControl](https://github.com/Rem0o/FanControl.Releases)** ([GitHub: Rem0o/FanControl.Releases](https://github.com/Rem0o/FanControl.Releases)), реализующий промышленный **пропорционально-интегрально-дифференциальный (PID) регулятор** для управления оборотами помпы жидкостного охлаждения (СЖО / кастомного контура / AIO) с собственным графическим конфигуратором **`PumpPidConfigurator.exe` (RU / EN)** и встроенным **двухканальным осциллографом переходных процессов**.

> ⚠️ **ВАЖНОЕ ПРИМЕЧАНИЕ (Для пользователей, понимающих принципы настройки PID):**
> Данный плагин представляет собой полноценный инженерный **PID-регулятор с обратной связью**, а не обычную температурную кривую. Регулятор предназначен для пользователей, которые **понимают физический смысл коэффициентов `Kp`, `Ki`, `Kd` и постоянной времени фильтра `Tau`**, а также умеют настраивать их под тепловую инерцию своего контура СЖО с помощью встроенного осциллографа. Некорректный подбор коэффициентов (например, сильно завышенный `Kp` или `Ki`) может привести к слишком резкому управлению, автоколебаниям оборотов помпы или недостаточному охлаждению.

---

## ✨ Ключевые особенности

1. **Безударный переход (`Bumpless Transfer`)**:
   - При переключении между ручным (`MANUAL`) и автоматическим (`AUTO`) режимом или при изменении коэффициента `Kp` на лету интегратор автоматически пересчитывается по формуле $I = U_{\text{hold}} - P - D$ с пропуском интегрального шага на тике переключения.
   - Помпа **никогда** не испытывает резких скачков ШИМ и гидроударов при переключении настроек (`0.0%` скачка выхода).
2. **Дифференцирование по измерению (`Derivative-on-Measurement`) + ФНЧ ($\tau_d$)**:
   - Так как для контура охлаждения ошибка равна $e = T_{\text{actual}} - T_{\text{setpoint}}$, дифференциальная составляющая вычисляется напрямую по отфильтрованной скорости роста температуры датчика ($+K_d \cdot \text{LPF}(dT/dt)$), а не по производной ошибки $de/dt$, что полностью исключает «дифференциальный удар» (`Derivative Kick`) при смене целевой уставки.
   - Встроенный фильтр нижних частот с постоянной времени `Tau (сек)` сглаживает резкие кратковременные всплески температуры современных процессоров Ryzen / Intel Core.
3. **Динамический `Anti-Windup` и защита диапазона помпы `[Min % .. Max %]`**:
   - Жесткое удержание управляющего сигнала и интегральной суммы в заданных безопасных границах оборотов помпы (по умолчанию от `25%` до `75%`).
4. **Поддержка как системных, так и пользовательских датчиков FanControl (`Custom Sensors`)**:
   - Безопасный полевой сканер памяти (`Field-Only`) обнаруживает не только аппаратные датчики CPU/GPU/материнской платы (`LibreHardwareMonitor`), но и созданные вами в FanControl **пользовательские датчики** (`Time Average`, `Mix`, `Offset`, `File`), помечая их значком `★ [Пользовательский]`.
5. **Графический конфигуратор `PumpPidConfigurator.exe` (RU / EN) с Осциллографом**:
   - Автоматически находит запущенный процесс `FanControl.exe` и его папку `Plugins` даже при запуске без `.bat`-файла из любой папки.
   - Мгновенное переключение языка интерфейса **`RU` / `EN`** в правом верхнем углу окна.
   - Горячее применение настроек (`Hot-Reload`) на лету без перезапуска FanControl (выбор датчика температуры, уставка, диапазон `Min..Max`, `Kp/Ki/Kd/Tau`, допуск стабилизации `SettlingBandCelsius`, режим `AUTO/MANUAL` и запись лога `EnableFileLogging` применяются мгновенно и фиксируются в `PumpPidEvents.log`).
   - Встроенный **двухканальный GDI+ осциллограф** с реальными миллисекундными метками времени, вертикальной сеткой (`5 сек` / `10 сек`) и интерактивным замером интервала мышью (`[t₁ .. t₂]`: расчет $\Delta t$, $\Delta T$, скорости нагрева/охлаждения $^circ\text{C/с}$, $\Delta U$ и скорости реакции помпы $\text{%/с}$).

---

## 📦 Установка (2 способа)

### Вариант А: Ручная установка из готового архива (`FanControl.PumpPid-Release.zip`)
1. Скачайте архив **`FanControl.PumpPid-Release.zip`** со страницы **Releases** на GitHub.
2. **Важно для Windows**: перед распаковкой нажмите правой кнопкой мыши по скачанному `.zip` архиву → **Свойства** → поставьте галочку **«Разблокировать» (Unblock)** → **Применить** (чтобы Windows не блокировала загрузку `.dll` плагина).
3. Скопируйте 3 файла из архива в папку **`Plugins`** вашего FanControl (например, `C:\Program Files\FanControl\Plugins\` или `C:\Program Files (x86)\FanControl\Plugins\`):
   - `FanControl.PumpPid.dll`
   - `PumpPidConfigurator.exe`
   - `PumpPidConfig.json`
4. Перезапустите **FanControl**.

### Вариант Б: Автоматическая установка из репозитория через PowerShell (`Install-Plugin.ps1`)
Если вы клонировали или скачали репозиторий с исходным кодом и хотите автоматически собрать и установить только готовые файлы плагина в папку `FanControl\Plugins\`:
1. Откройте PowerShell в папке репозитория и выполните:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Plugin.ps1
   ```
2. Скрипт автоматически найдет установленный у вас `FanControl.exe`, скомпилирует актуальные исходные файлы (`.cs` / `.csproj`) из папки репозитория в изолированной системной директории (`%TEMP%`), поместит **только 3 готовых файла** (`FanControl.PumpPid.dll`, `PumpPidConfigurator.exe`, `PumpPidConfig.json`) напрямую в папку `FanControl\Plugins\`, полностью очистит временные файлы сборки (не создавая никаких `.zip`-архивов) и откроет окно настройки!

> ⚠️ **ВНИМАНИЕ:** Никогда не копируйте сам файл `FanControl.Plugins.dll` внутрь подпапки `FanControl\Plugins\`! Этот файл уже лежит в корневой папке рядом с `FanControl.exe`. Наличие дубликата в папке `Plugins` вызовет конфликт типов .NET (`Type Identity mismatch`).

---

## 🔧 Пошаговая настройка внутри FanControl

Чтобы помпа управлялась от PID-регулятора, выполните **один раз** следующую привязку в окне FanControl:

### Шаг 1. Создайте линейную кривую-мост (0–100)
1. В разделе **«Кривые» (Curves)** нажмите кнопку **`+`** и выберите **«Линейная» (Linear)**.
2. Назовите кривую, например, **`PID Out`**.
3. В выпадающем списке **«Источник температуры» (Temperature source)** выберите датчик плагина:
   **`Pump PID Output (%) - Pump Bumpless PID Controller`**.
4. Выставьте параметры линейной кривой строго **1 к 1**:
   - **Мин. температура (Min. temp)**: `0 °C`  |  **Мин. скорость (Min. speed)**: `0 %`
   - **Макс. температура (Max. temp)**: `100 °C`  |  **Макс. скорость (Max. speed)**: `100 %`
   - **Гистерезис (Hysteresis)**: `0 °C` (или минимальный `1 °C • 0 сек`)
   *(Так как FanControl передает в кривые значения как «градусы», плагин выдает рассчитанный процент ШИМ `0..100%` через этот виртуальный датчик, а линейная кривая `0..100` без искажений переводит его в `0..100%` скорости помпы).*

### Шаг 2. Привяжите кривую к карточке вашей помпы (Pump)
1. Найдите карточку вашей помпы (**Pump**) в верхнем разделе **«Управление» (Controls)**.
2. Включите управление и выберите в поле **«Кривая» (Curve)** созданную кривую **`PID Out`**.

### Шаг 3. КРИТИЧЕСКИ ВАЖНО: Настройте «Шаг вверх / Шаг вниз» (Step Up / Step Down) у помпы
В карточке помпы в FanControl есть параметры ограничения скорости изменения процента в секунду (**«Шаг вверх» / «Шаг вниз»**, по умолчанию часто стоит `2 %/sec`).
- Если оставить `2 %/sec`, то когда PID-регулятор при резком нагреве процессора скомандует поднять обороты с `25%` до `75%`, сам FanControl будет искусственно растягивать этот подъем на **25 секунд**! Из-за этого возникнет фазовое запаздывание, перегрев и медленные автоколебания («качели»).
- **Рекомендуемые значения для карточки помпы**:
  - **Шаг вверх (Step Up)**: **`15 %/sec` — `25 %/sec`** (чтобы помпа быстро реагировала на P и D составляющие).
  - **Шаг вниз (Step Down)**: **`10 %/sec` — `15 %/sec`** (плавное снижение оборотов после снятия нагрузки).
  - **Минимум % (Minimum %)**: выставьте равным или чуть ниже вашего `Min %` в конфигураторе PID (например, `20%` или `25%`).

---

## 🖥️ Работа с конфигуратором `PumpPidConfigurator.exe` (RU / EN)

Запустите файл **`PumpPidConfigurator.exe`** прямо из папки `FanControl\Plugins\` (или из любой папки при запущенном FanControl):

1. **Выбор языка (`RU` | `EN`)**:
   - В правом верхнем углу окна нажмите **`RU`** для русского интерфейса или **`EN`** для английского. Выбор сохраняется автоматически.
2. **1. Выбор датчика температуры (Источник)**:
   - Выбор датчика температуры выполняется напрямую через выпадающий список в `PumpPidConfigurator.exe`, где отображаются все найденные в запущенном FanControl датчики с живым обновлением текущих градусов:
     - `★ [Пользовательский]` / `★ [Custom]` — ваши кастомные датчики из FanControl (например, `Time Avg CPU max`, `Mix CPU\GPU`).
     - `[Системный]` / `[System]` — аппаратные датчики процессора, видеокарты, материнской платы и датчика воды СЖО.
   - *Примечание*: если вы только что запустили FanControl, подождите 8–10 секунд, пока FanControl инициализирует все датчики, и нажмите кнопку **«Обновить список» (`Refresh List`)**.
3. **2. Уставка (`°C`) и диапазон помпы (`Min % — Max %`)**:
   - **Целевая уставка (`°C`)**: температура, которую регулятор стремится удерживать (по умолчанию `60.0 °C`).
   - **Мин. обороты (`Min %`) / Макс. обороты (`Max %`)**: рабочие границы вашей помпы (по умолчанию `25% .. 75%`).
4. **3. Коэффициенты PID, опции и режим (`АВТО` / `РУЧНОЙ`)**:
   - **`Kp` (Пропорциональный, по умолчанию `0.40`)**: на сколько `%` мгновенно изменится мощность помпы при отклонении температуры на `1 °C`.
   - **`Ki` (Интегральный, по умолчанию `0.80`)**: скорость накопления коррекции (`% / (°C·с)`) для плавного доведения температуры до целевой уставки.
   - **`Kd` (Дифференциальный, по умолчанию `1.80`)**: реакция на скорость роста температуры (`%·с / °C`) — дает упреждающий импульс помпе при резком скачке нагрузки.
   - **`Tau` (сек, по умолчанию `2.0 с`)**: постоянная времени фильтра сглаживания дифференциала.
   - **`Допуск ±°C` (`SettlingBandCelsius`, по умолчанию `0.30 °C`)**: коридор стабилизации вокруг уставки для замера времени выхода на режим.
   - **Галочка «Запись лога событий в файл (PumpPidEvents.log)» (`EnableFileLogging`)**: включает/отключает запись событий (включая все изменения конфигурации `[CONFIG_CHANGE]`) в `PumpPidEvents.log` на лету.
5. **Осциллограф переходных процессов и замер интервала мышью**:
   - **Кнопка «Сетка: 5 сек / 10 сек»**: переключает шаг вертикальных линий разметки времени.
   - **Замер мышью**: зажмите **левую кнопку мыши** на графике и протяните вправо от начала нагрева до выхода на полку — над графиком мгновенно отобразится точное время переходного процесса $\Delta t$ (в секундах), перепад температуры $\Delta T$ ($^circ\text{C/с}$) и скорость реакции помпы $\Delta U$ ($\text{%/с}$). Правый клик мыши (или кнопка «Сброс замера») снимает выделение.

---

## 📊 Параметры по умолчанию и стартовые пресеты PID

> 💡 **Рекомендация по настройке:** Всегда начинайте с **Базового профиля по умолчанию** (`Kp = 0.40`, `Ki = 0.80`, `Kd = 1.80`, `Tau = 2.0 с`). Избегайте чрезмерно высоких значений `Kp` (выше `1.5–2.0`), так как они превращают плавный регулятор в резкое релейное переключение оборотов помпы.

| Профиль / Тип датчика | Уставка (`°C`) | Диапазон (`Min..Max`) | `Kp` | `Ki` | `Kd` | `Tau (с)` | Особенности поведения |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **1. По умолчанию (Базовый сбалансированный)** | `60.0 °C` | `25% .. 75%` | **`0.40`** | **`0.80`** | **`1.80`** | **`2.0`** | **Стандартный профиль из коробки.** Плавная работа без резких рывков помпы + уверенное упреждение нагрева за счет `Kd = 1.8` |
| **2. Тихий / Мягкий контур (`Silent / Smooth`)** | `62.0 °C` | `25% .. 70%` | `0.25` | `0.50` | `1.20` | `2.5` | Максимально мягкий и бесшумный набор оборотов; увеличенный фильтр `Tau = 2.5 с` полностью игнорирует краткие всплески буста |
| **3. Динамичный (`High Load / Быстрый отклик`)** | `58.0 °C` | `30% .. 85%` | `0.65` | `1.00` | `2.40` | `1.5` | Усиленный упреждающий импульс при резком старте рендеринга или стресс-тестов |
| **4. Датчик жидкости СЖО (`Water / Coolant Temp`)** | `38.0 °C` | `25% .. 75%` | `0.80` | `1.10` | `2.00` | `1.5` | Адаптирован под высокую теплоемкость и медленное изменение температуры жидкости в контуре |

---
---

# 🇬🇧 English Version — Installation & Configuration Guide

**`FanControl.PumpPid`** is a dedicated **[FanControl](https://github.com/Rem0o/FanControl.Releases)** ([GitHub: Rem0o/FanControl.Releases](https://github.com/Rem0o/FanControl.Releases)) plugin (`IPlugin2`) that brings an industrial-grade **PID Controller with Bumpless Transfer** to liquid cooling pumps (AIO & custom water loops), complete with a standalone **bilingual GUI Configurator (`PumpPidConfigurator.exe` with `RU | EN` toggle)** and a **real-time dual-channel GDI+ transient oscilloscope**.

> ⚠️ **IMPORTANT NOTICE (Intended for Users Familiar with PID Tuning):**
> This plugin implements a true closed-loop **PID Controller**, not a simple temperature lookup curve. It is designed for users who **understand how PID control loops work and how to tune `Kp`, `Ki`, `Kd`, and derivative filter `Tau` coefficients** for the thermal inertia of their specific cooling loop using the built-in oscilloscope. Improperly tuned or overly aggressive gains can cause abrupt pump speed swings, oscillations, or suboptimal cooling.

---

## ✨ Key Features

1. **Bumpless Transfer (`Manual <-> Auto` & Live `Kp` Changes)**:
   - Continuously synchronizes the internal integral state ($I = U_{\text{hold}} - P - D$) while in Manual mode or when adjusting `Kp` on the fly, skipping the integral accumulation step on the exact transition tick.
   - Guarantees a `0.0%` output jump, eliminating sudden PWM spikes and hydraulic shocks when switching modes or tuning parameters.
2. **Derivative-on-Measurement + Low-Pass Filter ($\tau_d$)**:
   - Because the cooling loop error is defined as $e = T_{\text{actual}} - T_{\text{setpoint}}$, the derivative term is computed directly from the filtered rate of change of the measured temperature ($+K_d \cdot \text{LPF}(dT/dt)$) rather than setpoint error $de/dt$, completely preventing **Derivative Kick** when changing the target setpoint.
   - First-order low-pass filter (`Tau`) suppresses rapid thermal spikes typical of modern AMD Ryzen and Intel Core CPUs.
3. **Dynamic Anti-Windup & Safe Pump Range `[Min % .. Max %]`**:
   - Strictly clamps both the PID output and the integral accumulator within your pump's safe operating envelope (defaults to `25% .. 75%`).
4. **Full Support for System & Custom FanControl Sensors**:
   - Uses a thread-safe, non-UI `Field-Only` memory scanner that discovers both hardware sensors (`LibreHardwareMonitor` CPU/GPU/Motherboard/Coolant) and your **FanControl Custom Sensors** (`Time Average`, `Mix`, `Offset`, `File`), badged as `★ [Custom]` in the dropdown.
5. **Standalone GUI Configurator (`PumpPidConfigurator.exe`) with Built-In Oscilloscope**:
   - Automatically detects the running `FanControl.exe` process and its `Plugins` folder even if launched directly from any folder without a `.bat` script.
   - Instant **`RU | EN`** language toggle in the top-right corner.
   - Live hot-reload via `FileSystemWatcher` — apply temperature sensor selection, setpoint, `Min..Max`, `Kp/Ki/Kd/Tau`, `SettlingBandCelsius`, `AUTO/MANUAL` mode, and `EnableFileLogging` changes on the fly without restarting FanControl (all changes are logged in English under `[CONFIG_CHANGE]` in `PumpPidEvents.log`).
   - **Dual-channel GDI+ oscilloscope** with real millisecond timestamps, `5s / 10s` vertical time grid lines, and **mouse drag-to-measure interval ruler** (`[t₁ .. t₂]` showing $\Delta t$, $\Delta T$, $^circ\text{C/s}$, $\Delta U$, and $\text{%/s}$).

---

## 📦 Installation (2 Options)

### Option A: Manual Install from Pre-Built Release Archive (`FanControl.PumpPid-Release.zip`)
1. Download **`FanControl.PumpPid-Release.zip`** from the **Releases** page.
2. **Important on Windows**: Right-click the downloaded `.zip` file → **Properties** → check **Unblock** → **Apply** before extracting.
3. Copy the 3 files into your **`FanControl\Plugins\`** folder (e.g. `C:\Program Files\FanControl\Plugins\` or `C:\Program Files (x86)\FanControl\Plugins\`):
   - `FanControl.PumpPid.dll`
   - `PumpPidConfigurator.exe`
   - `PumpPidConfig.json`
4. Restart **FanControl**.

### Option B: Automatic PowerShell Installer from Repository (`Install-Plugin.ps1`)
If you cloned or downloaded the source repository and want a 1-click script that automatically locates your FanControl folder and installs only the compiled plugin files:
1. Open PowerShell in the repository folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Plugin.ps1
   ```
2. The script automatically locates your `FanControl.exe` installation, compiles the repository's `.cs` and `.csproj` source files inside an isolated system `%TEMP%` folder, copies **only the 3 required files** (`FanControl.PumpPid.dll`, `PumpPidConfigurator.exe`, `PumpPidConfig.json`) directly into `FanControl\Plugins\`, cleans up all temporary build files (without generating any `.zip` archives), and launches the GUI Configurator.

> ⚠️ **IMPORTANT:** Never copy `FanControl.Plugins.dll` into the `FanControl\Plugins\` subfolder! `FanControl.Plugins.dll` already resides in the root `FanControl` directory next to `FanControl.exe`.

---

## 🔧 Step-by-Step Wiring Inside FanControl

Perform this one-time setup inside the FanControl UI to connect the PID controller output to your pump:

### Step 1. Create a 1:1 Linear Curve (`0–100`)
1. In the **Curves** section of FanControl, click **`+`** and add a **Linear** curve.
2. Name it e.g. **`PID Out`**.
3. Set **Temperature source** to: **`Pump PID Output (%) - Pump Bumpless PID Controller`**.
4. Configure a strict **1:1 mapping**:
   - **Min. temp**: `0 °C`  →  **Min. speed**: `0 %`
   - **Max. temp**: `100 °C`  →  **Max. speed**: `100 %`
   - **Hysteresis**: `0 °C` (or minimum `1 °C • 0 s`)

### Step 2. Assign the Curve to Your Pump Control Card
1. Locate your **Pump** card under **Controls**.
2. Enable control and select the **`PID Out`** curve.

### Step 3. CRITICAL: Adjust `Step Up` / `Step Down` Rate on Your Pump Card
FanControl's control cards have a built-in rate limiter (**Step Up / Step Down**, often defaulting to `2 %/sec`).
- If left at `2 %/sec`, a `50%` PID response will be artificially throttled over **25 seconds**, introducing phase lag and slow oscillations.
- **Recommended Pump Card settings**:
   - **Step Up**: **`15 %/sec` – `25 %/sec`** (allows immediate P and D response).
   - **Step Down**: **`10 %/sec` – `15 %/sec`** (smooth ramp-down after load drops).

---

## 🖥️ Using `PumpPidConfigurator.exe` (RU / EN)

Launch **`PumpPidConfigurator.exe`** directly from `FanControl\Plugins\` (or any folder while FanControl is running):
1. Click **`EN`** or **`RU`** in the top-right corner to switch the interface language at any time.
2. Select your desired **System** (`[System]`) or **Custom** (`★ [Custom]`) temperature sensor directly from the dropdown in `PumpPidConfigurator.exe` (if FanControl just started, wait ~8 seconds and click **Refresh List**).
3. Adjust **Target Setpoint (°C)** (default `60.0 °C`), **Min / Max Pump Speed (%)** (default `25% .. 75%`), **PID Gains (`Kp = 0.40`, `Ki = 0.80`, `Kd = 1.80`, `Tau = 2.0 s`)**, **Band ±°C (`SettlingBandCelsius`)**, and **Log events to file (`EnableFileLogging`)** — all settings are hot-reloaded by FanControl within 0.5 seconds without restarting FanControl.
4. **Interactive Oscilloscope Measurement**: Hold the **Left Mouse Button** on the chart and drag horizontally across any transient to measure $\Delta t$ (seconds), $\Delta T$ ($^circ\text{C/s}$), and $\Delta U$ ($\text{%/s}$). Right-click or press **Reset Measure** to clear.

---

## 📊 Default Parameters & Recommended PID Presets

| Profile / Sensor Type | Setpoint (`°C`) | Pump Range (`Min..Max`) | `Kp` | `Ki` | `Kd` | `Tau (s)` | Behavior Characteristics |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **1. Default Balanced (Out-of-the-Box)** | `60.0 °C` | `25% .. 75%` | **`0.40`** | **`0.80`** | **`1.80`** | **`2.0`** | **Default profile.** Smooth integral tracking without abrupt pump jumps + responsive derivative damping (`Kd = 1.8`) |
| **2. Silent / Smooth Curve** | `62.0 °C` | `25% .. 70%` | `0.25` | `0.50` | `1.20` | `2.5` | Ultra-gentle acoustic transitions; higher `Tau = 2.5 s` filters out brief CPU boost spikes |
| **3. Dynamic / High Heat Load** | `58.0 °C` | `30% .. 85%` | `0.65` | `1.00` | `2.40` | `1.5` | Stronger feed-forward derivative kick during heavy rendering or stress tests |
| **4. Coolant / Water Temp Sensor** | `38.0 °C` | `25% .. 75%` | `0.80` | `1.10` | `2.00` | `1.5` | Tuned for the higher thermal mass and slower temperature rate of liquid coolant sensors |

---

## 📄 License

Distributed under the **MIT License**. See [`LICENSE`](LICENSE) for details.

# =============================================================================
# Clean End-User Installer for FanControl.PumpPid (GitHub Repository Edition)
# Compiles the .cs / .csproj source files from this repository folder in %TEMP%,
# installs ONLY FanControl.PumpPid.dll, PumpPidConfigurator.exe, and PumpPidConfig.json
# directly into FanControl\Plugins, and cleans up temporary build files.
# Run in PowerShell: powershell -ExecutionPolicy Bypass -File .\Install-Plugin.ps1
# =============================================================================
$ErrorActionPreference = "Stop"

$RepoDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$RequiredSourceFiles = @(
    "PumpPidPlugin.cs",
    "BumplessPidController.cs",
    "PumpPidConfigurator.cs",
    "PumpPidEventLogger.cs",
    "VirtualSensors.cs",
    "PumpPidConfig.cs",
    "PumpPidConfig.json",
    "FanControl.PumpPid.csproj"
)

foreach ($srcFile in $RequiredSourceFiles) {
    $srcPath = Join-Path $RepoDir $srcFile
    if (-not (Test-Path $srcPath)) {
        throw "Required source file '$srcFile' was not found in '$RepoDir'. Please run Install-Plugin.ps1 from the cloned repository folder (or use FanControl.PumpPid-Release.zip for manual installation)."
    }
}

$FanControlDir = "C:\Program Files\FanControl"

# 1. Locate the user's FanControl installation directory (where FanControl.exe lives)
$fcProc = Get-Process -Name "FanControl" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($fcProc -and $fcProc.Path) {
    $detectedDir = Split-Path -Parent $fcProc.Path
    if (Test-Path (Join-Path $detectedDir "FanControl.Plugins.dll")) {
        $FanControlDir = $detectedDir
    }
}

if (-not (Test-Path (Join-Path $FanControlDir "FanControl.exe"))) {
    try {
        $taskInfo = Get-ScheduledTask -TaskName "*Fan*Control*" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($taskInfo) {
            $exePath = $taskInfo.Actions[0].Execute.Trim('"')
            if (Test-Path $exePath) {
                $FanControlDir = Split-Path -Parent $exePath
            }
        }
    } catch {}
}

if (-not (Test-Path (Join-Path $FanControlDir "FanControl.Plugins.dll"))) {
    $commonPaths = @(
        "C:\Program Files\FanControl",
        "C:\Program Files (x86)\FanControl",
        (Join-Path $env:LOCALAPPDATA "FanControl")
    )
    foreach ($cp in $commonPaths) {
        if (Test-Path (Join-Path $cp "FanControl.Plugins.dll")) {
            $FanControlDir = $cp
            break
        }
    }
}

if (-not (Test-Path (Join-Path $FanControlDir "FanControl.Plugins.dll"))) {
    Write-Host "[!] Could not automatically find FanControl.exe." -ForegroundColor Yellow
    $inputDir = Read-Host "Please paste the full path to your FanControl folder (where FanControl.exe is located)"
    $inputDir = $inputDir.Trim('"').Trim()
    if (Test-Path (Join-Path $inputDir "FanControl.Plugins.dll")) {
        $FanControlDir = $inputDir
    } else {
        throw "FanControl.Plugins.dll was not found in: $inputDir"
    }
}

Write-Host "[1/4] Found FanControl at: $FanControlDir" -ForegroundColor Green

# 2. Create isolated temporary build folder in %TEMP% (cleaned up automatically)
$TempBuildDir = Join-Path ([System.IO.Path]::GetTempPath()) "FanControl_PumpPid_Temp_$PID"
if (Test-Path $TempBuildDir) {
    Remove-Item -LiteralPath $TempBuildDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $TempBuildDir -Force | Out-Null

try {
    Write-Host "[2/4] Staging repository source files into %TEMP%..." -ForegroundColor Cyan

    foreach ($srcFile in $RequiredSourceFiles) {
        Copy-Item -Path (Join-Path $RepoDir $srcFile) -Destination (Join-Path $TempBuildDir $srcFile) -Force
    }
    Copy-Item -Path (Join-Path $FanControlDir "FanControl.Plugins.dll") -Destination (Join-Path $TempBuildDir "FanControl.Plugins.dll") -Force

    # 3. Compile Plugin DLL and GUI Configurator inside %TEMP%
    Write-Host "[3/4] Compiling FanControl.PumpPid.dll and PumpPidConfigurator.exe..." -ForegroundColor Cyan
    Push-Location $TempBuildDir
    try {
        dotnet build "FanControl.PumpPid.csproj" -c Release "/p:FanControlDir=$FanControlDir" "/p:AutoCopyToPlugins=false"
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }

    $BuiltDll = Get-ChildItem -Path (Join-Path $TempBuildDir "bin\Release") -Filter "FanControl.PumpPid.dll" -Recurse | Select-Object -First 1
    if (-not $BuiltDll) {
        throw "Failed to locate compiled FanControl.PumpPid.dll"
    }

    $cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path $cscPath)) {
        $cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
    }

    $TempGuiCs = Join-Path $TempBuildDir "PumpPidConfigurator.cs"
    $TempConfigCs = Join-Path $TempBuildDir "PumpPidConfig.cs"
    $TempLoggerCs = Join-Path $TempBuildDir "PumpPidEventLogger.cs"
    $TempGuiExe = Join-Path $TempBuildDir "PumpPidConfigurator.exe"
    if (Test-Path $cscPath) {
        foreach ($csPath in @($TempGuiCs, $TempConfigCs, $TempLoggerCs)) {
            $csContent = Get-Content -Path $csPath -Raw -Encoding UTF8
            [System.IO.File]::WriteAllText($csPath, $csContent, (New-Object System.Text.UTF8Encoding($true)))
        }
        & $cscPath /nologo /target:winexe /platform:anycpu /optimize+ "/out:$TempGuiExe" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $TempGuiCs $TempConfigCs $TempLoggerCs
        if ($LASTEXITCODE -ne 0) {
            throw "csc.exe failed to compile PumpPidConfigurator.exe (exit code $LASTEXITCODE)"
        }
    }

    # 4. Copy ONLY the 3 finished files into FanControl\Plugins
    $PluginsDir = Join-Path $FanControlDir "Plugins"
    if (-not (Test-Path $PluginsDir)) {
        New-Item -ItemType Directory -Path $PluginsDir -Force | Out-Null
    }

    $WrongStubInPlugins = Join-Path $PluginsDir "FanControl.Plugins.dll"
    if (Test-Path $WrongStubInPlugins) {
        Remove-Item -Path $WrongStubInPlugins -Force -ErrorAction SilentlyContinue
    }

    Stop-Process -Name "PumpPidConfigurator" -Force -ErrorAction SilentlyContinue

    try {
        Copy-Item -Path $BuiltDll.FullName -Destination (Join-Path $PluginsDir "FanControl.PumpPid.dll") -Force
    } catch {
        Write-Host "[!] FanControl is currently locking FanControl.PumpPid.dll. Closing FanControl temporarily to update..." -ForegroundColor Yellow
        Stop-Process -Name "FanControl" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
        Copy-Item -Path $BuiltDll.FullName -Destination (Join-Path $PluginsDir "FanControl.PumpPid.dll") -Force
    }

    if (Test-Path $TempGuiExe) {
        Copy-Item -Path $TempGuiExe -Destination (Join-Path $PluginsDir "PumpPidConfigurator.exe") -Force
    }

    $DestJson = Join-Path $PluginsDir "PumpPidConfig.json"
    if (-not (Test-Path $DestJson)) {
        Copy-Item -Path (Join-Path $TempBuildDir "PumpPidConfig.json") -Destination $DestJson -Force
    }

    Get-ChildItem -LiteralPath $PluginsDir -Filter "*PumpPid*" | Unblock-File -ErrorAction SilentlyContinue

    $InstalledExe = Join-Path $PluginsDir "PumpPidConfigurator.exe"
    $ShortcutPath = $null
    if (Test-Path $InstalledExe) {
        try {
            $DesktopDir = [Environment]::GetFolderPath("Desktop")
            if ([string]::IsNullOrWhiteSpace($DesktopDir) -or -not (Test-Path $DesktopDir)) {
                $DesktopDir = Join-Path $env:USERPROFILE "Desktop"
            }
            if (Test-Path $DesktopDir) {
                $ShortcutPath = Join-Path $DesktopDir "Pump PID Configurator.lnk"
                $WshShell = New-Object -ComObject WScript.Shell
                $Shortcut = $WshShell.CreateShortcut($ShortcutPath)
                $Shortcut.TargetPath = $InstalledExe
                $Shortcut.Arguments = '"' + $PluginsDir + '"'
                $Shortcut.WorkingDirectory = $PluginsDir
                $Shortcut.Description = "FanControl Pump PID Controller & Oscilloscope Configurator"
                $Shortcut.IconLocation = "$InstalledExe,0"
                $Shortcut.Save()
            }
        } catch {
            Write-Host "[!] Could not create Desktop shortcut: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    Write-Host "[4/4] Installed cleanly into: $PluginsDir" -ForegroundColor Green
    Write-Host "      - FanControl.PumpPid.dll" -ForegroundColor White
    Write-Host "      - PumpPidConfigurator.exe (RU / EN)" -ForegroundColor White
    Write-Host "      - PumpPidConfig.json" -ForegroundColor White
    if ($ShortcutPath -and (Test-Path $ShortcutPath)) {
        Write-Host "      - Desktop Shortcut: $ShortcutPath" -ForegroundColor Cyan
    }
    Write-Host ""
    Write-Host "==================================================================" -ForegroundColor Green
    Write-Host " INSTALLATION COMPLETE! (Temporary build files removed)" -ForegroundColor Green
    Write-Host "==================================================================" -ForegroundColor Green

    if (Test-Path $InstalledExe) {
        Start-Process -FilePath $InstalledExe -ArgumentList ('"' + $PluginsDir + '"')
    }
} finally {
    # Always remove temporary build directory so no intermediate build files remain
    if (Test-Path $TempBuildDir) {
        Remove-Item -LiteralPath $TempBuildDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

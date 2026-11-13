@echo off
setlocal
rem Starts the desktop app from this checkout (dev build).
rem The helper programs come from .devinstall\resources (bin\, core\, roles\).
rem Set YAWBLE_DATA_DIR before running to open a particular data folder.

set "REPO=%~dp0.."
set "SHELL_DIR=%REPO%\shell"
set "ELECTRON=%SHELL_DIR%\node_modules\electron\dist\electron.exe"
set "YAWBLE_INSTALL_DIR=%REPO%\.devinstall\resources"

if not exist "%ELECTRON%" (
  echo Electron not found at "%ELECTRON%". Run "npm install" in "%SHELL_DIR%" first.
  pause
  exit /b 1
)

if not exist "%SHELL_DIR%\out\main" (
  echo Building the shell...
  pushd "%SHELL_DIR%"
  call npm run build
  if errorlevel 1 (
    popd
    echo Shell build failed.
    pause
    exit /b 1
  )
  popd
)

start "" "%ELECTRON%" "%SHELL_DIR%"
endlocal

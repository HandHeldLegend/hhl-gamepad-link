@echo off
:: Builds HHL Gamepad WLAN into build\release\HhlGamepadWlan.exe (one portable file).
::
:: Needs Visual Studio 2022 with the C++ x64 tools and the Windows SDK 10.0.26100. Everything else
:: (the .NET 10 SDK and the Windows Driver Kit package) is fetched into .tools\ by
:: scripts\fetch-tools.ps1; set HHL_TOOLS to keep them somewhere else.
setlocal
set "ROOT=%~dp0"
if not defined HHL_TOOLS set "HHL_TOOLS=%ROOT%.tools"
set "HM=%ROOT%external\HIDMaestro"
set "OUT=%ROOT%build"

if not exist "%HM%\scripts\build.cmd" (
    echo Fetching HIDMaestro...
    git -C "%ROOT%." submodule update --init || exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\fetch-tools.ps1" -Tools "%HHL_TOOLS%" || exit /b 1

set "DOTNET_ROOT=%HHL_TOOLS%\dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"
set "DOTNET_NOLOGO=1"
set "PATH=%DOTNET_ROOT%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

:: HIDMaestro with our changes (patches\hidmaestro-hhl.patch): WDK from the package, x64 only (the
:: USB/IP transport too), the GameCube adapter profile, and joy.cpl's name cache left alone
git -C "%HM%" apply --reverse --check "%ROOT%patches\hidmaestro-hhl.patch" 2>nul || (
    git -C "%HM%" apply "%ROOT%patches\hidmaestro-hhl.patch" || exit /b 1
)
set "HM_WDK_ROOT=%HHL_TOOLS%\wdk\c"
call "%HM%\scripts\build.cmd" x64 || exit /b 1
call "%HM%\scripts\build_companion.cmd" x64 || exit /b 1
set "HMPROPS=-p:HMSkipArm64=true -p:HMInf2catBin=%HHL_TOOLS%\inf2cat\"
:: Twice: the first pass stages the driver, the second embeds it (see HIDMaestro's build_all.cmd)
dotnet build "%HM%\sdk\HIDMaestro.Core\HIDMaestro.Core.csproj" -c Release -v minimal %HMPROPS% || exit /b 1
dotnet build "%HM%\sdk\HIDMaestro.Core\HIDMaestro.Core.csproj" -c Release -v minimal %HMPROPS% || exit /b 1

:: The window
call "%ROOT%native\build.cmd" "%OUT%\native" || exit /b 1

:: The app, compiled ahead of time into one .exe
dotnet publish "%ROOT%src\HhlGamepadWlan.csproj" -c Release -r win-x64 -o "%OUT%\release" ^
    -p:HIDMaestroDir="%HM%\sdk\HIDMaestro.Core\bin\Release\net10.0-windows10.0.26100.0\\" ^
    -p:NativeUiLib="%OUT%\native\hhl_ui.lib" || exit /b 1
del /q "%OUT%\release\*.pdb" 2>nul
copy /y "%ROOT%THIRD-PARTY-NOTICES.txt" "%OUT%\release\" >nul
echo.
echo Built %OUT%\release\HhlGamepadWlan.exe

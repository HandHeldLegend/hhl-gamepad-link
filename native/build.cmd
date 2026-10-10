@echo off
:: Builds hhl_ui.lib (the window: hhl_ui.cpp with Dear ImGui and its Win32 + Direct3D 11 backends),
:: which the app links into its single .exe. /MT matches NativeAOT's C runtime.
::   %1: output folder (default: native\build)
setlocal
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%~dp0build"
set "IMGUI=%~dp0imgui"
if not exist "%OUT%" mkdir "%OUT%"

set "VCVARS="
for /d %%A in ("C:\Program Files\Microsoft Visual Studio\*") do (
    for /d %%B in ("%%A\*") do (
        if exist "%%B\VC\Auxiliary\Build\vcvarsall.bat" set "VCVARS=%%B\VC\Auxiliary\Build\vcvarsall.bat"
    )
)
if not defined VCVARS (
    echo ERROR: Visual Studio C++ build tools are required.
    exit /b 1
)
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%VCVARS%" x64 >nul || exit /b 1

cl /nologo /c /O2 /MT /EHsc /std:c++17 /DNDEBUG /DUNICODE /D_UNICODE ^
    /I"%IMGUI%" /I"%IMGUI%\backends" /Fo"%OUT%\\" ^
    "%~dp0hhl_ui.cpp" ^
    "%IMGUI%\imgui.cpp" "%IMGUI%\imgui_draw.cpp" "%IMGUI%\imgui_tables.cpp" "%IMGUI%\imgui_widgets.cpp" ^
    "%IMGUI%\backends\imgui_impl_win32.cpp" "%IMGUI%\backends\imgui_impl_dx11.cpp" || exit /b 1

lib /nologo /OUT:"%OUT%\hhl_ui.lib" "%OUT%\*.obj" || exit /b 1
echo Built %OUT%\hhl_ui.lib

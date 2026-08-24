@echo off
REM ============================================================
REM WE Tool 精简版 libSkiaSharp.dll 复现构建(Windows x64)
REM 前置:VS(含 Clang 组件)+ gn(自编,参见 gn-src)+ ninja
REM 用法: 修改下方路径后运行,产物在 skia-engine/out/min-gif/
REM ============================================================
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
set PATH=C:\Users\lijun\Documents\Projects\gn-src\out;C:\Users\lijun\Documents\Projects\tools\bin;C:\Users\lijun\AppData\Roaming\uv\python\cpython-3.11.15-windows-x86_64-none;%PATH%
cd /d C:\Users\lijun\Documents\Projects\skia-engine
echo [1/2] gn gen...
gn gen out/min-gif
if errorlevel 1 exit /b 1
echo [2/2] ninja build...
ninja -C out/min-gif SkiaSharp
if errorlevel 1 exit /b 1
echo done: out\min-gif\libSkiaSharp.dll

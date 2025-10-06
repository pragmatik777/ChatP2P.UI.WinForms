@echo off
echo 🗑️ Clearing ChatP2P logs from all locations...
echo.

REM Clear VM1 logs
echo Clearing VM1 logs...
if exist "\\VM1\Users\User\Desktop\ChatP2P_Logs\*.*" (
    del /Q "\\VM1\Users\User\Desktop\ChatP2P_Logs\*.*" 2>nul
    echo ✅ VM1 logs cleared
) else (
    echo ⚠️ VM1 logs path not accessible or empty
)

REM Clear VM3 logs
echo Clearing VM3 logs...
if exist "\\VM3\ChatP2P_Logs\*.*" (
    del /Q "\\VM3\ChatP2P_Logs\*.*" 2>nul
    echo ✅ VM3 logs cleared
) else (
    echo ⚠️ VM3 logs path not accessible or empty
)

REM Clear local logs
echo Clearing local logs...
if exist "C:\Users\pragm\OneDrive\Bureau\ChatP2P_Logs\*.*" (
    del /Q "C:\Users\pragm\OneDrive\Bureau\ChatP2P_Logs\*.*" 2>nul
    echo ✅ Local logs cleared
) else (
    echo ⚠️ Local logs path not accessible or empty
)

echo.
echo 🎯 Log cleanup completed!
pause
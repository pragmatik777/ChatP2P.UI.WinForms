@echo off
set BASEDIR=C:\Users\pragm\source\repos\ChatP2P.UI.WinForms
set SRC_WINFORMS=%BASEDIR%\ChatP2P.UI.WinForms\bin\Debug\net8.0-windows10.0.17763
set SRC_SERVER=%BASEDIR%\ChatP2P.Server\bin\Debug\net8.0
set SRC_CLIENT=%BASEDIR%\ChatP2P.Client\bin\Debug\net8.0-windows
set DEST1=\\VM1\projchat
set DEST2=\\VM2\projchat

echo === DÉPLOIEMENT CHATP2P SUR VMS ===
echo.

echo --- Copie WinForms vers %DEST1% ---
robocopy "%SRC_WINFORMS%" "%DEST1%\WinForms" /MIR /NFL /NDL /NP /R:1 /W:1

echo --- Copie Server vers %DEST1% ---
robocopy "%SRC_SERVER%" "%DEST1%\Server" /MIR /NFL /NDL /NP /R:1 /W:1

echo --- Copie Client WPF vers %DEST1% ---
robocopy "%SRC_CLIENT%" "%DEST1%\Client" /MIR /NFL /NDL /NP /R:1 /W:1
echo 192.168.1.152 > "%DEST1%\Client\server.txt"

echo.
echo --- Copie WinForms vers %DEST2% ---
robocopy "%SRC_WINFORMS%" "%DEST2%\WinForms" /MIR /NFL /NDL /NP /R:1 /W:1

echo --- Copie Server vers %DEST2% ---
robocopy "%SRC_SERVER%" "%DEST2%\Server" /MIR /NFL /NDL /NP /R:1 /W:1

echo --- Copie Client WPF vers %DEST2% ---
robocopy "%SRC_CLIENT%" "%DEST2%\Client" /MIR /NFL /NDL /NP /R:1 /W:1
echo 192.168.1.152 > "%DEST2%\Client\server.txt"

echo.
echo === DÉPLOIEMENT TERMINÉ ===
echo.
echo Sur VM1/VM2 tu peux maintenant lancer :
echo   WinForms\ChatP2P.UI.WinForms.exe    (ton interface actuelle)
echo   Server\ChatP2P.Server.exe           (serveur console)
echo   Client\ChatP2P.Client.exe           (nouvelle interface WPF)
echo.
echo IP du serveur configurée automatiquement: 192.168.1.152
echo.
pause

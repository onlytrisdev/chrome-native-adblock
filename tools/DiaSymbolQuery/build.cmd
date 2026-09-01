@echo off
setlocal
set "VSROOT=C:\Program Files\Microsoft Visual Studio\18\Community"
call "%VSROOT%\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 >nul
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++20 /EHsc /W4 /O2 /DUNICODE /D_UNICODE ^
  /I"%VSROOT%\DIA SDK\include" DiaSymbolQuery.cpp ^
  /link /out:DiaSymbolQuery.exe ^
  /libpath:"%VSROOT%\DIA SDK\lib\amd64" diaguids.lib ole32.lib oleaut32.lib advapi32.lib

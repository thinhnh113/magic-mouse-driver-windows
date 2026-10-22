@echo off
rem Builds MagicTouchFilter.sys with VS Build Tools + WDK 10.0.26100 (no full Visual Studio needed),
rem then stamps the INF, creates the catalog and test-signs both.
rem Output: driver\MagicTouchFilter\out\
setlocal
set HERE=%~dp0
set WDK=%ProgramFiles(x86)%\Windows Kits\10
set VER=10.0.26100.0
set KMDF=1.15
set OUT=%HERE%out
set OBJ=%HERE%obj

call "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OBJ%" mkdir "%OBJ%"

set INC=/I"%WDK%\Include\%VER%\km" /I"%WDK%\Include\%VER%\shared" /I"%WDK%\Include\%VER%\km\crt" /I"%WDK%\Include\wdf\kmdf\%KMDF%"
set DEFS=/D_WIN64 /D_AMD64_ /DAMD64 /DNTDDI_VERSION=0x0A000008 /D_WIN32_WINNT=0x0A00 /DWINVER=0x0A00 /DKMDF_VERSION_MAJOR=1 /DKMDF_VERSION_MINOR=15 /DPOOL_NX_OPTIN=1 /DNDEBUG

cl /nologo /c /kernel /Zi /W4 /WX- /O2 /GS /Gy /Zp8 /Gz /guard:cf /Qspectre- /wd4201 /wd4214 %DEFS% %INC% /Fo"%OBJ%\Driver.obj" /Fd"%OBJ%\vc.pdb" "%HERE%Driver.c" || exit /b 1

set LIBK=%WDK%\Lib\%VER%\km\x64
set LIBW=%WDK%\Lib\wdf\kmdf\x64\%KMDF%
link /nologo /DRIVER /SUBSYSTEM:NATIVE,10.00 /ENTRY:FxDriverEntry /NODEFAULTLIB /INCREMENTAL:NO /DEBUG /OPT:REF /OPT:ICF ^
  /MERGE:_TEXT=.text;_PAGE=PAGE /SECTION:INIT,d /guard:cf /INTEGRITYCHECK ^
  /OUT:"%OUT%\MagicTouchFilter.sys" /PDB:"%OUT%\MagicTouchFilter.pdb" "%OBJ%\Driver.obj" ^
  "%LIBW%\WdfDriverEntry.lib" "%LIBW%\WdfLdr.lib" "%LIBK%\ntoskrnl.lib" "%LIBK%\hal.lib" "%LIBK%\wmilib.lib" ^
  "%LIBK%\BufferOverflowFastFailK.lib" "%LIBK%\usbd.lib" "%LIBK%\ntstrsafe.lib" || exit /b 1

rem Test certificate (created once in CurrentUser\My; the .cer is what the target machine must trust)
set CERT=%OUT%\MagicTouchTest.cer
set SIGN="%WDK%\bin\%VER%\x64\signtool.exe" sign /fd sha256 /s My /n "MagicTouch Test"
powershell -NoProfile -Command "$c = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=MagicTouch Test' | Select-Object -First 1; if (-not $c) { $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=MagicTouch Test' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10) }; Export-Certificate -Cert $c -FilePath '%CERT%' | Out-Null" || exit /b 1

rem 1) sign the .sys  2) stamp INF + build catalog over the signed .sys  3) sign the catalog
%SIGN% "%OUT%\MagicTouchFilter.sys" || exit /b 1
copy /y "%HERE%MagicTouchFilter.inf" "%OUT%\MagicTouchFilter.inf" >nul
rem DriverVer date in UTC: Inf2Cat rejects a "future" date, which a local date is just after midnight east of UTC.
rem Version from the same UTC clock (yy.MMdd.HHmm.ss): it must only ever grow, or Windows keeps an installed
rem driver with the same date and a higher version ("-v *" uses local time, which goes backwards vs UTC dates).
for /f "tokens=1,2" %%a in ('powershell -NoProfile -Command "$u=[DateTime]::UtcNow; $u.ToString('MM/dd/yyyy') + ' ' + $u.ToString('yy.MMdd.HHmm.ss')"') do (
  set DRVDATE=%%a
  set DRVVER=%%b
)
"%WDK%\bin\%VER%\x64\stampinf.exe" -f "%OUT%\MagicTouchFilter.inf" -d %DRVDATE% -a amd64 -v %DRVVER% -k %KMDF% >nul || exit /b 1
"%WDK%\bin\%VER%\x86\Inf2Cat.exe" /driver:"%OUT%" /os:10_X64,10_VB_X64,10_NI_X64 || exit /b 1
%SIGN% "%OUT%\MagicTouchFilter.cat" || exit /b 1
echo Built and test-signed: %OUT%





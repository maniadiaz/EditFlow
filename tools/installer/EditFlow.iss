; SPDX-FileCopyrightText: 2026 maniadiaz
; SPDX-License-Identifier: GPL-3.0-or-later
;
; Instalador de Windows para EditFlow.
;
; Empaqueta la aplicación ya publicada (self-contained: no hace falta tener .NET
; instalado) junto con FFmpeg. whisper.cpp, llama.cpp y sus modelos NO van dentro:
; se descargan la primera vez que se usan los subtítulos o la traducción, y meterlos
; aquí multiplicaría el tamaño para algo que mucha gente no va a tocar.
;
; Se construye desde la raíz del repositorio:
;
;     tools\installer\build.cmd 0.6.0
;
; que publica la aplicación y llama a ISCC con lo que haga falta.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish\win-x64"
#endif

#ifndef FFmpegDir
  #define FFmpegDir "..\ffmpeg\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif

#define AppName "EditFlow"
#define AppPublisher "maniadiaz"
#define AppUrl "https://github.com/maniadiaz/EditFlow"
#define AppExe "EditFlow.exe"

[Setup]
AppId={{8E5C6F2A-4B7D-4E19-9C3A-1D6F0B2E7A54}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Instalación por usuario, en %LOCALAPPDATA%: no pide permisos de administrador.
; Un editor de video no necesita tocar nada del sistema, y pedir elevación solo
; añadiría una puerta más que cruzar —y un aviso más que ignorar— para nada.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=EditFlow-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\EditFlow.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

; La mayor parte de lo que se empaqueta son binarios nativos que comprimen bien.
; 'solid' mejora bastante la ratio a costa de más memoria al comprimir, que solo
; sufre quien construye el instalador, no quien lo ejecuta.
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
ShowLanguageDialog=auto

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
es.CreateDesktopIcon=Crear un acceso directo en el escritorio
es.AssociateProjects=Abrir los archivos .editflow con EditFlow
es.LaunchApp=Abrir EditFlow
es.CheckingFFmpeg=Comprobando que FFmpeg funciona...
es.FFmpegBlocked=La instalación ha terminado, pero FFmpeg NO PUEDE EJECUTARSE en este equipo.%n%nLos archivos están completos: es el Control inteligente de aplicaciones (Smart App Control) de Windows 11 el que los bloquea por no llevar firma digital. EditFlow no podrá exportar ni reproducir vídeo hasta que se resuelva.%n%nCómo se resuelve, y por qué desactivarlo es IRREVERSIBLE, está explicado en:%n%n%1%n%nSe abrirá al cerrar este aviso.
es.FFmpegBroken=La instalación ha terminado, pero FFmpeg NO PUEDE EJECUTARSE en este equipo.%n%nNo parece un bloqueo de Windows, así que lo más probable es que los archivos hayan llegado dañados. Vuelve a descargar el instalador y comprueba su SHA-256.%n%nPara ver el diagnóstico completo:%n%n%1
en.CreateDesktopIcon=Create a desktop shortcut
en.AssociateProjects=Open .editflow files with EditFlow
en.LaunchApp=Launch EditFlow
en.CheckingFFmpeg=Checking that FFmpeg works...
en.FFmpegBlocked=Setup finished, but FFmpeg CANNOT RUN on this machine.%n%nThe files are complete: Windows 11 Smart App Control is blocking them because they are not digitally signed. EditFlow will not be able to export or play video until this is resolved.%n%nHow to resolve it, and why turning it off is IRREVERSIBLE, is explained in:%n%n%1%n%nIt will open when you close this message.
en.FFmpegBroken=Setup finished, but FFmpeg CANNOT RUN on this machine.%n%nThis does not look like a Windows block, so the files most likely arrived damaged. Download the installer again and verify its SHA-256.%n%nFor the full diagnosis:%n%n%1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associate"; Description: "{cm:AssociateProjects}"

[Files]
; La aplicación publicada, con sus subcarpetas (libvlc y demás).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; FFmpeg va en {app}\ffmpeg, que es el primer sitio donde FFmpegLocator busca.
Source: "{#FFmpegDir}\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion

; El diagnóstico viaja con la instalación a propósito: quien se encuentra con que la
; aplicación no abre normalmente no tiene el repositorio clonado, y es justo entonces
; cuando hace falta. Desde {app}\tools encuentra {app}\ffmpeg por su cuenta.
Source: "..\check-ffmpeg.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\check-ffmpeg.cmd"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\..\docs\SMART-APP-CONTROL.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; La asociación se escribe bajo HKCU porque la instalación es por usuario. La aplicación
; ya sabe abrir un .editflow que le llegue como argumento (Program.StartupFiles).
Root: HKCU; Subkey: "Software\Classes\.editflow"; ValueType: string; ValueName: ""; ValueData: "EditFlow.Project"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\EditFlow.Project"; ValueType: string; ValueName: ""; ValueData: "Proyecto de EditFlow"; Flags: uninsdeletekey; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\EditFlow.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\EditFlow.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: associate

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
// Que los archivos estén copiados no significa que se puedan ejecutar. Ya pasó: una
// versión publicada instalaba FFmpeg correctamente —hash incluido— y la aplicación
// moría al abrirse con un 0xc0e90002 que no explicaba nada. La comprobación va aquí
// para que el aviso llegue en el momento en que se puede entender, y no después.
//
// No aborta la instalación: el resto de EditFlow sí queda instalado y utilizable,
// y el problema puede resolverse sin desinstalar nada.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Script, Doc, Tool: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  Script := ExpandConstant('{app}\tools\check-ffmpeg.ps1');
  Doc    := ExpandConstant('{app}\docs\SMART-APP-CONTROL.md');
  Tool   := ExpandConstant('{app}\tools\check-ffmpeg.cmd');

  WizardForm.StatusLabel.Caption := ExpandConstant('{cm:CheckingFFmpeg}');

  if not Exec('powershell.exe',
              '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '"',
              '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Log('check-ffmpeg: no se pudo ejecutar; se omite la comprobación.');
    Exit;   // No se pudo ni lanzar la comprobación: no es motivo para alarmar.
  end;

  Log('check-ffmpeg: codigo de salida ' + IntToStr(Code));

  // 3 = bloqueo del Control de aplicaciones. 4 = binario roto. 0 = todo bien.
  if Code = 3 then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('FFmpegBlocked'), [Doc]),
                       mbCriticalError, MB_OK, IDOK);
    if not WizardSilent then
      ShellExec('open', Doc, '', '', SW_SHOWNORMAL, ewNoWait, Code);
  end
  else if Code <> 0 then
    SuppressibleMsgBox(FmtMessage(CustomMessage('FFmpegBroken'), [Tool]),
                       mbCriticalError, MB_OK, IDOK);
end;

[UninstallDelete]
; Las cachés se generan al usar la aplicación, así que el instalador no las conoce:
; hay que borrarlas explícitamente o quedarían huérfanas. Todas se pueden rehacer a
; partir del material del usuario, así que borrarlas no cuesta nada.
;
; Lo que NO se borra, a propósito:
;   · settings.json y recent-projects.json, que ocupan nada y devuelven las
;     preferencias intactas si se reinstala;
;   · la carpeta 'speech', con los modelos de Whisper y de traducción, que pesan
;     cientos de MB y volver a descargarlos lleva un buen rato. Quien quiera el disco
;     limpio del todo puede borrarla a mano; se dice en el README.
;
; Los proyectos y los videos del usuario no se tocan nunca, estén donde estén.
Type: filesandordirs; Name: "{localappdata}\EditFlow\filmstrips"
Type: filesandordirs; Name: "{localappdata}\EditFlow\waveforms"
Type: filesandordirs; Name: "{localappdata}\EditFlow\proxies"
Type: filesandordirs; Name: "{localappdata}\EditFlow\thumbs"
Type: filesandordirs; Name: "{localappdata}\EditFlow\covers"
Type: filesandordirs; Name: "{%TEMP}\editflow-preview"
Type: filesandordirs; Name: "{%TEMP}\editflow-preview-cache"
Type: filesandordirs; Name: "{%TEMP}\editflow-ovframes"

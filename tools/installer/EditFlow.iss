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
en.CreateDesktopIcon=Create a desktop shortcut
en.AssociateProjects=Open .editflow files with EditFlow
en.LaunchApp=Launch EditFlow

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associate"; Description: "{cm:AssociateProjects}"

[Files]
; La aplicación publicada, con sus subcarpetas (libvlc y demás).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; FFmpeg va en {app}\ffmpeg, que es el primer sitio donde FFmpegLocator busca.
Source: "{#FFmpegDir}\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion

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

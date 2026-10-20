; Instalador de Pantallazo (Inno Setup 6). Lo compila scripts\publish.ps1:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=<dist\Pantallazo> /DOutputDir=<dist> installer\Pantallazo.iss
; Instala solo para el usuario actual (sin administrador) en %LOCALAPPDATA%\Programs\Pantallazo.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Pantallazo"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6E0C2F4B-5B8E-4C1F-9A2D-3F7E1B6C9D40}
AppName=Pantallazo
AppVersion={#AppVersion}
AppVerName=Pantallazo {#AppVersion}
AppPublisher=Pantallazo
AppComments=Capturas y grabación de pantalla
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Instalador de Pantallazo
DefaultDirName={autopf}\Pantallazo
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Pantallazo-{#AppVersion}-instalador
SetupIconFile=..\src\Pantallazo\Assets\pantallazo.ico
UninstallDisplayIcon={app}\Pantallazo.exe
UninstallDisplayName=Pantallazo
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Si Pantallazo está abierto, el instalador y el desinstalador le piden que se cierre ("Pantallazo.exe --salir",
; que guarda una grabación en curso antes de salir). Ver [Code]. No se usa AppMutex porque solo sabe preguntar.
CloseApplications=no
RestartApplications=no
; Al terminar, avisar a Windows que cambiaron los íconos (SHCNE_ASSOCCHANGED). Sin esto, el Explorador seguía
; mostrando en la barra de tareas el ícono de la versión anterior que tenía en memoria.
ChangesAssociations=yes

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
es.TaskDesktop=Crear un ícono en el escritorio
es.TaskStartup=Iniciar Pantallazo con Windows (en segundo plano, en la bandeja)
es.TaskPrintScreen=Liberar la tecla Impr Pant (Windows 11 se la da a la Herramienta Recortes)
es.GroupOptions=Opciones:
es.LaunchNow=Abrir Pantallazo ahora

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:GroupOptions}"; Flags: unchecked
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:GroupOptions}"; Flags: unchecked
Name: "printscreen"; Description: "{cm:TaskPrintScreen}"; GroupDescription: "{cm:GroupOptions}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Pantallazo"; Filename: "{app}\Pantallazo.exe"; Comment: "Capturas y grabación de pantalla"
Name: "{autodesktop}\Pantallazo"; Filename: "{app}\Pantallazo.exe"; Tasks: desktopicon

[Registry]
; Mismo valor que escribe la app desde Configuración → "Iniciar con Windows".
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Pantallazo"; \
    ValueData: """{app}\Pantallazo.exe"" --minimizado"; Flags: uninsdeletevalue; Tasks: startup
; Igual que el instalador de ShareX: Impr Pant deja de abrir Recortes (se deshace al desinstalar o desde la app).
Root: HKCU; Subkey: "Control Panel\Keyboard"; ValueType: dword; ValueName: "PrintScreenKeyForSnippingEnabled"; \
    ValueData: 0; Flags: uninsdeletevalue; Tasks: printscreen

[Run]
Filename: "{app}\Pantallazo.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Último recurso si la app no respondió al pedido de salida (InitializeUninstall).
Filename: "{sys}\taskkill.exe"; Parameters: "/im Pantallazo.exe /f"; Flags: runhidden; RunOnceId: "CerrarPantallazo"

[Code]
const
  AppMutexName = 'Local\Pantallazo-instancia-unica';

{ Pide a la instancia abierta que salga (la app espera hasta 20 s a que termine). true = ya no está abierta. }
function CloseRunningApp(): Boolean;
var
  Exe: String;
  Code: Integer;
begin
  Result := not CheckForMutexes(AppMutexName);
  if Result then Exit;
  Exe := ExpandConstant('{app}\Pantallazo.exe');
  if FileExists(Exe) then
    Exec(Exe, '--salir', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := not CheckForMutexes(AppMutexName);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseRunningApp() then
    Result := 'Pantallazo está abierto y no se pudo cerrar solo. Cerralo desde el ícono de la bandeja (menú → Salir) y volvé a intentar.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not CloseRunningApp() then
    Log('Pantallazo no respondió al pedido de salida; se cierra a la fuerza.');
end;

{ El valor de "Iniciar con Windows" puede haberlo creado la app (Configuración) y no el instalador: se borra igual. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Exe: String;
  I: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Pantallazo');
    { Windows mantiene bloqueado el .exe un instante después de que la app cierra: se reintenta hasta 5 s.
      La carpeta se quita solo si quedó vacía (nunca se borra una carpeta entera). }
    Exe := ExpandConstant('{app}\Pantallazo.exe');
    I := 0;
    while FileExists(Exe) and (I < 20) do
    begin
      DeleteFile(Exe);
      if FileExists(Exe) then Sleep(250);
      I := I + 1;
    end;
    RemoveDir(ExpandConstant('{app}'));
  end;
end;

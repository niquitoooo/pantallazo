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
; La tarea "printscreen" (liberar Impr Pant, como el instalador de ShareX) va en [Code]: antes anota cómo estaba.

[Run]
Filename: "{app}\Pantallazo.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Último recurso si la app no respondió al pedido de salida (InitializeUninstall).
Filename: "{sys}\taskkill.exe"; Parameters: "/im Pantallazo.exe /f"; Flags: runhidden; RunOnceId: "CerrarPantallazo"

[Code]
const
  AppMutexName = 'Local\Pantallazo-instancia-unica';
  KeyboardKey = 'Control Panel\Keyboard';
  SnippingValue = 'PrintScreenKeyForSnippingEnabled';
  { Igual que Hotkeys\PrintScreenKey.cs: cómo tenía Windows Impr Pant antes de Pantallazo (0 o 1, o 2 si no existía). }
  RecordKey = 'Software\Pantallazo';
  RecordValue = 'ImprPantOriginal';
  OriginalMissing = 2;
  { Clave de desinstalación de esta app (AppId de [Setup]): de ahí sale la versión que había antes. }
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E0C2F4B-5B8E-4C1F-9A2D-3F7E1B6C9D40}_is1';

var
  PreviousVersion: String; { '' si Pantallazo no estaba instalado }

function InitializeSetup(): Boolean;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, UninstallKey, 'DisplayVersion', PreviousVersion) then
    PreviousVersion := '';
  Result := True;
end;

{ Anota cómo tenía Windows Impr Pant antes de que Pantallazo la toque. Solo la primera vez: después no se pisa.
  La 1.0.0 no anotaba: al actualizar desde ella con la tecla liberada (0), ese 0 lo puso Pantallazo y lo original era
  lo predeterminado de Windows. }
procedure RememberPrintScreen();
var
  Current, Original: Cardinal;
begin
  if RegValueExists(HKEY_CURRENT_USER, RecordKey, RecordValue) then Exit;
  if not RegQueryDWordValue(HKEY_CURRENT_USER, KeyboardKey, SnippingValue, Current) then
    Original := OriginalMissing
  else if Current <> 0 then
    Original := 1
  else if PreviousVersion = '1.0.0' then
    Original := OriginalMissing
  else
    Original := 0;
  RegWriteDWordValue(HKEY_CURRENT_USER, RecordKey, RecordValue, Original);
  Log(Format('Impr Pant antes de Pantallazo: anotación %d.', [Original]));
end;

{ Al desinstalar: si Impr Pant sigue liberada (0), la deja como estaba antes de Pantallazo; si alguien ya la devolvió a
  Recortes, se respeta. Sin anotación, vuelve a lo predeterminado de Windows (en Windows 11, Recortes).
  Si antes no había valor, se escribe lo predeterminado en vez de borrarlo: borrado, Windows sigue con el 0 que leyó
  hasta cerrar sesión. Es lo mismo que PrintScreenKey.RestoreOriginal en la app. }
procedure RestorePrintScreen();
var
  Current, Original, Value: Cardinal;
  Version: TWindowsVersion;
begin
  if not RegQueryDWordValue(HKEY_CURRENT_USER, KeyboardKey, SnippingValue, Current) then Exit;
  if Current <> 0 then Exit;
  if not RegQueryDWordValue(HKEY_CURRENT_USER, RecordKey, RecordValue, Original) then
    Original := OriginalMissing;
  if Original = OriginalMissing then
  begin
    GetWindowsVersionEx(Version);
    if Version.Build >= 22000 then Value := 1 else Value := 0;
  end
  else if Original = 0 then
    Value := 0
  else
    Value := 1;
  RegWriteDWordValue(HKEY_CURRENT_USER, KeyboardKey, SnippingValue, Value);
  Log(Format('Impr Pant = %d: vuelve a como estaba antes de Pantallazo (anotación %d).', [Value, Original]));
end;

{ Con Impr Pant en pausa (Configuración de la app), el instalador no la libera: la pausa manda hasta que se reactive. }
function PrintScreenPausedInApp(): Boolean;
var
  Json: AnsiString;
begin
  Result := False;
  if LoadStringFromFile(ExpandConstant('{userappdata}\Pantallazo\settings.json'), Json) then
    Result := Pos('"PrintScreenPaused": true', Json) > 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RememberPrintScreen();
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('printscreen') then
  begin
    if PrintScreenPausedInApp() then
      Log('Impr Pant está en pausa en Pantallazo: no se libera.')
    else
      RegWriteDWordValue(HKEY_CURRENT_USER, KeyboardKey, SnippingValue, 0);
  end;
end;

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

{ El valor de "Iniciar con Windows" y el cambio de Impr Pant pueden venir de la app (Configuración) y no del instalador:
  se deshacen igual. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Exe: String;
  I: Integer;
begin
  { Impr Pant, antes de procesar el registro de desinstalación: si se instaló la 1.0.0 con "liberar Impr Pant", ahí
    quedó "borrar el valor", y con solo borrarlo Windows seguiría con el 0 hasta cerrar sesión. Escrito antes, Windows
    toma lo original al instante y el borrado posterior ya no lo cambia. }
  if CurUninstallStep = usUninstall then
  begin
    RestorePrintScreen();
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, RecordKey);
  end;
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

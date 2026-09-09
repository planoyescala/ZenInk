; --- INSTALADOR ZENINK (parte de ZenBIM · OPEN SOURCE) ---

#define MyAppName "ZenInk"
#define MyAppExeName "ZenInk.App.exe"
#define MyAppVersion "0.0.1"
#define MyAppPublisher "plano y escala"
#define MyAppURL "https://www.planoyescala.com"

; RUTAS
; Todas cuelgan de donde está este .iss, no de una ruta escrita a mano: así el
; repositorio se puede mover o clonar en otra máquina y esto sigue compilando.
#define RepoDir     SourcePath + "..\"
#define PayloadDir  RepoDir + "dist\suelto"
#define AssetsDir   RepoDir + "design\instalador"

[Setup]
; --- IDENTIDAD ---
AppId={{EE6029D7-06E0-4F66-994E-416BBB59F48C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppCopyright=© 2026 {#MyAppPublisher} — GPLv3

; --- ESTÉTICA Y BIENVENIDA ---
WizardStyle=modern

; La marca de plano y escala, no la de ZenInk, y ahora también en el banner: el
; asistente es el que hace el programa hablando, no el programa. El icono de
; ZenInk lo lleva el propio ejecutable, que es donde significa algo — y donde
; el usuario lo va a buscar después.
; Es el mismo dibujo que el instalador de ZenBIM, a propósito: dos programas de
; la misma casa se instalan igual, y quien ya instaló uno reconoce el segundo.
; Copiado al repositorio también a propósito: depender de la carpeta de otro
; proyecto se rompe solo.
;
; El banner sangra por los cuatro lados y no lleva ni título ni pie. Lo que
; dicen las palabras ya está escrito al lado, en la página de bienvenida; una
; imagen que lo repite en pequeño solo compite con ella.
SetupIconFile={#AssetsDir}\PlanoYEscala.ico

WizardImageFile={#AssetsDir}\Banner.png
WizardSmallImageFile={#AssetsDir}\Small.png
WizardImageBackColor=clWhite
WizardImageStretch=yes

DisableWelcomePage=no

; Con dos idiomas Windows elige el que coincide con el suyo y no pregunta;
; solo sale el selector cuando no hay ninguno que coincida.
ShowLanguageDialog=auto

; LICENCIA GPLv3 — la misma que va dentro del programa, sin una segunda copia
; que se pueda quedar atrás.
LicenseFile={#RepoDir}LICENSE

; --- CONFIGURACIÓN TÉCNICA ---
; A la carpeta del usuario y sin pedir administrador: en un ordenador de
; empresa eso es la diferencia entre instalarlo y tener que pedir permiso.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#RepoDir}dist
OutputBaseFilename=ZenInk_Setup_v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 10 1809, que es lo que pide el propio programa.
MinVersion=10.0.17763

; Si ZenInk está abierto, sus ficheros están en uso y la instalación fallaría a
; medias. Es el mismo mutex con el que la aplicación se reconoce a sí misma.
AppMutex=Local\ZenInk.instancia

UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; --- METADATOS ---
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=ZenInk Installer (Open Source)
VersionInfoCopyright=© 2026 {#MyAppPublisher} - Licensed under GPLv3

; --- FIRMA ---
; Sin firma, Windows no enseña el programa: enseña la pantalla azul de
; SmartScreen y lo llama «de editor desconocido». Y como no hay certificado al
; que colgar la reputación, esta se cuelga del propio archivo, así que cada
; compilación vuelve a empezar de cero por buena que fuera la anterior.
;
; Con quién se firma no vive aquí —una clave no se versiona—: `Publicar.ps1`
; pasa la orden entera con /Szenink=… y define Firmar. Sin ella esto compila
; igual y sale un instalador sin firmar, que es lo que había hasta ahora.
#ifdef Firmar
SignTool=zenink
; El desinstalador también. Lo genera el instalador, se queda en el ordenador y
; también se ejecuta; firmar solo lo que se descarga deja sin firmar lo que dura.
SignedUninstaller=yes
#endif

; --- IDIOMAS ---
; El inglés primero porque es el idioma en el que se publica; Windows elige
; solo el que coincida con el suyo, y solo pregunta cuando no hay ninguno.
; Las frases propias van en [CustomMessages], una por idioma, y se piden con
; {cm:…}: así una entrada nueva que se olvide en un idioma canta al compilar.
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"


[Messages]
; --- TEXTOS DE BIENVENIDA ---
english.WelcomeLabel1=Install ZenInk
english.WelcomeLabel2=ZenInk {#MyAppVersion} is about to be installed on this computer.%n%nZenInk is a viewer and editor for PDF drawings: annotate, compare revisions, measure on the drawing and sign. It is free software, made by plano y escala and published under the GNU General Public License v3.%n%nNothing else needs installing: everything it needs travels inside.
english.ClickNext=Press Next to accept the free licence and carry on.
spanish.WelcomeLabel1=Instalar ZenInk
spanish.WelcomeLabel2=Se va a instalar ZenInk {#MyAppVersion} en este ordenador.%n%nZenInk es un visor y editor de planos PDF: anotar, comparar revisiones, medir sobre el plano y firmar. Es software libre, hecho por plano y escala y publicado bajo la GNU General Public License v3.%n%nNo hace falta instalar nada más: todo lo que necesita va dentro.
spanish.ClickNext=Pulsa Siguiente para aceptar la licencia libre y continuar.

[CustomMessages]
english.DesktopIcon=Create a shortcut on the desktop
english.ShortcutsGroup=Shortcuts:
english.PdfAssoc=Offer ZenInk when a PDF is opened (it appears under “Open with”)
english.PdfFilesGroup=PDF files:
english.PdfDocument=PDF document
english.AppDescription=Viewer and editor for PDF drawings
english.RunApp=Open ZenInk
spanish.DesktopIcon=Crear un acceso directo en el escritorio
spanish.ShortcutsGroup=Accesos directos:
spanish.PdfAssoc=Ofrecer ZenInk al abrir un PDF (aparece en «Abrir con»)
spanish.PdfFilesGroup=Archivos PDF:
spanish.PdfDocument=Documento PDF
spanish.AppDescription=Visor y editor de planos PDF
spanish.RunApp=Abrir ZenInk

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:ShortcutsGroup}"
Name: "pdfassoc"; Description: "{cm:PdfAssoc}"; GroupDescription: "{cm:PdfFilesGroup}"

[Files]
; El programa entero, con .NET y el Windows App SDK dentro. Por eso son ~280 MB
; sin comprimir: a cambio, en el ordenador de destino no hay que preparar nada.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Sin paquete MSIX, quien declara que ZenInk abre PDF es el registro. Todo bajo
; HKCU, que es lo que permite instalar sin administrador — y lo que hace que
; desinstalar no deje nada detrás.
Root: HKCU; Subkey: "Software\Classes\ZenInk.pdf"; ValueType: string; ValueName: ""; ValueData: "{cm:PdfDocument}"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\Classes\ZenInk.pdf\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\Classes\ZenInk.pdf\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: pdfassoc

; «Abrir con» sin tocar cuál es el predeterminado: eso solo lo cambia el usuario.
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "ZenInk.pdf"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: pdfassoc

; Y para que salga en la lista de aplicaciones predeterminadas de Windows.
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{cm:AppDescription}"; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "ZenInk.pdf"; Tasks: pdfassoc
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: "Software\{#MyAppName}\Capabilities"; Flags: uninsdeletevalue; Tasks: pdfassoc

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent

<#
.SYNOPSIS
    Compila el instalador de ZenInk, firmado si hay con qué firmarlo.

.DESCRIPTION
    Un .exe sin firmar no es «un .exe con un aviso»: para Windows es un programa
    de editor desconocido, y SmartScreen lo para con la pantalla azul de
    «Windows protegió su PC». Y sin certificado la reputación se cuelga del
    propio archivo, así que cada versión vuelve a empezar de cero.

    Firmar el instalador no basta. Lo que se descarga es una cosa, y lo que
    queda en el ordenador es otra: el programa, sus bibliotecas y el
    desinstalador se ejecutan durante años después. Así que aquí se firma el
    contenido primero y el envoltorio después —ese orden y no el otro, porque
    el instalador se lleva dentro los archivos tal como estén al compilarlo—.

    Con quién se firma no está escrito en el repositorio. Una clave no se
    versiona, y ni siquiera está en este disco: los certificados de firma de
    código viven desde 2023 en un token o en un servicio, no en un .pfx. Lo que
    se pasa aquí es la orden entera, la misma que entiende Inno Setup:

      $f  el archivo a firmar        $q  una comilla doble

    Se puede dejar en la variable de entorno ZENINK_FIRMA y olvidarla.

.PARAMETER Firma
    Orden de firma completa. Vacía = se compila sin firmar, que es legítimo
    para probar, pero el resultado no se le da a nadie.

.EXAMPLE
    # Certificado en token (OV/EV): se elige por el nombre del titular.
    .\Publicar.ps1 -Firma 'signtool.exe sign /n $qplano y escala$q /fd sha256 /tr http://timestamp.digicert.com /td sha256 $f'

.EXAMPLE
    # Azure Trusted Signing: el certificado lo pone el servicio en cada firma.
    .\Publicar.ps1 -Firma 'signtool.exe sign /v /fd sha256 /tr http://timestamp.acs.microsoft.com /td sha256 /dlib $qC:\ts\Azure.CodeSigning.Dlib.dll$q /dmdf $qC:\ts\metadata.json$q $f'

.EXAMPLE
    # Sin firma, para ver que el asistente sigue compilando.
    .\Publicar.ps1
#>
[CmdletBinding()]
param(
    [string] $Firma = $env:ZENINK_FIRMA,
    [string] $Iscc  = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'

$Raiz    = Split-Path -Parent $PSScriptRoot
$Guion   = Join-Path $PSScriptRoot 'ZenInk.iss'
$Carga   = Join-Path $Raiz 'dist\suelto'
$Destino = Join-Path $Raiz 'dist'

if (-not (Test-Path $Iscc))  { throw "No esta el compilador de Inno Setup en $Iscc." }
if (-not (Test-Path $Carga)) { throw "No esta la carpeta del programa en $Carga. Publica antes ZenInk.App." }
if (-not (Test-Path (Join-Path $Carga 'ZenInk.App.exe'))) { throw "En $Carga no esta ZenInk.App.exe." }

# Ejecuta una orden de firma sobre un archivo. Va por cmd.exe a proposito: la
# orden viene escrita como una linea de consola, y dejar que PowerShell la
# trocee en argumentos es como se pierde una comilla por el camino.
function Invoke-Firma {
    param([string] $Orden, [string] $Archivo)

    $linea = $Orden.Replace('$q', '"').Replace('$f', '"' + $Archivo + '"')
    & cmd.exe /c $linea
    if ($LASTEXITCODE -ne 0) { throw "Fallo la firma de $Archivo (codigo $LASTEXITCODE)." }
}

if ($Firma) {
    # Solo lo nuestro. Lo que viene de .NET y del Windows App SDK ya lo firmo
    # Microsoft, y volver a firmarlo encima no anade nada: son cientos de
    # archivos y otras tantas llamadas a la autoridad de sellado de tiempo.
    $mios = @(Get-ChildItem -Path $Carga -Filter 'ZenInk.App.exe') +
            @(Get-ChildItem -Path $Carga -Filter 'ZenInk.*.dll')

    Write-Host "Firmando $($mios.Count) archivos del programa..." -ForegroundColor Cyan
    foreach ($archivo in $mios) { Invoke-Firma -Orden $Firma -Archivo $archivo.FullName }
}
else {
    Write-Warning 'Sin firma. El instalador saldra como editor desconocido y SmartScreen lo parara.'
}

$argumentos = @()
if ($Firma) {
    # Firmar lo define el propio .iss: sin el no hay SignTool declarado y esto
    # compila igual, en vez de fallar por una herramienta que nadie configuro.
    $argumentos += '/DFirmar'
    $argumentos += "/Szenink=$Firma"
}
$argumentos += $Guion

Write-Host 'Compilando el instalador...' -ForegroundColor Cyan
& $Iscc @argumentos
if ($LASTEXITCODE -ne 0) { throw "ISCC termino con codigo $LASTEXITCODE." }

$salida = Get-ChildItem -Path $Destino -Filter 'ZenInk_Setup_v*.exe' |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($Firma) {
    # Comprobar la firma contra las raices de confianza de esta maquina, que es
    # lo que va a hacer el ordenador de quien lo descargue. Que signtool no
    # protestara al firmar no dice nada sobre si la cadena se resuelve.
    Write-Host 'Comprobando la firma del instalador...' -ForegroundColor Cyan
    & signtool.exe verify /pa /v $salida.FullName
    if ($LASTEXITCODE -ne 0) { throw 'El instalador quedo firmado con algo que esta maquina no valida.' }
}

$mb = [math]::Round($salida.Length / 1MB, 1)
Write-Host ""
Write-Host "$($salida.FullName)  -  $mb MB  -  $(if ($Firma) { 'firmado' } else { 'SIN FIRMAR' })" -ForegroundColor Green

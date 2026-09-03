# Avisos de terceros

ZenInk se distribuye bajo la GNU General Public License v3.0 o posterior
(ver `LICENSE`). Este fichero recoge el software de terceros que viaja dentro
del programa o de su paquete de instalación, y la licencia de cada uno.

Los textos íntegros están en `licenses/`. Lo que aparece aquí es el índice, no
la licencia.

## PDFium

- **Qué hace**: es el motor que rasteriza las hojas, lee el texto y escribe las
  anotaciones. Todo lo que se ve de un plano en ZenInk sale de aquí.
- **Origen**: <https://pdfium.googlesource.com/pdfium/>
- **Licencia**: BSD 3-Clause (con partes bajo Apache-2.0), © The PDFium Authors.
- **Texto**: `licenses/PDFium-LICENSE.txt`
- **Cómo llega**: la biblioteca nativa la empaqueta PDFiumCore.

## PDFiumCore

- **Qué hace**: las declaraciones de C# sobre las que ZenInk llama a PDFium, y
  el binario nativo para cada arquitectura.
- **Origen**: <https://github.com/Dtronix/PDFiumCore>
- **Licencia**: Apache License 2.0.
- **Texto**: `licenses/Apache-2.0.txt`

## Windows App SDK (WinUI 3)

- **Qué hace**: la ventana, los controles y el ciclo de vida de la aplicación
  empaquetada.
- **Origen**: <https://github.com/microsoft/WindowsAppSDK>
- **Licencia**: Microsoft Software License Terms — no es una licencia libre. Es
  la que trae el paquete NuGet `Microsoft.WindowsAppSDK`.
- **Texto**: `licenses/WindowsAppSDK-LICENSE.txt`

## Win2D (Microsoft.Graphics.Win2D)

- **Qué hace**: dibujar las marcas, los tiradores y la superposición de
  revisiones sobre el lienzo.
- **Origen**: <https://github.com/microsoft/Win2D>
- **Licencia**: el paquete NuGet se distribuye bajo términos de licencia de
  Microsoft (`http://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm`);
  el código fuente del proyecto está publicado bajo licencia MIT.

## .NET y las bibliotecas de la plataforma

`System.Security.Cryptography.Pkcs` —el CMS/CAdES de la firma— y el resto del
tiempo de ejecución de .NET son de Microsoft, bajo licencia MIT:
<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>.

---

Nada de lo anterior lo modifica ZenInk: se usa tal como viene publicado.

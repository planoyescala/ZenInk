# ZenInk

Visor y editor de PDF/planos para Windows 11, unificando visor de alto rendimiento
(estilo Bluebeam/Drawboard), anotación con lápiz, gestión de páginas y captura de
pantalla integrada en una sola herramienta.

## Alcance

1. **Visor de planos de alto rendimiento** — renderizado por tiles sobre PDFium,
   fluido con planos grandes (A0).
2. **Anotaciones** — formas, nubes de revisión, resaltado de texto, texto,
   comentarios y lápiz, con aplanado.
3. **Gestión de páginas** — añadir, quitar, unir.
4. **Captura de pantalla integrada** — recortar y pegar como imagen en el documento.

Fuera de alcance: comparación y superposición de revisiones de planos.

ZenInk se declara como programa que abre PDF: aparece en «Abrir con» y en la
lista de aplicaciones predeterminadas de Windows, y ofrece una vez —con un
«no volver a preguntar»— llevarte a la página donde se elige. Ponerlo como
predeterminado es cosa del usuario: desde Windows 10, ningún programa puede
hacerlo por su cuenta. Los planos que se abran así van a pestañas de la misma
ventana, no a una ventana cada uno.

La medición calibrada estaba fuera de alcance en el planteamiento inicial y
**se ha vuelto a incluir**; está en "Pendiente".

## Stack

- **Renderizado PDF y anotaciones**: [PDFium](https://pdfium.googlesource.com/pdfium/)
  (BSD-3-Clause) vía [PDFiumCore](https://github.com/bblanchon/PDFiumCore) (Apache-2.0)
- **Firma**: `System.Security.Cryptography.Pkcs` (MIT, de Microsoft) para el
  CMS/CAdES; el diccionario de firma y la actualización incremental los escribe
  ZenInk
- **UI y dibujo**: WinUI 3 (.NET 10) con Win2D

Las anotaciones no necesitaron ninguna dependencia nueva: PDFium escribe las
anotaciones, los objetos de trazado con su transparencia, los objetos de texto y
el aplanado.

**PDFsharp se probó y se descartó** para la firma y para el hito 3: reescribe el
archivo entero, así que una segunda firma invalida la primera. El hito 3 acabó
sin necesitarlo —PDFium mueve, importa y borra páginas—, así que no se usa en
ninguna parte. Ver `ESTADO.md`.

Todas las dependencias son gratuitas y libres para uso comercial/cerrado. Ver
`THIRD-PARTY-NOTICES.md` para atribuciones de terceros empaquetadas en los binarios
nativos de PDFium (FreeType, ICU, libjpeg-turbo, lcms2).

## Estructura

```
src/
  ZenInk.App/       # App WinUI 3: interfaz, visor y impresión
  ZenInk.Core/      # Motor sin interfaz: PDFium, tiles, texto, geometría de papel
tests/
  ZenInk.Tests/     # Comprobaciones del motor de renderizado
tools/              # Fuera de la solución: planos de prueba, capturas e iconos
design/             # El maestro del logo; de ahí sale todo el juego de iconos
```

Los iconos **no se editan a mano**: se regeneran con
`dotnet run --project tools/ZenInk.Icons`, que escribe los 82 assets del
paquete desde `design/LogoZenInk.png`. Ver `tools/README.md`.

## Pruebas

```bash
dotnet run --project tests/ZenInk.Tests -c Release
```

Comprueban el motor sin abrir ninguna ventana, contra PDFs sintéticos generados
en el momento: orientación bajo las cuatro rotaciones de página, que cada tile
coincide píxel a píxel con el render de página completa, que las cajas de texto
siguen a la tinta dibujada, el apilado de hojas a una y a dos columnas, el
plegado de mayúsculas y acentos del buscador, que girar una hoja mueve tinta y
texto a la vez, que el giro llega al archivo al guardarlo, qué trozo de plano
cae en cada folio al imprimir —incluido repartir un A0 en varias hojas sin
huecos— y que un documento no puede alterar a otro al cerrarse o fallar.

Y sobre las anotaciones: que una marca vuelve del archivo donde estaba y como
estaba —geometría, color, giro, relleno, acentos y saltos de línea—, que una
marca escrita sobre una hoja que el archivo ya gira cae en el mismo sitio, que
girar la hoja al guardar mueve la marca con el dibujo, que un relleno deja leer
el plano por debajo, que las marcas propias no se dibujan dos veces, que las
anotaciones de otro programa no se tocan, que aplanar las convierte en dibujo
sin perder la página, y la geometría de todo ello: nubes, tiradores, giro sobre
el propio centro y qué coge un clic.

## Hitos

- [x] **Hito 1**: abrir un PDF/plano grande y renderizarlo por tiles con zoom/pan
      fluido. Incluye modos de vista (ancho / página / tamaño real, a una o dos
      páginas, continuo u hoja a hoja), zoom por ventana, navegación por
      teclado, giro de hojas con guardado en el PDF, buscador con Ctrl+F, abrir
      arrastrando, e **impresión** con escala real, márgenes, calidad, grises y
      reparto de un plano grande en varias hojas.
- [x] **Hito 2**: anotaciones. Línea, flecha, polilínea, rectángulo, elipse,
      polígono, nube de revisión, resaltado de texto, texto sobre el plano,
      comentario y lápiz, más una herramienta para coger las marcas hechas.
      Color, grosor, relleno con opacidad propia, tamaño de letra, giro y
      estirado por tiradores, deshacer/rehacer y borrado con la otra punta del
      lápiz. Se guardan en el PDF como anotaciones estándar —se ven en Acrobat
      o Bluebeam—, se reabren para seguir editándolas, se imprimen en vector, y
      se pueden **aplanar** para que nadie las cambie.
- [x] **Firma digital**: firmar con certificado —el de la FNMT— produciendo
      firmas PAdES (`ETSI.CAdES.detached`) que se **añaden** al archivo sin
      reescribirlo, de modo que una firma que ya venía en el documento sigue
      valiendo y la nueva se apila encima. Sello visible con encabezado,
      nombre, DNI, fecha y motivo, colocado dibujando un rectángulo y movible
      antes de escribir nada. Importar certificados por el asistente de
      Windows. Por defecto firma en una copia `… signed.pdf`.
- [x] **Hito 3**: gestión de páginas. Mover, quitar, duplicar y girar hojas
      desde el panel lateral —arrastrando, con botones, o diciendo a qué hoja
      van—, insertar hojas de otros PDF (varios archivos de una vez, ordenados
      por nombre) y hojas en blanco, extraer una selección a un PDF nuevo y
      dividir el juego en varios. Todo queda pendiente hasta guardar, y entra
      en el mismo deshacer que las marcas. Además, el **índice de marcadores**
      del PDF en su propia pestaña del panel.
- [ ] Hito 4: captura de pantalla integrada.
- [ ] Comparar revisiones.

## Pendiente

**Comparar dos revisiones — después del hito 4.** Superponer la revisión nueva
sobre la vieja y enseñar qué ha cambiado: lo que sobra en un color, lo que
falta en otro. Un BIM Manager no lee un plano, lee *qué ha cambiado* entre la
revisión J y la K, y hoy eso se hace a ojo o pagando Bluebeam.

Lo difícil ya está hecho: los tiles, las transformaciones por hoja y el dibujo
sobre el lienzo. Queda alinear las dos hojas —que pueden diferir en tamaño de
papel o en giro— y componer la diferencia. Va después del hito 4 porque la
captura integrada da la forma de exportar el resultado, que es la mitad del
valor: la comparación se enseña en una reunión.

**Medición sobre el plano.** Distancias, áreas, perímetros y ángulos, con la
escala calibrada por hoja. Revierte la exclusión del planteamiento inicial.

Cada hoja se calibra por separado — dos hojas del mismo set pueden ir a escalas
distintas, y el PDF rara vez trae la suya de forma fiable, así que el camino
normal es que el usuario marque una distancia conocida. Una medición es una
anotación: se guarda con el documento y se vuelve a dibujar, así que conviene
que entre después del hito 2 y no antes, para no montar dos veces la capa que
persiste sobre el plano.

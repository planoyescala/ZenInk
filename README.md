# ZenInk

Visor y editor de PDF/planos para Windows 11, unificando visor de alto rendimiento
(estilo Bluebeam/Drawboard), anotación con lápiz, gestión de páginas y captura de
pantalla integrada en una sola herramienta.

## Alcance

1. **Visor de planos de alto rendimiento** — renderizado por tiles sobre PDFium,
   fluido con planos grandes (A0).
2. **Anotaciones** — líneas, rectángulos, comentarios, lápiz/stylus.
3. **Gestión de páginas** — añadir, quitar, unir.
4. **Captura de pantalla integrada** — recortar y pegar como imagen en el documento.

Fuera de alcance: comparación y superposición de revisiones de planos.

La medición calibrada estaba fuera de alcance en el planteamiento inicial y
**se ha vuelto a incluir**; está en "Pendiente".

## Stack

- **Renderizado PDF**: [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD-3-Clause) vía
  [PDFiumCore](https://github.com/bblanchon/PDFiumCore) (Apache-2.0)
- **Gestión de páginas**: [PDFsharp](https://www.pdfsharp.net/) (MIT)
- **Lápiz/stylus**: Windows Ink API (Windows App SDK)
- **UI**: WinUI 3 (.NET 10)

Todas las dependencias son gratuitas y libres para uso comercial/cerrado. Ver
`THIRD-PARTY-NOTICES.md` para atribuciones de terceros empaquetadas en los binarios
nativos de PDFium (FreeType, ICU, libjpeg-turbo, lcms2).

## Estructura

```
src/
  ZenInk.App/       # App WinUI 3
tests/
  ZenInk.Tests/     # Comprobaciones del motor de renderizado
```

## Pruebas

```bash
dotnet run --project tests/ZenInk.Tests -c Release
```

Comprueban el motor sin abrir ninguna ventana, contra PDFs sintéticos generados
en el momento: orientación bajo las cuatro rotaciones de página, que cada tile
coincide píxel a píxel con el render de página completa, que las cajas de texto
siguen a la tinta dibujada, el apilado de hojas, y que un documento no puede
alterar a otro al cerrarse o fallar.

## Hitos

- [x] **Hito 1**: abrir un PDF/plano grande y renderizarlo por tiles con zoom/pan fluido.
- [ ] Hito 2: anotaciones (lápiz, formas, comentarios).
- [ ] Hito 3: gestión de páginas.
- [ ] Hito 4: captura de pantalla integrada.

## Pendiente

**Firma digital de PDF.** Poder firmar un plano con certificado de la FNMT y
con DNIe, y producir firmas CMS/CAdES. Requisito de trabajo, no un extra:
un plano visado o entregado a cliente se firma.

Sin decidir todavía, y con un punto que conviene mirar antes de elegir
librería: PDFsharp no firma, e iText —la opción habitual— es AGPL, lo que
choca con la condición de que todo el stack sea libre para uso comercial y
cerrado. La vía probable es BouncyCastle (MIT) para el CMS más construir a
mano el diccionario de firma del PDF, pero hay que verificarlo. El acceso al
DNIe y a los certificados de la FNMT va por el almacén de certificados de
Windows (CNG/CAPI), que no es problema de licencia pero sí de integración.

**Medición sobre el plano.** Distancias, áreas, perímetros y ángulos, con la
escala calibrada por hoja. Revierte la exclusión del planteamiento inicial.

Cada hoja se calibra por separado — dos hojas del mismo set pueden ir a escalas
distintas, y el PDF rara vez trae la suya de forma fiable, así que el camino
normal es que el usuario marque una distancia conocida. Una medición es una
anotación: se guarda con el documento y se vuelve a dibujar, así que conviene
que entre después del hito 2 y no antes, para no montar dos veces la capa que
persiste sobre el plano.

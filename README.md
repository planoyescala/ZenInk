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

Fuera de alcance: mediciones calibradas a escala y comparación de revisiones.

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
  ZenInk.App/     # App WinUI 3
```

## Hitos

- [ ] **Hito 1**: abrir un PDF/plano grande y renderizarlo por tiles con zoom/pan fluido.
- [ ] Hito 2: anotaciones (lápiz, formas, comentarios).
- [ ] Hito 3: gestión de páginas.
- [ ] Hito 4: captura de pantalla integrada.

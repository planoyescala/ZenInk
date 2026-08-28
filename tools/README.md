# Herramientas de desarrollo

No forman parte de la aplicación ni de la suite de pruebas, y **no están en la
solución** a propósito: compilar la solución no debe arrastrarlas. Se ejecutan
sueltas con `dotnet run --project`.

## `ZenInk.Fixtures` — planos sintéticos

Los planos reales son de clientes y no pueden vivir en el repositorio. Estos
ocupan su sitio, y cada uno está hecho para destapar un fallo concreto.

```bash
dotnet run --project tools/ZenInk.Fixtures -- denso      # A0 con ~40.000 objetos
dotnet run --project tools/ZenInk.Fixtures -- rellenos   # rellenos planos de color
dotnet run --project tools/ZenInk.Fixtures -- conjunto   # 6 hojas con texto
```

- **denso** es contra el que medir. En un A0 el coste es recorrer los objetos,
  no pintar píxeles, así que un plano ligero no dice nada.
- **rellenos** es donde se ve una costura entre tiles: sobre un bloque de color
  una raya pálida es inconfundible, sobre líneas se esconde en el dibujo.
- **conjunto** tiene texto buscable repetido y varias hojas: buscador, giros y
  vista a dos páginas.

Escriben a `%TEMP%` salvo que se les dé una ruta como segundo argumento.

## `ZenInk.Shots` — mirar las capturas

La app puede capturarse a sí misma (ver «Depurar la interfaz» en `CLAUDE.md`).
Esto es lo que se hace después con esos PNG.

```bash
# Ampliar una zona a 4x, o dos capturas apiladas para comparar antes/después
dotnet run --project tools/ZenInk.Shots -- ampliar captura.png 20 70 700 50 4
dotnet run --project tools/ZenInk.Shots -- ampliar antes.png despues.png 810 180 100 60 6

# Medir costuras: promedia una franja y delata los saltos de brillo
dotnet run --project tools/ZenInk.Shots -- costuras captura.png columnas 160 180
```

`costuras` responde con números lo que a simple vista es una impresión. Fue lo
que confirmó que el alineado de tiles a la rejilla de píxeles eliminaba las
cuatro columnas con desvío de 40–51 niveles de brillo.

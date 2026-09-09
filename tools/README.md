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
dotnet run --project tools/ZenInk.Fixtures -- revisiones # dos ediciones, J y K
```

- **denso** es contra el que medir. En un A0 el coste es recorrer los objetos,
  no pintar píxeles, así que un plano ligero no dice nada.
- **rellenos** es donde se ve una costura entre tiles: sobre un bloque de color
  una raya pálida es inconfundible, sobre líneas se esconde en el dibujo.
- **conjunto** tiene texto buscable repetido y varias hojas: buscador, giros y
  vista a dos páginas.
- **revisiones** son dos ediciones de un mismo dibujo que difieren en unos
  pocos sitios con nombre: para comparar revisiones, donde lo que importa es
  poder decir a ojo si los cambios encontrados son los que hay.

Escriben a `%TEMP%` salvo que se les dé una ruta como segundo argumento.

## `ZenInk.Compare` — medir la comparación contra planos reales

Lo que la suite no puede cubrir: qué cuesta comparar dos A0 de verdad. Abre las
dos revisiones por la cola del motor —igual que el visor— y cronometra las tres
cosas de las que depende que la comparación sirva.

```bash
dotnet run --project tools/ZenInk.Compare -- hoja.pdf revision.pdf
dotnet run --project tools/ZenInk.Compare                # las fixtures de %TEMP%
```

`--hoja N` · `--pagina N` · `--tiles N` · `--zoom X` (repetible) · `--estirar`
· `--sin-imagen`.

Mide el barrido partido en sus tres tramos —las dos páginas y la inundación—,
el coste de un tile normal contra uno compuesto a cada zoom, el de componer
sobre tinta ya rasterizada, y la memoria en cada paso. Además busca si algún
desplazamiento de unos milímetros encajaría mejor que el que eligió
`SheetAlignment`, que es lo único que dice si hace falta el ajuste manual.

Y escribe la hoja compuesta entera a un PNG en `%TEMP%`: si el rojo, el azul y
el gris caen donde toca no lo decide ningún número.

**Copia los dos archivos a `%TEMP%` antes de abrirlos.** Los planos contra los
que esto existe son los del escritorio, y son de clientes.

## `ZenInk.Icons` — el juego de iconos

Rehace los ochenta y pico iconos de `src/ZenInk.App/Assets` a partir del
maestro `design/LogoZenInk.png`. Sin argumentos: lo lee, lo recorta, lo escala
y los escribe.

```bash
dotnet run --project tools/ZenInk.Icons
```

**Se le dan a Windows todos los tamaños que sabe pedir**, en vez de unos pocos
y el trabajo de estirarlos. La barra de tareas pide el icono a 24 píxeles
efectivos: al 150 % de escala son 36 reales, y con solo un asset de 24 el shell
lo amplía y el resultado se ve borroso. Ampliar un mapa de bits es lo único que
no tiene arreglo después, así que la escalera cubre 100, 125, 150, 200 y 400 %
más una lista de tamaños concretos de 16 a 256. El `.csproj` los recoge por
comodín justamente porque la lista a mano se quedaba corta en silencio.

Escala promediando en alfa premultiplicado. El maestro lleva blanco debajo de
sus píxeles transparentes, y cualquier redimensionado que ignore el alfa lo
arrastra a los bordes como una orla pálida. Se nota primero a 16 px, que es el
tamaño que nadie mira.

## `ZenInk.Shots` — mirar las capturas

La app puede capturarse a sí misma con `RenderTargetBitmap` y dejar el PNG en
`%TEMP%`. Esto es lo que se hace después con esos PNG.

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

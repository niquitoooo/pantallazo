# Pantallazo: todas las funciones

Guía de referencia: qué hace cada cosa, qué se puede cambiar y dónde. Entre paréntesis, el valor que viene de fábrica.

**Contenido**

- [Atajos de teclado](#atajos-de-teclado)
- [Captura de pantalla completa](#captura-de-pantalla-completa)
- [Captura de una región](#captura-de-una-región)
- [Grabación de pantalla](#grabación-de-pantalla)
- [Durante la grabación: la píldora](#durante-la-grabación-la-píldora)
- [Avisos al guardar](#avisos-al-guardar)
- [La ventana: Inicio](#la-ventana-inicio)
- [La ventana: Grabación](#la-ventana-grabación)
- [La ventana: Configuración](#la-ventana-configuración)
- [Nombres de archivo](#nombres-de-archivo)
- [El ícono de la bandeja](#el-ícono-de-la-bandeja)
- [Instalación, portable y desinstalación](#instalación-portable-y-desinstalación)
- [Dónde guarda cada cosa](#dónde-guarda-cada-cosa)
- [Opciones de línea de comandos](#opciones-de-línea-de-comandos)
- [Límites y cosas a saber](#límites-y-cosas-a-saber)

---

## Atajos de teclado

Funcionan en todo Windows, aunque la ventana de Pantallazo esté cerrada (mientras la app siga en la bandeja).

| Acción | Atajo de fábrica |
|---|---|
| Capturar toda la pantalla | `Impr Pant` |
| Capturar una región | `Ctrl + Impr Pant` |
| Iniciar o detener una grabación | `Alt + Impr Pant` |

**Qué se puede cambiar** (Configuración → *Atajos de teclado*):

- Cada acción puede tener **más de una combinación**: *Agregar alternativa* suma otra y la ✗ la quita.
- Para cambiar una, se hace clic en el cuadro y se aprieta la combinación nueva.
- **Restaurar predeterminados** vuelve a los tres de fábrica.
- **Conflictos:** si otro programa ya usa una combinación, queda marcada con el motivo. Si dos acciones de Pantallazo
  tienen la misma, también se avisa.
- Mantener apretada la tecla no repite la acción, y el mismo atajo se ignora durante medio segundo (para que un doble
  toque sin querer no saque dos capturas).

### Impr Pant y la Herramienta Recortes

Windows 11 puede reservar `Impr Pant` para la Herramienta Recortes. Si pasa, Pantallazo lo avisa en Inicio y ofrece
**Liberar Impr Pant** (cambia `PrintScreenKeyForSnippingEnabled` a 0, solo para tu usuario). En Configuración →
*Atajos de teclado* se puede **devolver a Recortes**. Si Windows no toma el cambio enseguida, hay que cerrar sesión y
volver a entrar.

---

## Captura de pantalla completa

- Captura **todos los monitores juntos** en un solo PNG, a resolución real (sin escalar).
- Se guarda en la carpeta de capturas y, si está activado, se **copia al portapapeles** (listo para `Ctrl + V`).
- Suena un sonido corto y aparece un **aviso** con la vista previa (las dos cosas se pueden apagar).
- Si se hace desde la ventana de Pantallazo, la ventana se esconde antes de capturar y vuelve después.

---

## Captura de una región

Al apretar el atajo, la pantalla se **congela** (lo que estaba al apretar es lo que se captura) y se oscurece un poco.
Funciona igual que el selector de ShareX.

**Mouse**

| Acción | Resultado |
|---|---|
| Arrastrar | Marca un rectángulo; al soltar, se captura |
| Pasar el mouse sobre una ventana o control | Se resalta; un clic captura justo eso |
| `Shift` mientras se arrastra | Selección cuadrada |
| `Ctrl` mientras se arrastra | Mueve la selección en vez de agrandarla |
| Clic derecho mientras se arrastra | Descarta esa selección |
| Clic derecho sin selección | Cancela |
| Rueda del mouse | Cambia el aumento de la lupa |
| Botón lateral "atrás" del mouse | Captura toda la pantalla |
| Botón lateral "adelante" del mouse | Captura el monitor donde está el puntero |

**Teclado**

| Tecla | Resultado |
|---|---|
| `Enter` | Captura la selección actual |
| `Esc` | Cancela |
| Flechas | Mueven el puntero de a 1 píxel (`Shift` + flecha: de a 10) |
| `Espacio` | Captura toda la pantalla |
| `º` (la tecla a la izquierda del 1) | Captura el monitor donde está el puntero |
| `Supr` | Borra la selección para empezar de nuevo |

**En pantalla:** lupa con grilla de píxeles, posición X/Y y tamaño de la selección. Nada de eso sale en la imagen.

**Qué se puede cambiar** (Configuración → *Opciones de captura*): la lupa, la detección de ventanas, la cruz que
sigue al puntero y si se incluye el puntero del mouse (ver más abajo).

---

## Grabación de pantalla

- **Iniciar y detener:** `Alt + Impr Pant`, el botón de la tarjeta *Iniciar grabación* en Inicio, el botón de la página
  Grabación o el menú de la bandeja. Suena un tono al empezar (no queda grabado) y otro al terminar.
- **Formato:** MP4 con video H.264 y audio AAC (48 kHz, estéreo).
- **Ritmo constante:** el video sale exactamente a los cuadros por segundo elegidos. Si la pantalla no cambia, o la
  placa no llega, se repite el cuadro anterior.
- **Placa de video:** captura y comprime con la placa (AMD, NVIDIA o Intel). Si no puede, sigue por software, con
  tope de 1920×1080 a 60 fps, y lo avisa.
- **Pausa:** desde la píldora, la página Grabación o la bandeja. En pausa no se graba ni imagen ni sonido, y al
  reanudar el video sigue sin hueco. Se puede detener estando en pausa.
- **Protecciones:**
  - si el disco se queda sin espacio, se detiene sola y guarda lo grabado;
  - si un micrófono está silenciado en Windows, se avisa que se va a grabar silencio;
  - si salís de la app grabando, pregunta y guarda.
- El archivo se escribe con otro nombre (`….grabando.mp4`) y recién al terminar bien toma su nombre final.

Todo lo que se puede elegir para grabar está en [La ventana: Grabación](#la-ventana-grabación).

---

## Durante la grabación: la píldora

- Aparece una **píldora flotante** con un punto rojo que late, el tiempo, el botón **Pausa/Reanudar** y el botón
  **Detener**. En pausa el punto queda gris y quieto.
- Alrededor de lo que se graba hay un **borde punteado rojo**.
- **Ni la píldora ni el borde salen en el video.**
- La píldora **se puede arrastrar** a cualquier lugar, también sobre la barra de tareas. La próxima grabación aparece
  donde la dejaste. Si ese lugar ya no existe (por ejemplo, se desconectó un monitor), vuelve arriba al centro.
- Con el menú Inicio de Windows abierto, la barra de tareas queda por encima de todo. Es un límite de Windows: al
  cerrar Inicio, todo vuelve adelante.

---

## Avisos al guardar

- Abajo a la derecha, con la vista previa de la captura o el nombre de la grabación.
- **Clic:** abre el archivo. **Clic derecho:** cierra el aviso. Con el mouse encima no se va.
- Duración: 3 segundos al guardar, 4 los informativos y 7 los de error.
- Se apagan en Configuración → *Mostrar aviso al guardar* (los de error siempre aparecen).

---

## La ventana: Inicio

Se abre con doble clic en el ícono de la bandeja.

- **Tres tarjetas:** capturar pantalla completa, seleccionar región e iniciar/detener grabación, cada una con su atajo.
  Mientras se graba, la tarjeta muestra el tiempo y sirve para detener.
- **Avisos:** por ejemplo, un atajo que no se pudo activar o `Impr Pant` tomada por Recortes.
- **Botones Capturas y Grabaciones:** abren cada carpeta.

### Recientes

- Muestra las **30 imágenes y videos más nuevos** de la carpeta de capturas y la de grabaciones (las que estén
  configuradas), los haya hecho Pantallazo, otra app o los hayas copiado a mano.
- Cada fila tiene **miniatura** (también los videos), nombre, tipo y fecha.
- **Al pasar el mouse por una fila** aparecen: **Abrir**, **Mostrar en la carpeta**, **Renombrar** y **Eliminar**.
  Con **clic derecho** está el mismo menú.
- **Renombrar:** se edita el nombre sin la extensión. Avisa si tiene caracteres que Windows no acepta
  (`\ / : * ? " < > |`), si es un nombre reservado (`CON`, `LPT1`…) o si ya existe.
- **Eliminar:** manda el archivo a la **Papelera** (se puede recuperar), igual que `Supr` en el Explorador.
- **Se mantiene sola al día:** lo que agregues, borres, renombres o muevas desde el Explorador se refleja enseguida.
  Si cambiás una carpeta en Configuración, pasa a mostrar la nueva.

---

## La ventana: Grabación

Todo se guarda solo y vale para la próxima grabación.

**Arriba:** tiempo, botón **Iniciar/Detener grabación** y, mientras se graba, **Pausar/Reanudar**.

| Opción | Qué hace | Valores (de fábrica) |
|---|---|---|
| **Qué grabar** | Todas las pantallas, una pantalla (se elige de la lista, con su resolución y Hz) o una región fija (botón *Elegir región*, con el mismo selector de las capturas) | (Todas las pantallas) |
| **Audio de la grabación** | Audio del sistema (lo que suena en la PC), micrófono, sistema + micrófonos o sin audio | (Audio del sistema) |
| Volumen del audio del sistema | Con medidor de nivel en vivo | 0 a 200 % (100 %) |
| Micrófonos | Lista de los conectados; cada uno se marca para grabarlo y tiene su volumen y su medidor. Se pueden grabar varios a la vez, mezclados. El botón ⟳ vuelve a buscar dispositivos | 0 a 200 % (100 %) |
| **Resolución** | Original, 1080p o 720p. Achicar la hace la placa; da archivos más livianos y el texto chico menos nítido | (Original) |
| **Cuadros por segundo** | 15, 24, 30, 60, 90, 120 y **144** (solo aparece si hay un monitor de 144 Hz o más). Avisa si la pantalla que grabás no llega a esos Hz | (30) |
| **Calidad** | Liviana, Equilibrada o Alta: cuánto pesa el archivo y qué tan nítido sale en movimiento | (Equilibrada) |
| Grabar el puntero del mouse | Si el puntero sale en el video | (Sí) |
| Carpeta de destino | Dónde se guardan los videos (*Cambiar* / *Abrir*) | (`Videos\Capturas de video`) |

Para que salgan muchos cuadros distintos a 90, 120 o 144 fps conviene tener Windows en "Mejor rendimiento".

---

## La ventana: Configuración

| Sección | Opción | Qué hace | De fábrica |
|---|---|---|---|
| Atajos de teclado | (ver [Atajos](#atajos-de-teclado)) | Cambiar, agregar alternativas, restaurar, liberar `Impr Pant` | — |
| Carpetas de destino | Capturas de pantalla | *Cambiar*, *Abrir* y ↶ para volver a la predeterminada | `Imágenes\Capturas de pantalla` |
| | Grabaciones de video | Igual | `Videos\Capturas de video` |
| Nombres de archivo | Plantilla de capturas y de grabaciones | Ver [Nombres de archivo](#nombres-de-archivo) | `Captura {n}` · `Grabacion {n}` |
| Apariencia | Tema | Del sistema, claro u oscuro | Del sistema |
| | Color | El de Windows o uno propio: azul, verde azulado, verde, ámbar, rosa, violeta o gris. Toda la interfaz se arma con ese color (Material You) | El de Windows |
| Opciones de captura | Incluir el puntero del mouse | En la pantalla completa y en la región (donde estaba al apretar el atajo) | Sí |
| | Copiar al portapapeles | Además de guardar el PNG | Sí |
| | Sonido al capturar | También los tonos de grabación | Sí |
| | Mostrar aviso al guardar | La vista previa abajo a la derecha | Sí |
| | Lupa en la selección de región | La rueda cambia el aumento | Sí |
| | Detectar ventanas y controles | Un clic captura la ventana resaltada | Sí |
| | Cruz a lo ancho de la pantalla | Líneas guía que siguen al puntero | No |
| Calidad de grabación | Resumen | Botón *Abrir Grabación* para cambiarla | — |
| Inicio y segundo plano | Iniciar con Windows | Arranca en segundo plano al iniciar sesión | No |
| | Al cerrar la ventana, seguir en la bandeja | Los atajos siguen andando; para salir, el menú de la bandeja | Sí |
| | Abrir minimizado al iniciar con Windows | Sin mostrar la ventana | Sí |
| Acerca de | Versión y *Abrir registros* | Abre la carpeta de registros | — |

---

## Nombres de archivo

En Configuración → **Nombres de archivo** hay una plantilla para capturas y otra para grabaciones. La extensión
(`.png` o `.mp4`) se agrega sola. Debajo de cada una se ve **el nombre exacto que va a salir**.

| Etiqueta | Se reemplaza por |
|---|---|
| `{tipo}` | Captura o Grabación |
| `{app}` | El programa que estaba al frente (por ejemplo, Google Chrome). Se ignoran las ventanas de Pantallazo; si no hay ninguno, Escritorio |
| `{ventana}` | El título de esa ventana (sin caracteres prohibidos, hasta 60 letras) |
| `{aaaa}` `{mm}` `{dd}` | Año, mes y día |
| `{hh}` `{min}` `{ss}` | Hora, minutos y segundos |
| `{n}` | Número que sube en cada archivo: 001, 002… Si un número ya está en la carpeta, usa el siguiente libre |

- Las etiquetas se agregan tocando sus botones (entran donde está el cursor).
- Si la plantilla tiene una etiqueta que no existe o un carácter prohibido, se avisa en rojo y no se guarda.
- **Volver a los nombres predeterminados** deja `Captura {n}` y `Grabacion {n}`.
- Ejemplo: `{tipo} {app} {aaaa}-{mm}-{dd} {hh}.{min}.{ss}` → `Captura Google Chrome 2026-10-18 00.28.44.png`.
- Nunca se pisa un archivo: si el nombre ya existe, se agrega ` (2)`, ` (3)`…

---

## El ícono de la bandeja

- **Clic:** captura una región (mientras se graba: detiene la grabación).
- **Doble clic:** abre la ventana.
- **Clic derecho:** menú con capturar pantalla completa, seleccionar región, iniciar/detener grabación,
  pausar/reanudar (mientras se graba), carpeta de capturas, carpeta de grabaciones, abrir Pantallazo y **Salir**.
- Mientras se graba, el ícono se pone rojo.
- Cerrar la ventana no cierra la app: para salir del todo, **Salir** en este menú.

---

## Instalación, portable y desinstalación

- **Instalador** (`Pantallazo-<versión>-instalador.exe`): se instala solo para tu usuario, sin permisos de
  administrador, en `%LOCALAPPDATA%\Programs\Pantallazo`, con acceso directo en el menú Inicio. Ofrece tres casillas
  opcionales (vienen sin marcar): acceso directo en el escritorio, iniciar con Windows y liberar `Impr Pant` de la
  Herramienta Recortes. Si Pantallazo está abierto, lo cierra solo (guardando una grabación en curso). Instalar encima
  de una versión anterior conserva la configuración.
- **Portable** (`Pantallazo-<versión>-win-x64.zip`): se descomprime en cualquier carpeta y se abre `Pantallazo.exe`.
- Ninguno necesita instalar .NET aparte.
- **Desinstalar:** desde *Configuración de Windows → Aplicaciones*. Deshace "iniciar con Windows" y "liberar
  `Impr Pant`" si los habías marcado al instalar. No borra tus capturas ni grabaciones.
- No se pueden abrir dos Pantallazo a la vez: abrirlo de nuevo muestra la ventana del que ya está.

---

## Dónde guarda cada cosa

| Qué | Dónde |
|---|---|
| Capturas | `Imágenes\Capturas de pantalla` (o la carpeta elegida) |
| Grabaciones | `Videos\Capturas de video` (o la carpeta elegida) |
| Configuración | `%APPDATA%\Pantallazo\settings.json` |
| Registros (14 días) | `%LOCALAPPDATA%\Pantallazo\logs` |

Las carpetas predeterminadas siguen a Imágenes y Videos aunque estén en OneDrive o en otro disco. Pantallazo funciona
sin Internet y no envía datos a ningún lado.

---

## Opciones de línea de comandos

| Opción | Para qué |
|---|---|
| `Pantallazo.exe --minimizado` | Abre en la bandeja sin mostrar la ventana (lo usa *Iniciar con Windows*) |
| `Pantallazo.exe --salir` | Cierra el Pantallazo abierto (guarda una grabación en curso); lo usa el instalador |
| `Pantallazo.exe --render-ui <carpeta> [#color]` | Guarda imágenes de cada sección en claro y oscuro (para revisar la interfaz) |
| `Pantallazo.exe --make-icon <archivo.ico>` | Genera el ícono de la app |

---

## Límites y cosas a saber

- **Cuadros reales:** cuántos cuadros distintos salen depende de la placa y del modo de energía de Windows. Pedir
  más fps que los Hz del monitor solo agrega cuadros repetidos.
- **Resolución máxima** por placa de video: 4096×2304. Por software: 1920×1080 a 60 fps.
- **Menú Inicio:** abierto, tapa la píldora y el borde de grabación (límite de Windows).
- **Varios monitores con distinta escala:** está pensado para eso, pero solo se probó con un monitor.
- **Recientes** muestra 30 elementos: para ver todo, botones *Capturas* y *Grabaciones*.

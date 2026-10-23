# Pruebas

Equipo de prueba: notebook con Windows 11, un solo panel interno 2560×1440 a 120 Hz (escala 175 %) conectado a la
GPU integrada AMD, más una GPU NVIDIA dedicada sin monitores; enchufada. Micrófono interno.

## Automáticas

`dotnet test Pantallazo.slnx`: **131 en verde** (63 de la app: atajos, nombres, geometría del selector, color M3 y
contraste, configuración, `Impr Pant` (anotación del original, restaurar, pausa, atajos en pausa); 68 del motor:
rejilla de cuadros, tamaños y bitrate, mezclador, conversión de audio, nombres, lienzo de monitores, encoder). Las
pruebas de `Impr Pant` usan el registro real pero en una clave propia que se borra al terminar.

## Reales (con la app compilada, teclado y mouse simulados con `keybd_event`/`mouse_event` y UI Automation)

| # | Prueba | Resultado |
|---|---|---|
| 1 | Pantalla completa con `Impr Pant` | OK: PNG 2560×1440 en `Imágenes\Capturas de pantalla`, aviso y portapapeles |
| 2 | Región con `Ctrl + Impr Pant` (arrastre de 400×300) | OK: PNG de 400×300 exactos, sin oscurecido, borde ni lupa en la imagen |
| 2b | Clic simple sobre una ventana detectada (barra de tareas) | OK: PNG 2560×84 |
| 3 | `Esc` cancela | OK: no se guarda nada |
| 4 | Cambiar los tres atajos desde Configuración | OK: Ctrl+Mayús+F9/F10/F11 guardados en `settings.json`; "Restaurar predeterminados" vuelve a Impr Pant / Ctrl / Alt |
| 5 | Atajos con la ventana minimizada y con la app en la bandeja | OK (Ctrl+Mayús+F9 capturó con la ventana minimizada; las pruebas 1–3 corrieron con la app solo en la bandeja) |
| 6 | Conflicto de atajos | OK: con otro proceso dueño de Ctrl+Impr Pant, Windows devuelve 1409 y la app lo marca "Otra aplicación ya usa esta combinación"; dos acciones con la misma combinación se marcan en Configuración |
| 7 | Grabación sin audio | OK: 2560×1440, 30,1 fps, sin pista de audio, cierre en 26 ms |
| 8 | Grabación con audio del sistema (tono de 1 kHz) | OK: tono detectado a ≈1000 Hz durante 1,9 s de 2 s; audio y video de igual duración |
| 9 | Grabación con un micrófono | OK: con el tono de 1 kHz sonando por los parlantes, la pista del mic tiene señal real (pico 0,046, RMS 0,0047) con el máximo a los 1,9 s, cuando sonaba el tono (≈830 Hz por el procesamiento del arreglo de micrófonos y la sala). Con el mic silenciado en Windows la app avisa "se va a grabar silencio" |
| 10 | Sistema + micrófono | OK: tono de 1 kHz detectado 2,2 s; la mezcla sube respecto de solo sistema (pico 0,101 contra 0,066; RMS 0,027 contra 0,021), o sea que se suman las dos fuentes. Con un solo micrófono físico no se probó "varios" en hardware (sí en los tests del mezclador) |
| 11 | Sincronización | Motor (RecHarness): audio entre +13 y +60 ms respecto del video midiendo un destello contra un tono (incluye ~50 ms de buffer del reproductor); 60 s a 1440p: audio 60,139 s = video |
| 12 | Carpetas predeterminadas | OK: `Imágenes\Capturas de pantalla` y `Videos\Capturas de video` vía carpetas conocidas |
| 13 | Cambiar carpetas | Probado el guardado de la ruta en configuración; el diálogo de carpeta es el de Windows (no automatizado) |
| 14 | Varios monitores / DPI distintos | **No probado con hardware** (hay un solo monitor). Diseño: todo en píxeles físicos (PerMonitorV2), selector en una sola ventana sobre el escritorio virtual que ignora WM_DPICHANGED, grabación con un WGC por monitor compuestos en un lienzo |
| 15 | Falta de espacio, dispositivos, errores | Motor: poco disco al iniciar (no arranca) y durante (se detiene y guarda), falla del encoder por hardware (reintenta por software), Dispose grabando: OK con ganchos de prueba. Captura: errores de disco lleno/permiso se informan con aviso. **No probado**: desconectar un mic o un monitor grabando |
| 16 | Configuración persistente | OK: atajos, audio y opciones se releen de `settings.json` al reabrir |
| 17 | Tema claro / oscuro / del sistema | OK: `--render-ui` genera las tres secciones en claro y oscuro; el tema del sistema sigue `AppsUseLightTheme` y escucha el cambio de preferencias de Windows (ese cambio en vivo no se probó) |
| 18 | Pausa y reanudación | Motor (RecHarness, 120 fps a 1080p, 10 s con 3 s de pausa): video y audio de 7,125 s, 844 cuadros a 120,0 fps, hueco máximo 8 ms, tono detectado 2,0 s, sincronía +40 ms (por software: −6 ms). App con clics reales: pausar en la píldora, reanudar y volver a pausar desde la página, **detener estando en pausa**: video de 4,55 s (2,48 + 2,07 s grabados) de 15,5 s de prueba; la píldora y la página muestran Reanudar y el punto gris quieto en pausa |
| 19 | Scroll con trackpad | El ScrollViewer de WPF movía 48 px por mensaje de rueda sin mirar el delta. Medido en Configuración con mensajes simulados: 12 mensajes de 10 (lo mismo que un clic de rueda) bajaban 1081 px de 2481; ahora 90 px, igual que un clic de rueda (que no cambió) |
| 20 | 144 fps | Motor (RecHarness, 8 s con audio del sistema): 1080p → 1157 cuadros, 144,0 fps exactos, hueco máx 7 ms, 10,1 MB, sincronía +53 ms; 1440p → 144,0 fps, hueco máx 7 ms, 13,9 MB, +23 ms (encoder AMD por hardware, ambos aceptados). En la página Grabación el botón aparece solo con un monitor de 143 Hz o más (o si ya estaba elegido, con aviso de cuadros repetidos); acá el panel es de 120 Hz, **no se probó con un monitor de 144 Hz real** |
| 21 | Cartelitos de la píldora en el video | Antes: pasando el mouse por Pausa, el cartelito «Pausar la grabación» quedaba grabado (la píldora no). Ahora se excluye de la captura al abrirse: barrido de 21 cuadros cada 0,1 s mientras aparece, solo cambia el puntero (58 contra 1284 píxeles). Después la píldora quedó sin ningún cartelito (Pausa, Detener ni la píldora: verificado con UI Automation) |
| 22 | Barra de título de 48 (como Windows 11) | Con clics reales: al apoyar el mouse en maximizar aparece el menú de acomodar ventanas; clic en maximizar/restaurar, doble clic en la barra, arrastrar (se movió 120, 80 exactos), minimizar y cerrar (a la bandeja) OK. Maximizada la ventana queda justo en el área de trabajo (no tapa la barra de tareas ni se corta arriba). Claro y oscuro revisados con `--render-ui` |
| 23 | Recientes sigue a los archivos | Con la ventana abierta en Inicio: borrar una captura o un video (a la Papelera, como el Explorador) los saca de la lista en menos de 1,2 s; renombrar una captura cambia el nombre en la lista. Además se revisa la lista cada vez que la ventana toma el foco |
| 24 | Renombrar y eliminar desde Recientes | Con UI Automation y clic derecho real: el diálogo propone el nombre sin extensión, «a:b» muestra «El nombre no puede tener \ / : * ? " < > \|» sin cerrar, «prueba-ui» renombra el archivo y la fila; Eliminar (botón) y Eliminar (menú de clic derecho: Abrir, Mostrar en la carpeta, Renombrar, Eliminar) mandan a la Papelera y sacan la fila. Bug encontrado y corregido: al cerrarse el diálogo la ventana tomaba el foco y la revisión borraba la fila renombrada |
| 25 | Miniatura de las grabaciones | Grabación de 4 s desde la app: en Recientes aparece con miniatura (la del Explorador), igual que una grabación anterior; al renombrarla la conserva. Se cargan en segundo plano |
| 26 | Plantillas de nombre | En Configuración con UI Automation: ejemplo en vivo al escribir, «{fecha}» y «a:b» muestran el error y no se guardan, tocar una etiqueta la inserta en el campo, «Volver a los nombres de siempre» restaura. Con Impr Pant y Alt + Impr Pant (Chrome detrás de Pantallazo): «Captura Google Chrome 2026-10-18 00.28.44.png» y «Grabación Google Chrome 001.mp4» |
| 27 | Nombres predeterminados nuevos | Una configuración con las plantillas viejas sin tocar pasa sola a `Captura_{hh}-{min}-{ss}_{dd}-{mm}-{aaaa}` y `Grabación_{hh}-{min}-{ss}_{dd}-{mm}-{aaaa}`; con atajos salieron `Captura_00-36-37_18-10-2026.png` y `Grabación_00-36-40_18-10-2026.mp4`. Una plantilla propia se respeta (test) |
| 28 | Recientes = las carpetas | Al abrir aparecen los archivos de las dos carpetas aunque no los haya hecho esta copia de Pantallazo; un PNG creado desde afuera aparece solo y un `.png.tmp` no; mandarlo a la Papelera lo saca. Cambiando la carpeta de capturas en Configuración (diálogo de Windows), la lista pasa a la carpeta nueva al instante y una captura nueva aparece ahí; «volver a la predeterminada» vuelve a la de siempre |
| 29 | Predeterminadas `Captura {n}` / `Grabacion {n}` | Una configuración con las predeterminadas anteriores pasa sola a las nuevas. Con `Captura 001.png` y `Grabacion 001.mp4` ya en las carpetas, Impr Pant y Alt + Impr Pant dieron `Captura 002.png` y `Grabacion 002.mp4` (saltean el número usado); el ejemplo de Configuración coincide con el archivo que sale |
| 30 | Pausar `Impr Pant` (1.1.0, app instalada, clics reales en el interruptor de Configuración) | Liberada → pausar: el valor vuelve al original anotado (se escribe 1, lo predeterminado de Windows 11) y `Impr Pant` abre Recortes al instante; Pantallazo no captura. Reiniciar la app: sigue en pausa. Reactivar: vuelve a 0 y `Impr Pant` captura con Pantallazo. Inicio muestra «en pausa» en los tres atajos |
| 31 | Borrar el valor en vez de escribirlo | Con el valor borrado después de un 0, Windows siguió con el 0 (Impr Pant no abrió Recortes) hasta otro cambio: por eso restaurar escribe lo predeterminado en vez de borrar |

## Rendimiento medido

- Grabación 1440p a 30 fps con el encoder AMD por hardware: 30,1 fps, 0 cuadros descartados, ~6 Mbps reales.
- 1440p a 120 fps por hardware: ~100 fps reales (la GPU integrada comparte trabajo con el encoder). Por software
  llega a 120 pero en modo seguro 1080p/60.
- Arranque de la grabación (del atajo al primer cuadro): **0,7 s** la primera vez y **0,47 s** las siguientes con
  audio del sistema; 0,41 s sin audio. Antes eran 2,4–2,7 s: la primera inicialización del encoder AMD en cada proceso
  tarda ~1,9 s y las siguientes ~0,2 s, así que la app lo "calienta" en segundo plano al abrirse (sin costo en reposo:
  57 MB privados, 0 % de CPU), abre el audio y el encoder en paralelo y crea el indicador mientras el motor arranca.
- La sección Grabación carga los dispositivos y medidores en segundo plano (tardan ~0,9 s y antes congelaban la ventana).

- Resolución (10 s de la misma animación a 30 fps, encoder AMD): original 2560×1440 = 13,0 MB; 1080p = 7,7 MB (−41 %);
  720p = 4,9 MB (−62 %). El escalado lo hace la GPU antes de codificar; las tres a 30 fps sin cuadros descartados.

- 90 y 120 fps: por hardware el video sale a ritmo constante (ranuras de 1/fps; si no llega un cuadro nuevo
  se repite el último NV12, sin costo de conversión): 8 s a 120 fps = 964–965 cuadros, 90 fps = 724, 30 fps = 241,
  hueco máximo de un cuadro. La conversión a NV12 pasó a una sola pasada desde la captura (antes copia al lienzo +
  copia al pool + conversión de Media Foundation): la grabación usa la mitad del motor 3D (17,6 % contra 33,4 %).
  Cuadros **distintos** a 120 fps: los limita la composición del escritorio (`dwm`, 43–57 % del motor 3D) con Windows en
  "Mejor eficiencia energética" y el perfil silencioso del fabricante: ~53/s tanto con la versión vieja como con la
  nueva (A/B intercalado, 3 rondas).
- Con Windows en "Mejor rendimiento" y el perfil de máximo rendimiento del fabricante (enchufada, 2 rondas de 8 s): **100 % de cuadros reales**:
  120 fps a 1080p y a 1440p = 120,3 nuevos/s (2–4 repeticiones en 965 cuadros); 90 fps = 90,2–90,6 nuevos/s.
  Grabando 1440p/120 la GPU integrada queda con el codificador al 59 % y el motor 3D al 53 % (dwm 30 %, la grabación 10 %):
  sobra margen. Conclusión: los fps reales dependen sobre todo del modo de energía.

## Limitaciones

- Lo que pasa en pantalla en los ~0,5 s entre el atajo y el primer cuadro no se graba (el indicador muestra "Iniciando…").
  Si se aprieta Alt+Impr Pant en los primeros ~2 s después de abrir la app, el encoder todavía se está calentando y ese
  primer arranque puede tardar más.
- Con una GPU NVIDIA sin monitores, Windows puede elegir "Microsoft AVC DX12 Encoder HMFT", que falla: si el monitor
  grabado está en esa placa, se graba por software (1080p/60 como máximo) con aviso.
- Si cambia la salida de audio predeterminada durante una grabación, se sigue grabando la anterior; un micrófono
  desconectado pasa a silencio (con aviso) y no se reconecta solo.
- Grabar "todas las pantallas" con un escritorio de más de 4096×2304 se escala para que entre en el encoder.
- `Impr Pant` sola puede no llegar si Recortes la tiene asignada (se ofrece liberarla).
- Después de varias desinstalaciones e instalaciones seguidas en un par de minutos, Explorer (que es el que abre
  Recortes con `Impr Pant`) dejó de tomar los cambios de la tecla: ni el interruptor de la Configuración de Windows la
  apagaba. Se arregla reiniciando Explorer o cerrando sesión. En uso normal (pausar, reactivar, una actualización) los
  cambios se tomaron al instante.
- Instalador y portable sin firma digital (SmartScreen avisa la primera vez). El asistente usa la traducción al
  español de Inno Setup (neutro, no rioplatense).

## Instalador (Inno Setup, probado en silencio con `/VERYSILENT`)

| Prueba | Resultado |
|---|---|
| Instalación limpia | OK: código 0, archivos en `%LOCALAPPDATA%\Programs\Pantallazo`, menú Inicio, entrada en Aplicaciones instaladas, "Iniciar con Windows" cuando se elige |
| Reinstalar con la app abierta y **grabando** | OK: la app recibe `--salir`, guarda el video (3,8 MB, sin temporales sueltos), cierra, y se instala encima |
| Desinstalar con la app abierta | OK: la cierra, borra archivos, carpeta, acceso directo, inicio con Windows y entrada; conserva configuración y grabaciones |
| App instalada | OK: Impr Pant, región, Esc y clic en ventana |
| Actualizar de 1.0.0 a 1.1.0 con «liberar Impr Pant» marcada | OK: anota que antes de Pantallazo no había valor (el 0 lo había puesto la 1.0.0) |
| Actualizar con `Impr Pant` en pausa | OK: el instalador no la libera (lo dice el log) |
| Desinstalar una instalación que viene de la 1.0.0 | OK: escribe el original antes de procesar el registro de desinstalación viejo (que borra el valor); Impr Pant abre Recortes |
| Instalación limpia y desinstalación de la 1.1.0 | OK: anota el valor que había al instalar; al desinstalar lo vuelve a poner (1) y borra la anotación |
| Asistente normal | Se ve en castellano con el ícono de la app (captura revisada) |

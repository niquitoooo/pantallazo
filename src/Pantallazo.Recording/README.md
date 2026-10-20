# Pantallazo.Recording

Motor de grabación de pantalla de Pantallazo, escrito desde cero:
Windows Graphics Capture → D3D11 → `IMFSinkWriter` (H.264 + AAC, MP4) y audio WASAPI mezclado por reloj.
Evita trampas de drivers conocidas (ver `docs/DISENO.md`).

## Uso desde la app

```csharp
// Main, ANTES del mutex de instancia única y de WPF:
if (args.Contains(EncoderProbe.Argument)) return EncoderProbe.RunChild(args);   // "--probe-encoder <LUID>"
EncoderProbe.ProbeExecutablePath = Environment.ProcessPath;
_ = EncoderProbe.PrepareAsync();          // opcional: sondea el encoder en segundo plano (ahorra ~1,7 s la 1.ª vez)
RecordingLog.Message += m => log.Write(m); // diagnóstico (encoder elegido, tiempos, errores)
```

- `DisplayCatalog.GetDisplays()`: monitores en píxeles físicos (usa `EnumDisplaySettings`, no depende del DPI del
  proceso), nombre real vía `QueryDisplayConfig` y Hz exactos. Orden: izquierda→derecha.
- `AudioDeviceCatalog`: `GetInputDevices()` (micrófonos activos, `IsDefault` = rol Console), `GetDefaultOutput()`
  (rol Multimedia), `DevicesChanged` (se dispara en un hilo del pool).
- `AudioLevelMonitor(systemAudio, micIds)`: medidores sin grabar, ~20 eventos/s, una entrada por fuente pedida
  (sin repetidos, en el orden pedido; `"system"` primero). `SourceFailed` llega en el primer tick (50 ms) si una
  fuente no abre, o cuando se desconecta. El constructor bloquea ~100 ms (abre WASAPI en un hilo MTA). Puede convivir
  con el `Recorder` (WASAPI compartido), pero conviene liberarlo al empezar a grabar: el `Recorder` publica sus propios niveles.
- `Recorder`:
  - La **primera** inicialización del encoder AMD en cada proceso tarda ~1,9 s (dentro de `SetInputMediaType`,
    del driver); las siguientes ~0,2 s. `EncoderProbe.PrepareAsync()` además de sondear "calienta" el encoder en el
    proceso (solo si el sondeo aislado eligió hardware del fabricante), así `StartAsync` tarda ~0,45–0,7 s.
    Audio (WASAPI, ~0,4–0,6 s) y encoder se abren en paralelo; la pista de audio se crea si se pidió audio, aunque
    ninguna fuente abra (queda en silencio, con aviso).
    Mostrar el estado `Starting`. Lanza `InvalidOperationException` con mensaje en castellano.
  - Todos los eventos (`StateChanged`, `LevelsChanged`, `Warning`, `Finished`) llegan **en orden, en un hilo propio del
    grabador** (nunca el de la UI): usar `Dispatcher.BeginInvoke`. Un manejador lento no frena la captura.
  - `StopAsync` tarda ~50 ms sin audio y ~250 ms con audio (espera 150 ms los últimos paquetes de WASAPI). Dos llamadas
    simultáneas reciben el mismo resultado; después de terminar, devuelve "No hay ninguna grabación en curso.".
  - `Finished` se dispara siempre al terminar (manual o automático). `Warnings` del resultado trae todos los avisos.
  - `Elapsed`: tiempo grabado (en `Starting` vale 0; después de parar queda la duración final).
  - `Dispose` grabando: detiene y guarda (espera hasta 15 s).
  - `OutputPath`: la app elige el nombre; se graba en `<nombre>.grabando.mp4` y al cerrar bien se renombra
    (si existe, " (2)", " (3)"…). La carpeta se crea si no existe.

## Cómo funciona

| Pieza | Archivo | Notas |
|---|---|---|
| Lienzo y monitores | `Core/CanvasLayout.cs` | AllDisplays = unión de monitores (negro donde no hay); Display = un monitor; Region = recorte (dimensiones pares, se recorta 1 px si hace falta) |
| Captura | `Video/MonitorCapture.cs`, `WgcInterop.cs` | Una sesión WGC por monitor, `IsBorderRequired=false`, cursor según opción, `MinUpdateInterval=1 ms` por vtable (`IGraphicsCaptureSession5`; no está en la proyección 22621) |
| Composición y ritmo | `Video/VideoPipeline.cs` | Copia cada cuadro a su lugar del lienzo (`CopySubresourceRegion`); emite según `Core/FrameGrid.cs` (ranura = ⌊(t−t0)·fps+0,5⌋). Un marcapasos emite cambios que cayeron en una ranura ya usada, un cuadro de mantenimiento por segundo con la pantalla quieta y uno final al detener |
| Encoder hardware | `Encoding/Mp4Writer.cs` | Textura BGRA del pool (8) → `MFCreateDXGISurfaceBuffer` con `CurrentLength` → sink writer con D3D manager; el sink writer escala en GPU. `MF_XVP_DISABLE_FRC` en el procesador de video, `MF_LOW_LATENCY=0`, throttling apagado (el pool pone el límite), bitrate por CODECAPI (VBR con pico 1,5×, GOP 2 s), perfil High → Main → por defecto |
| Encoder software | `Video/Nv12Converter.cs` | Procesador de video D3D11: escala y convierte a NV12 (BT.709 limitado) en GPU, se baja a memoria; cola máx. 6 cuadros (si no, se descarta y cuenta). Modo seguro 1080p/60 |
| Decisión de encoder | `EncoderProbe.cs` | Proceso hijo `--probe-encoder <LUID>` que crea el sink writer real y codifica 30 cuadros; si Windows elige "Microsoft AVC DX12 Encoder HMFT", un encoder de Windows o falla → software. Caché en `%LOCALAPPDATA%\Pantallazo\encoder-cache.json` por VEN:DEV:SUBSYS + versión de driver + nombre |
| Audio | `Audio/AudioCaptureSource.cs`, `Core/AudioConverter.cs`, `Core/AudioTimeline.cs`, `Core/AudioMixer.cs` | WASAPI compartido (NAudio): loopback de la salida predeterminada (Multimedia) y micrófonos por id. Conversión propia a 48 kHz estéreo float (remuestreo lineal, bajada de 5.1/7.1). Cada fuente se ubica en la línea de tiempo por hora de llegada (QPC): si se atrasa >60 ms se rellena con silencio, si se adelanta se descarta el exceso. El mezclador va 100 ms detrás del reloj y produce siempre el tramo pedido (la fuente sin datos aporta silencio), ganancia por fuente, saturación suave (identidad hasta 0,8, tanh hasta 1), picos post‑ganancia. Ids repetidos se mezclan una vez. PCM 16 → AAC 192 kbps |
| Reloj | — | Video: `SystemRelativeTime` de WGC (QPC); audio: QPC de llegada. Mismo t0 (QPC al arrancar la captura) |
| Estados y errores | `Recorder.cs` | Ver tabla de abajo |

### Errores

| Situación | Qué hace |
|---|---|
| < 200 MB libres al iniciar | No arranca: "Queda poco espacio en el disco…" |
| < 100 MB durante | Se detiene sola, guarda, `Warning` + `Finished` |
| Monitor desconectado / cambio de resolución | Se detiene sola y guarda (`GraphicsCaptureItem.Closed` / `ContentSize` distinto) |
| Hardware no arranca | Reintenta una vez por software al iniciar (aviso) y marca la caché |
| Hardware falla en los primeros 3 s | Descarta lo grabado, reinicia por software (aviso; se pierden esos ≤3 s) y marca la caché |
| Encoder falla después | Se detiene; el archivo sin cerrar queda como temporal y se informa la ruta |
| `Finalize` no termina en 10 s | Se abandona el escritor; `Error` con la ruta del temporal |
| Ningún cuadro | Se borra el temporal; `Error` claro (nunca quedan archivos de 0 bytes) |
| Mic desconectado | Esa fuente queda en silencio + `Warning` |
| Mic silenciado en Windows al iniciar | `Warning` ("se va a grabar silencio") |
| Cambia la salida predeterminada | Se sigue grabando la anterior + `Warning` |
| Sin fuentes de audio | MP4 sin pista de audio |
| Pierde > 5 % de cuadros 5 s seguidos | Un `Warning` sugiriendo menos fps |
| Monitores en placas distintas | Se usa la placa del monitor principal (AllDisplays) o del monitor con más área (Display/Region); WGC entrega igual los cuadros de los otros monitores; `Warning` |

## Mediciones reales (notebook de prueba: GPU integrada AMD + GPU NVIDIA dedicada, panel 2560×1440 a 120 Hz, enchufada)

| Prueba | Resultado |
|---|---|
| 1440p, 60 fps, sin audio, 8 s, hardware (AMDh264Encoder) | 58,5 fps reales, 0 descartados, 9,6 Mbps, cierre 40 ms |
| 1440p, 120 fps, 10 s | WGC entrega ~90 cuadros/s → 89 fps en el archivo |
| 1440p, 120 fps, calidad alta, sistema + mic, 60 s | 6007 cuadros = 100 fps, 0 descartados, hueco máx 42 ms, audio 60,139 s = video, 13 Mbps, cierre 282 ms |
| Audio del sistema con tono 1 kHz | detectado ≈1000 Hz durante 1,9–2,0 s de 2 s |
| Sincronía (destello blanco vs. comienzo del tono) | audio +13 / +60 ms (hardware), +41 ms (software); incluye ~50 ms del buffer del reproductor del tono |
| Región 1001×701 en (301,201) | 1000×700, 59,7 fps, contenido correcto (verificado decodificando un cuadro) |
| Software forzado, 1440p → 1080p, 60 fps | 59,9 fps, WGC a 120/s (sin el encoder la iGPU rinde más), colores correctos |
| Sondeo sobre la GPU NVIDIA sin monitores | Windows elige "Microsoft AVC DX12 Encoder HMFT" → software |
| Ciclo de vida | doble Stop, reutilizar, Dispose grabando, poco disco al iniciar y durante, falla simulada del hardware: todo OK, sin temporales |

## Limitaciones conocidas

- A 1440p/120 con encoder por hardware la iGPU AMD comparte trabajo con WGC: ~90–100 cuadros/s reales.
- El primer cuadro llega 15–50 ms después de t0 (el audio arranca en 0).
- Región: WGC captura el monitor entero; llegan cuadros aunque la región no cambie.
- Remuestreo lineal (suficiente para voz y sistema; la mayoría de los dispositivos ya son 48 kHz).
- Al cambiar la salida predeterminada no se reabre el loopback; un mic desconectado no se reconecta solo.
- La caché marca "software" para siempre (hasta cambiar de driver) si el hardware falló grabando; para reintentar, borrar `encoder-cache.json`.

## No probado

Dos monitores / dos placas con monitores, desconexión de monitor o cambio de resolución, desconexión real de un
micrófono, cambio de salida predeterminada grabando, `Finalize` colgado, cero cuadros, pantalla realmente quieta,
funcionamiento a batería.

## Pruebas

- `tests\Pantallazo.Recording.Tests`: rejilla, tamaños/escalado, bitrate, lienzo y recortes, nombres únicos,
  conversor de audio, línea de tiempo (silencio, atraso/adelanto, datos tardíos), mezclador (silencio por reloj,
  ganancia, saturación, dedupe), detección de encoder.
- `tools\RecHarness`: `list`, `adapters`, `decide <LUID>`, `record …` (ventanita animada, tono de 1 kHz, informe del
  MP4 y sincronía), `inspect`, `frame <mp4> <s> <png>`, `meter`, `lifecycle <carpeta>`.

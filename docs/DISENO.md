# Pantallazo — arquitectura breve

Alternativa mínima a ShareX para Windows 10/11 con tres funciones: captura completa, captura de región y
grabación de pantalla con audio. Referencia de comportamiento: ShareX (commit `7223e53`). No se copia código de
ShareX; se reproduce su comportamiento (ver `LICENCIAS.md`).

## Decisiones de diseño
- Interfaz en **WPF** con componentes Material 3 propios (no el tema Fluent).
- Motor de grabación **propio**: WGC → D3D11 → Media Foundation, y WASAPI para el audio. Evita trampas de drivers
  conocidas (ver abajo).
- Selector de región **igual a ShareX** (pantalla congelada, oscurecido 20 %, lupa con grilla, X/Y, tamaño,
  detección de ventanas, soltar = capturar, Shift = cuadrado, flechas 1 px) con estética Material 3.
- Impr Pant ocupada por Recortes: avisar y ofrecer botón "Liberar Impr Pant"
  (`HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled = 0`, reversible).
- Atajos propios (no los de ShareX): Impr Pant = completa, Ctrl+Impr Pant = región, Alt+Impr Pant = grabar.

## Plataforma
C# / .NET 10 / WPF, `net10.0-windows10.0.22621.0`, PerMonitorV2, una sola instancia.
Paquetes (todos MIT): `Vortice.Direct3D11`, `Vortice.MediaFoundation`, `NAudio.Wasapi`.
Sin red, sin telemetría, sin FFmpeg.

## Módulos (`src/Pantallazo`)
| Carpeta | Responsabilidad |
|---|---|
| `Core/` | Lógica pura testeable: atajos (modelo, texto, conflictos), nombres de archivo únicos, configuración, geometría (escritorio virtual, DPI), reloj de cuadros, bitrate, mezcla de audio |
| `Native/` | P/Invoke Win32 (user32, gdi32, shell32, dwmapi) |
| `Config/` | `SettingsStore` (`%APPDATA%\Pantallazo\settings.json`) y rutas conocidas (`SHGetKnownFolderPath`) |
| `Logging/` | Log local diario en `%LOCALAPPDATA%\Pantallazo\logs` |
| `Hotkeys/` | `HotkeyService`: ventana oculta, `RegisterHotKey`, estados Registrado/Falló/Sin configurar, anti-repetición |
| `Capture/` | `ScreenGrabber` (GDI BitBlt + CAPTUREBLT del escritorio virtual, como ShareX), `WindowFinder` (rectángulos de ventanas y controles en Z-order), `ImageSaver` (PNG) |
| `Region/` | `RegionSelectorWindow`: overlay sobre todo el escritorio virtual con la imagen congelada |
| `Recording/` | `DisplayCatalog`, `MonitorCapture` (WGC por monitor), `FrameCompositor` (une monitores en una textura), `Mp4Writer` (sink writer H.264 + AAC), `AudioCapture` (WASAPI loopback/captura → 48 kHz estéreo float), `AudioMixer` (por reloj, ganancia por fuente, medidores), `Recorder` (máquina de estados) |
| `Theming/` | Tokens Material 3 (esquema claro/oscuro generado desde un color semilla), estilos de botones, tarjetas, switches, segmentados, sliders, campos, riel de navegación |
| `Views/` | `MainWindow` (Inicio, Grabación, Configuración), `RecordingIndicator`, `Toast`, `HotkeyBox` |
| `Services/` | `CaptureController`, `RecordingController`, `TrayIcon`, `StartupManager`, `History` |

## Trampas de drivers conocidas
- `MF_LOW_LATENCY` cuelga el encoder AMD → apagado. HEVC del MFT AMD crashea → solo H.264.
- Con algunas GPU NVIDIA sin monitor conectado, Windows elige "Microsoft AVC DX12 Encoder HMFT" → crashea; se
  sondea el encoder en un proceso hijo y se cae a software (memoria de sistema, modo seguro 1080p/60).
- Al escalar, `MF_XVP_DISABLE_FRC` antes de `BeginWriting` (si no, rellena con cuadros repetidos).
- Buffer DXGI nace con longitud 0: fijar `CurrentLength`.
- El encoder AMD ignora `MF_MT_AVG_BITRATE`: fijar bitrate por CODECAPI.
- WGC solo entrega cuadros cuando la pantalla cambia: medir FPS con algo animado y enchufado.

## Todos los monitores en un video
Una sesión WGC por monitor; cada cuadro se copia a su posición en una textura del tamaño del escritorio
virtual (en píxeles físicos). Los encoders H.264 por hardware suelen tener un tope (≈4096 de ancho): si el
escritorio lo supera, se escala proporcionalmente al máximo aceptado. Documentado como limitación.

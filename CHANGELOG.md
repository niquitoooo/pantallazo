# Cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); las versiones siguen
[Versionado Semántico](https://semver.org/lang/es/).

## [1.0.0] - 2026-10-20

Primera versión pública.

### Capturas
- Pantalla completa con `Impr Pant`: todos los monitores en un PNG, copia al portapapeles y aviso con vista previa.
- Región con `Ctrl + Impr Pant`: selector como el de ShareX (pantalla congelada, lupa con grilla de píxeles,
  detección de ventanas y controles, `Shift` para cuadrado, flechas para ajustar de a un píxel).
- Opción de incluir el puntero del mouse.

### Grabación
- `Alt + Impr Pant` inicia y detiene. MP4 H.264 + AAC con audio del sistema, micrófonos o los dos mezclados.
- Captura y codificación por la placa de video (AMD, NVIDIA, Intel), con respaldo por software.
- 15 a 120 fps a ritmo constante, y 144 fps con monitores de 144 Hz o más. Resolución original, 1080p o 720p.
- Pausa y reanudación sin cortes. Píldora flotante con tiempo, pausa y detener, que no sale en el video.

### Interfaz
- Material You (Material 3) con tema claro, oscuro o del sistema, y color propio o el de Windows.
- Barra de título como las apps de Windows 11, con el menú de acomodar ventanas.
- Recientes: las capturas y grabaciones de las carpetas elegidas, con miniaturas; abrir, renombrar y eliminar.
- Nombres de archivo con plantillas (`{tipo}`, `{app}`, `{ventana}`, fecha, hora, `{n}`).
- Atajos configurables con alternativas y aviso de conflictos; ícono en la bandeja; inicio con Windows.
- Instalador por usuario (sin administrador) y versión portable, sin necesidad de instalar .NET.

[1.0.0]: ../../releases/tag/v1.0.0

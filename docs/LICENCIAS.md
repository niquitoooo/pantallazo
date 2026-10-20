# Dependencias y licencias

| Componente | Uso en Pantallazo | Licencia |
|---|---|---|
| .NET 10 / WPF (Microsoft) | Plataforma y la interfaz | MIT |
| Vortice.Direct3D11 3.8.3 | Direct3D 11 para la captura de video | MIT |
| Vortice.MediaFoundation 3.8.3 | Codificación H.264/AAC y escritura del MP4 | MIT |
| NAudio.Wasapi 2.2.1 | Captura de audio WASAPI (sistema y micrófonos) | MIT |
| xUnit (solo pruebas) | Tests automáticos | Apache 2.0 |
| Inno Setup 6.7.3 (solo para armar el instalador) | Compilador del instalador; el instalador generado incluye su motor | Inno Setup License (libre, también para uso comercial) |
| Segoe Fluent Icons / Segoe MDL2 Assets | Íconos (fuente que trae Windows, no se distribuye) | Parte de Windows |

**Pantallazo** se distribuye bajo la licencia **GPL v3** (ver `LICENSE`).

**ShareX** (GPL v3) se usó como **referencia de comportamiento**: cómo funciona su captura de pantalla, su selector
de región, sus atajos y su grabador. No se copiaron recursos ni íconos de ShareX; los comentarios `ShareX: …` del
código señalan qué comportamiento o valor se tomó de ahí.

El sonido de captura y los íconos de la app se generan con código propio.

No hay telemetría ni conexiones de red: la app funciona completamente sin Internet.

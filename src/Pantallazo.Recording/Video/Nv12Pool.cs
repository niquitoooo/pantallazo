using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Pantallazo.Recording;

/// <summary>
/// Camino por hardware: escala y convierte a NV12 (BT.709, rango limitado) en una sola pasada del procesador de video
/// D3D11, directo desde la textura de WGC (o desde el lienzo si hay varios monitores), a texturas de un pool que van
/// tal cual al encoder. Antes eran tres pasadas por cuadro (copia al lienzo, copia al pool y conversión de Media
/// Foundation) y el motor 3D de una GPU integrada se saturaba a 120 fps (92 % medido).
/// Una textura del pool sigue ocupada mientras algún sample (o el último cuadro guardado para repetir) la referencia.
/// </summary>
internal sealed class Nv12Pool : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly int _outW, _outH;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D[] _textures;
    private readonly ID3D11VideoProcessorOutputView[] _outputs;
    private readonly Dictionary<nint, ID3D11VideoProcessorInputView> _inputs = [];
    private int _next;
    private bool _loggedRefcount;

    public Nv12Pool(GpuDevice gpu, int maxInW, int maxInH, int outW, int outH, int fps, int size)
    {
        _gpu = gpu;
        _outW = outW;
        _outH = outH;
        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)maxInW,
            InputHeight = (uint)maxInH,
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)outW,
            OutputHeight = (uint)outH,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(desc);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new Vortice.RawRect(0, 0, outW, outH));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new Vortice.RawRect(0, 0, outW, outH));
        _videoContext.VideoProcessorSetStreamOutputRate(_processor, 0, VideoProcessorOutputRate.Normal, false, null);

        _textures = new ID3D11Texture2D[size];
        _outputs = new ID3D11VideoProcessorOutputView[size];
        for (int i = 0; i < size; i++)
        {
            _textures[i] = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)outW, (uint)outH, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            _outputs[i] = _videoDevice.CreateVideoProcessorOutputView(_textures[i], _enumerator,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        }
    }

    /// <summary>
    /// Convierte el rectángulo <paramref name="source"/> de <paramref name="input"/> a una textura libre del pool y la
    /// devuelve; null si están todas ocupadas (el encoder no da abasto).
    /// </summary>
    public ID3D11Texture2D? Convert(ID3D11Texture2D input, Vortice.RawRect source)
    {
        int idx = AcquireFree();
        if (idx < 0) return null;
        var view = InputView(input);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, source);
        var stream = new VideoProcessorStream { Enable = true, InputSurface = view };
        _videoContext.VideoProcessorBlt(_processor, _outputs[idx], 0, 1, [stream]).CheckError();
        return _textures[idx];
    }

    private int AcquireFree()
    {
        for (int i = 0; i < _textures.Length; i++)
        {
            int idx = (_next + i) % _textures.Length;
            var tex = _textures[idx];
            tex.AddRef();
            uint rc = tex.Release();
            if (!_loggedRefcount) { _loggedRefcount = true; RecordingLog.Write($"Pool NV12: refcount libre = {rc}"); }
            if (rc <= 1)
            {
                _next = (idx + 1) % _textures.Length;
                return idx;
            }
        }
        return -1;
    }

    /// <summary>Vista de entrada por textura (WGC recicla pocas texturas: se guardan; si cambian mucho, se rehacen).</summary>
    private ID3D11VideoProcessorInputView InputView(ID3D11Texture2D input)
    {
        if (_inputs.TryGetValue(input.NativePointer, out var view)) return view;
        if (_inputs.Count >= 16) ClearInputs();
        view = _videoDevice.CreateVideoProcessorInputView(input, _enumerator,
            new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        _inputs[input.NativePointer] = view;
        return view;
    }

    private void ClearInputs()
    {
        foreach (var v in _inputs.Values) v.Dispose();
        _inputs.Clear();
    }

    public void Dispose()
    {
        ClearInputs();
        foreach (var o in _outputs) o?.Dispose();
        foreach (var t in _textures) t?.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}

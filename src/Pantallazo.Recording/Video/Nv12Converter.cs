using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Mathematics;

namespace Pantallazo.Recording;

/// <summary>
/// Camino del encoder por software: escala y convierte el lienzo BGRA a NV12 en la GPU (procesador de video D3D11,
/// BT.709 rango limitado) y lo baja a memoria del sistema en un IMFSample. Darle texturas al encoder por software
/// resultó inestable con algunos drivers, por eso se baja cada cuadro.
/// </summary>
internal sealed class Nv12Converter : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly int _outW, _outH;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D _nv12;
    private readonly ID3D11Texture2D _staging;
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11Texture2D? _inputFor;
    private readonly int _fps;

    public Nv12Converter(GpuDevice gpu, int inW, int inH, int outW, int outH, int fps)
    {
        _gpu = gpu;
        _outW = outW;
        _outH = outH;
        _fps = fps;
        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)inW,
            InputHeight = (uint)inH,
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)outW,
            OutputHeight = (uint)outH,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(desc);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        _nv12 = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)outW, (uint)outH, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _staging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)outW, (uint)outH, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_nv12, _enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
        {
            YCbCr_Matrix = 1,      // BT.709
            Nominal_Range = 1,     // 16–235
        });
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new Vortice.RawRect(0, 0, inW, inH));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new Vortice.RawRect(0, 0, outW, outH));
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new Vortice.RawRect(0, 0, outW, outH));
        _videoContext.VideoProcessorSetStreamOutputRate(_processor, 0, VideoProcessorOutputRate.Normal, false, null);
    }

    public int FrameBytes => _outW * _outH * 3 / 2;

    /// <summary>Convierte el lienzo y devuelve un sample NV12 en memoria (sin tiempo asignado).</summary>
    public IMFSample Convert(ID3D11Texture2D canvas)
    {
        if (!ReferenceEquals(canvas, _inputFor))
        {
            _inputView?.Dispose();
            _inputView = _videoDevice.CreateVideoProcessorInputView(canvas, _enumerator,
                new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
            _inputFor = canvas;
        }
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
        _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, [stream]).CheckError();
        _gpu.Context.CopyResource(_staging, _nv12);

        int len = FrameBytes;
        var buffer = MediaFactory.MFCreateMemoryBuffer(len);
        try
        {
            var map = _gpu.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                buffer.Lock(out nint dst, out _, out _);
                try
                {
                    unsafe
                    {
                        byte* src = (byte*)map.DataPointer;
                        byte* d = (byte*)dst;
                        int pitch = (int)map.RowPitch;
                        for (int y = 0; y < _outH; y++)
                            Buffer.MemoryCopy(src + (long)y * pitch, d + (long)y * _outW, _outW, _outW);
                        byte* srcUv = src + (long)pitch * _outH;
                        byte* dUv = d + (long)_outW * _outH;
                        for (int y = 0; y < _outH / 2; y++)
                            Buffer.MemoryCopy(srcUv + (long)y * pitch, dUv + (long)y * _outW, _outW, _outW);
                    }
                }
                finally { buffer.Unlock(); }
            }
            finally { _gpu.Context.Unmap(_staging, 0); }
            buffer.CurrentLength = len;
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public void Dispose()
    {
        _inputView?.Dispose();
        _outputView.Dispose();
        _staging.Dispose();
        _nv12.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}

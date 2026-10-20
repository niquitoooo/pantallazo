namespace Pantallazo.Recording.Tests;

public class EncoderProbeTests
{
    [Theory]
    [InlineData("AMDh264Encoder", true)]
    [InlineData("NVIDIA H.264 Encoder MFT", true)]
    [InlineData("Intel® Quick Sync Video H.264 Encoder MFT", true)]
    [InlineData("Microsoft AVC DX12 Encoder HMFT", false)]   // el que falla con algunas GPU NVIDIA
    [InlineData("H264 Encoder MFT", false)]
    [InlineData("H264 Encoder MFT (software)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SoloEncodersDelFabricanteCuentanComoHardware(string? name, bool expected)
    {
        Assert.Equal(expected, EncoderProbe.IsVendorEncoder(name));
    }

    [Fact]
    public void ArgumentosInvalidosDelHijo()
    {
        Assert.Equal(2, EncoderProbe.RunChild(["--probe-encoder"]));
        Assert.Equal(2, EncoderProbe.RunChild(["--probe-encoder", "zz"]));
    }
}

public class MfGuidsTests
{
    [Fact]
    public void SubtiposFourCc()
    {
        Assert.Equal(new Guid("34363248-0000-0010-8000-00AA00389B71"), MfGuids.VideoH264);
        Assert.Equal(new Guid("3231564E-0000-0010-8000-00AA00389B71"), MfGuids.VideoNV12);
        Assert.Equal(new Guid("00000015-0000-0010-8000-00AA00389B71"), MfGuids.VideoARGB32);
        Assert.Equal(new Guid("00001610-0000-0010-8000-00AA00389B71"), MfGuids.AudioAAC);
    }

    [Fact]
    public void Empaquetado()
    {
        Assert.Equal(((ulong)1920 << 32) | 1080, MfGuids.Pack(1920, 1080));
    }
}

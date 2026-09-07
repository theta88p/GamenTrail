using System.Buffers;
using System.Runtime.InteropServices;
using GamenTrail.Core.Media;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GamenTrail.Platform.Windows.Video;

internal sealed unsafe class D3D11TextureReader : IDisposable
{
    private const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const uint D3D11MapRead = 1;
    private static readonly Guid Direct3DDxgiInterfaceAccessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid D3D11Texture2DId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private nint _device;
    private nint _context;
    private nint _stagingTexture;
    private uint _stagingWidth;
    private uint _stagingHeight;

    public MediaBuffer Read(
        WindowsCaptureFrameBuffer frame, int outputWidth, int outputHeight,
        int? contentWidth = null, int? contentHeight = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(outputWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(outputHeight, 0);

        var sourceTexture = GetTexture(frame.Surface);
        try
        {
            var description = GetDescription(sourceTexture);
            if (description.Format != DxgiFormatB8G8R8A8Unorm)
            {
                throw new NotSupportedException($"Unsupported capture texture format: {description.Format}.");
            }

            EnsureStagingTexture(sourceTexture, description);
            CopyResource(_context, _stagingTexture, sourceTexture);
            return MapAndCopy(
                outputWidth,
                outputHeight,
                frame.CropX,
                frame.CropY,
                Math.Min(description.Width, checked((uint)(frame.CropX + (contentWidth ?? outputWidth)))),
                Math.Min(description.Height, checked((uint)(frame.CropY + (contentHeight ?? outputHeight)))));
        }
        finally
        {
            Marshal.Release(sourceTexture);
        }
    }

    public void Dispose()
    {
        Release(ref _stagingTexture);
        Release(ref _context);
        Release(ref _device);
        _stagingWidth = 0;
        _stagingHeight = 0;
    }

    private void EnsureStagingTexture(nint sourceTexture, D3D11Texture2DDescription sourceDescription)
    {
        if (_device == 0)
        {
            _device = GetDevice(sourceTexture);
            _context = GetImmediateContext(_device);
        }

        if (_stagingTexture != 0 &&
            _stagingWidth == sourceDescription.Width &&
            _stagingHeight == sourceDescription.Height)
        {
            return;
        }

        Release(ref _stagingTexture);
        var stagingDescription = sourceDescription;
        stagingDescription.MipLevels = 1;
        stagingDescription.ArraySize = 1;
        stagingDescription.SampleDescription = new DxgiSampleDescription { Count = 1 };
        stagingDescription.Usage = D3D11UsageStaging;
        stagingDescription.BindFlags = 0;
        stagingDescription.CpuAccessFlags = D3D11CpuAccessRead;
        stagingDescription.MiscFlags = 0;

        var vtable = *(nint**)_device;
        var createTexture = (delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDescription*, nint, nint*, int>)vtable[5];
        nint stagingTexture = 0;
        Marshal.ThrowExceptionForHR(createTexture(_device, &stagingDescription, 0, &stagingTexture));
        _stagingTexture = stagingTexture;
        _stagingWidth = sourceDescription.Width;
        _stagingHeight = sourceDescription.Height;
    }

    private MediaBuffer MapAndCopy(
        int outputWidth,
        int outputHeight,
        int sourceX,
        int sourceY,
        uint sourceWidth,
        uint sourceHeight)
    {
        var mapped = default(D3D11MappedSubresource);
        var contextVtable = *(nint**)_context;
        var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3D11MappedSubresource*, int>)
            contextVtable[14];
        var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)contextVtable[15];
        Marshal.ThrowExceptionForHR(map(_context, _stagingTexture, 0, D3D11MapRead, 0, &mapped));

        var length = checked(outputWidth * outputHeight * 4);
        IMemoryOwner<byte>? owner = MemoryPool<byte>.Shared.Rent(length);
        try
        {
            var destination = owner.Memory.Span[..length];
            destination.Clear();
            var availableWidth = Math.Max(0, checked((int)sourceWidth) - sourceX);
            var availableHeight = Math.Max(0, checked((int)sourceHeight) - sourceY);
            var copyWidth = Math.Min(outputWidth, availableWidth);
            var copyHeight = Math.Min(outputHeight, availableHeight);
            var rowBytes = checked(copyWidth * 4);

            for (var sourceRow = 0; sourceRow < copyHeight; sourceRow++)
            {
                var source = new ReadOnlySpan<byte>(
                    (byte*)mapped.Data + ((sourceY + sourceRow) * mapped.RowPitch) + (sourceX * 4),
                    rowBytes);
                var destinationRow = outputHeight - sourceRow - 1;
                source.CopyTo(destination.Slice(destinationRow * outputWidth * 4, rowBytes));
            }

            var result = new MediaBuffer(owner, length);
            owner = null;
            return result;
        }
        finally
        {
            owner?.Dispose();
            unmap(_context, _stagingTexture, 0);
        }
    }

    private static nint GetTexture(IDirect3DSurface surface)
    {
        using var surfaceReference = MarshalInterface<IDirect3DSurface>.CreateMarshaler(surface);
        var surfaceAbi = MarshalInterface<IDirect3DSurface>.GetAbi(surfaceReference);
        var accessId = Direct3DDxgiInterfaceAccessId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfaceAbi, in accessId, out var access));

        try
        {
            var textureId = D3D11Texture2DId;
            nint texture = 0;
            var vtable = *(nint**)access;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[3];
            Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
            return texture;
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    private static D3D11Texture2DDescription GetDescription(nint texture)
    {
        var result = default(D3D11Texture2DDescription);
        var vtable = *(nint**)texture;
        var getDescription = (delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDescription*, void>)vtable[10];
        getDescription(texture, &result);
        return result;
    }

    private static nint GetDevice(nint resource)
    {
        nint result = 0;
        var vtable = *(nint**)resource;
        var getDevice = (delegate* unmanaged[Stdcall]<nint, nint*, void>)vtable[3];
        getDevice(resource, &result);
        return result;
    }

    private static nint GetImmediateContext(nint device)
    {
        nint result = 0;
        var vtable = *(nint**)device;
        var getImmediateContext = (delegate* unmanaged[Stdcall]<nint, nint*, void>)vtable[40];
        getImmediateContext(device, &result);
        return result;
    }

    private static void CopyResource(nint context, nint destination, nint source)
    {
        var vtable = *(nint**)context;
        var copyResource = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)vtable[47];
        copyResource(context, destination, source);
    }

    private static void Release(ref nint instance)
    {
        if (instance != 0)
        {
            Marshal.Release(instance);
            instance = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSampleDescription
    {
        public uint Count;

        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDescription
    {
        public uint Width;

        public uint Height;

        public uint MipLevels;

        public uint ArraySize;

        public uint Format;

        public DxgiSampleDescription SampleDescription;

        public uint Usage;

        public uint BindFlags;

        public uint CpuAccessFlags;

        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public nint Data;

        public uint RowPitch;

        public uint DepthPitch;
    }
}

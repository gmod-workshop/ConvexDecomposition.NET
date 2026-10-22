using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace CuACD.Interop;

/// <summary>
/// Hands the embedded CUDA fatbinary to the native shim, once per process.
/// </summary>
internal static unsafe class KernelImage
{
    private const string ResourceName = "CuACD.kernels.fatbin";
    private static readonly object Gate = new();
    private static bool _installed;

    internal static void EnsureInstalled()
    {
        lock (Gate)
        {
            if (_installed)
                return;

            int version;
            try
            {
                version = NativeMethods.cuacd_capi_version();
            }
            catch (DllNotFoundException e)
            {
                throw new CuAcdException(
                    $"The CuACD native library could not be loaded for {RuntimeInformation.RuntimeIdentifier}. " +
                    "Supported platforms are win-x64 and linux-x64.", -1, e);
            }
            if (version != NativeMethods.ExpectedCapiVersion)
                throw new CuAcdException(
                    $"cuacd_native C API version {version} does not match the managed bindings " +
                    $"({NativeMethods.ExpectedCapiVersion}).", -1);

            Assembly asm = typeof(KernelImage).Assembly;
            using Stream stream = asm.GetManifestResourceStream(ResourceName)
                ?? throw new CuAcdException($"Embedded resource {ResourceName} is missing.", -1);

            // The native side keeps the pointer for the process lifetime. A
            // resource of a non-collectible, file-mapped assembly lives that
            // long already; anything else is copied to never-freed memory.
            void* data;
            nuint length = (nuint)stream.Length;
            bool collectible = AssemblyLoadContext.GetLoadContext(asm)?.IsCollectible ?? false;
            if (stream is UnmanagedMemoryStream ums && !collectible && (nuint)ums.PositionPointer % 16 == 0)
            {
                data = ums.PositionPointer;
            }
            else
            {
                data = NativeMemory.AlignedAlloc(length, 64);
                stream.ReadExactly(new Span<byte>(data, checked((int)length)));
            }

            NativeMethods.cuacd_set_fatbin(data, length);
            _installed = true;
        }
    }
}

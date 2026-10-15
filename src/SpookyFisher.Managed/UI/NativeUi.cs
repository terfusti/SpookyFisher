using System;
using System.Runtime.InteropServices;

namespace SpookyFisher
{
    internal sealed class NativeUi
    {
        // CaptureInput bits and TakeActions bits are shared with Overlay.cpp.
        internal const int MouseCapture = 1, KeyboardCapture = 2;
        internal const int StartFishing = 1, StopFishing = 2, Detach = 4;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FishingState
        {
            // Enabled: 0 = off, 1 = fishing, 2 = waiting for the first manual cast.
            internal int Enabled, CanStart, HasAim, Bobbers, BaitPower, FailedCasts, Retrievals;
            internal float AimX, AimY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Frame
        {
            internal int VertexCount, IndexCount, CommandCount, Reserved;
            internal IntPtr Vertices, Indices, Commands;
            internal float Width, Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DrawCommand
        {
            internal float Left, Top, Right, Bottom;
            internal int ElementCount, IndexOffset, VertexOffset, VertexCount;
            internal ulong TextureId;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int InitializeDelegate(IntPtr window);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int StateDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int DisplayDelegate(int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int FontDelegate(out IntPtr pixels, out int width, out int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int FrameDelegate(int width, int height, out Frame frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal delegate void LogDelegate([MarshalAs(UnmanagedType.LPWStr)] string message);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal delegate void FishingDelegate(ref FishingState state,
            [MarshalAs(UnmanagedType.LPWStr)] string status, [MarshalAs(UnmanagedType.LPWStr)] string rod);

        internal readonly InitializeDelegate Initialize;
        internal readonly StateDelegate Shutdown, CaptureInput, Visible, TakeActions;
        internal readonly FishingDelegate SetFishing;
        internal readonly DisplayDelegate ConfigureDisplay;
        internal readonly FontDelegate Font;
        internal readonly FrameDelegate BuildFrame;
        internal readonly LogDelegate WriteLog;

        internal NativeUi()
        {
            // Resolve the already-loaded bridge without adding a CLR-cached LoadLibrary reference.
            // All delegates become unreachable before the native detach acknowledgement.
            IntPtr module = GetModuleHandleW("SpookyFisher.dll");
            if (module == IntPtr.Zero) throw new InvalidOperationException("The native bridge is not loaded.");
            Initialize = Resolve<InitializeDelegate>(module, "SF_UiInitialize");
            Shutdown = Resolve<StateDelegate>(module, "SF_UiShutdown");
            CaptureInput = Resolve<StateDelegate>(module, "SF_UiCaptureInput");
            Visible = Resolve<StateDelegate>(module, "SF_UiVisible");
            TakeActions = Resolve<StateDelegate>(module, "SF_UiTakeActions");
            SetFishing = Resolve<FishingDelegate>(module, "SF_UiSetFishing");
            ConfigureDisplay = Resolve<DisplayDelegate>(module, "SF_UiConfigureDisplay");
            Font = Resolve<FontDelegate>(module, "SF_UiFont");
            BuildFrame = Resolve<FrameDelegate>(module, "SF_UiFrame");
            WriteLog = Resolve<LogDelegate>(module, "SF_Log");
        }

        private static T Resolve<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name + ": rebuild the native DLL too.");
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}

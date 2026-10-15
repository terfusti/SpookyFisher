using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SpookyFisher
{
    internal sealed class Overlay : IDisposable
    {
        internal readonly NativeUi Native = new NativeUi();
        private XnaUiRenderer renderer;
        private IntPtr window;

        internal bool CapturesMouse => (Native.CaptureInput() & NativeUi.MouseCapture) != 0;
        internal bool CapturesKeyboard => (Native.CaptureInput() & NativeUi.KeyboardCapture) != 0;

        internal void Draw(Game game)
        {
            var device = game.GraphicsDevice;
            if (device.GraphicsDeviceStatus != GraphicsDeviceStatus.Normal) return;
            if (window != game.Window.Handle)
            {
                if (Native.Shutdown() == 0) throw new InvalidOperationException("The previous UI window hook is still in use.");
                if (Native.Initialize(game.Window.Handle) == 0)
                    throw new InvalidOperationException("ImGui could not attach to Terraria's window thread.");
                window = game.Window.Handle;
            }
            if (renderer == null || renderer.Device != device)
            {
                renderer?.Dispose();
                renderer = new XnaUiRenderer(device, Native);
            }
            renderer.DrawBackbuffer();
        }

        public void Dispose()
        {
            // Remove the native window callback before the bridge is allowed to unload.
            if (Native.Shutdown() == 0)
                throw new InvalidOperationException("Another window hook still chains through ImGui; native bridge must remain loaded.");
            renderer?.Dispose();
            renderer = null;
            window = IntPtr.Zero;
        }
    }
}

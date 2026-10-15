using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SpookyFisher;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // A hidden HWND supplies a real XNA device. No Terraria process or visible test window is opened.
        IntPtr window = CreateWindowExW(0, "STATIC", "SpookyFisher UI tests", 0x80000000,
            0, 0, 1024, 768, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        NativeUi native = null;
        WindowProcedure inputObserver = null;
        int forwardedMouseDown = 0, forwardedKeyDown = 0;
        try
        {
            Assert(window != IntPtr.Zero, "Could not create hidden test window.");
            IntPtr originalProc = GetWindowLongPtrW(window, -4);
            inputObserver = (hwnd, message, wParam, lParam) =>
            {
                if (message == 0x0201) forwardedMouseDown++;
                if (message == 0x0100) forwardedKeyDown++;
                return CallWindowProcW(originalProc, hwnd, message, wParam, lParam);
            };
            SetWindowLongPtrW(window, -4, Marshal.GetFunctionPointerForDelegate(inputObserver));
            Assert(LoadLibraryW(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpookyFisher.dll")) != IntPtr.Zero,
                "Native UI DLL did not load.");
            native = new NativeUi();
            Assert(native.Initialize(window) == 1, "UI initialization failed.");
            Assert(native.CaptureInput() == 0, "Menu should start hidden.");
            PostMessageW(window, 0x0100, new IntPtr(0x2D), new IntPtr(1));
            PumpMessages();
            Assert(native.Visible() == 1, "Queued Insert was lost before window-message preprocessing.");
            PostMessageW(window, 0x0101, new IntPtr(0x2D), IntPtr.Zero);
            PostMessageW(window, 0x0100, new IntPtr(0x2D), new IntPtr(1));
            PostMessageW(window, 0x0101, new IntPtr(0x2D), IntPtr.Zero);
            PumpMessages();
            Assert(native.Visible() == 0, "Queued Insert did not close the menu.");
            Assert(Marshal.SizeOf(typeof(NativeUi.DrawCommand)) == 40, "Draw-command ABI mismatch.");
            Assert(Marshal.SizeOf(typeof(NativeUi.Frame)) == 36, "Frame ABI mismatch.");
            Assert(Marshal.SizeOf(typeof(NativeUi.FishingState)) == 36, "Fishing state ABI mismatch.");
            var fishing = new NativeUi.FishingState
            {
                CanStart = 1, HasAim = 0, BaitPower = 35
            };
            native.SetFishing(ref fishing, "Click Start, then cast once into the water.", "Sitting Duck's Fishing Pole");
            Assert(native.TakeActions() == 0, "Publishing a snapshot triggered a fishing action.");
            IntPtr font;
            int fontWidth, fontHeight;
            Assert(native.Font(out font, out fontWidth, out fontHeight) == 1 && font != IntPtr.Zero && fontWidth > 0,
                "Font atlas was not built.");
            NativeUi.Frame frame;
            Assert(native.BuildFrame(1024, 768, out frame) == 1 && frame.VertexCount == 0, "Hidden menu emitted geometry.");
            SendMessageW(window, 0x0100, new IntPtr(0x2D), new IntPtr(1));
            Assert(native.Visible() == 1, "Insert did not open the menu.");
            SendMessageW(window, 0x0100, new IntPtr(0x2D), new IntPtr(0x40000001));
            Assert(native.Visible() == 1, "A repeated Insert keydown toggled the menu.");
            Console.WriteLine("PASS Insert toggle, key repeat, hidden frame, native ABI");

            var parameters = new PresentationParameters
            {
                BackBufferWidth = 1024, BackBufferHeight = 768, DeviceWindowHandle = window,
                IsFullScreen = false, DepthStencilFormat = DepthFormat.None,
                PresentationInterval = PresentInterval.Immediate
            };
            using (var device = new GraphicsDevice(GraphicsAdapter.DefaultAdapter, GraphicsProfile.Reach, parameters))
            using (var target = new RenderTarget2D(device, 1024, 768, false, SurfaceFormat.Color, DepthFormat.None))
            using (var renderer = new XnaUiRenderer(device, native))
            {
                Parallel.For(0, 2200, i => native.WriteLog("Concurrent managed log " + i + " — Unicode test"));
                native.WriteLog("Renderer test: native bootstrap and managed messages share this Console.");
                device.SetRenderTarget(target);
                renderer.Prepare();
                var blend = device.BlendState;
                var rasterizer = device.RasterizerState;
                var depth = device.DepthStencilState;
                var sampler = device.SamplerStates[0];
                var scissor = device.ScissorRectangle;
                for (int i = 0; i < 3; i++)
                {
                    device.Clear(Color.CornflowerBlue);
                    Assert(native.BuildFrame(1024, 768, out frame) == 1 && frame.VertexCount > 0,
                        "Visible menu did not produce draw data.");
                    ValidateFrame(frame);
                    renderer.Draw(frame);
                }
                Assert(ReferenceEquals(device.BlendState, blend) && ReferenceEquals(device.RasterizerState, rasterizer) &&
                    ReferenceEquals(device.DepthStencilState, depth) && ReferenceEquals(device.SamplerStates[0], sampler) &&
                    device.ScissorRectangle == scissor, "Renderer did not restore Terraria's graphics state.");
                device.SetRenderTarget(null);
                var pixels = new Color[1024 * 768];
                target.GetData(pixels);
                int changed = 0;
                foreach (var pixel in pixels) if (pixel != Color.CornflowerBlue) changed++;
                Assert(changed > 50000, "XNA did not render the menu into the target.");
                using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "menu-preview.png")))
                    target.SaveAsPng(output, 1024, 768);
                Console.WriteLine("PASS XNA rendering, clipping, concurrent logs, graphics state restoration");

                MoveMouse(window, native, 900, 700);
                Assert(native.CaptureInput() == 0, "The open menu blocked Terraria outside its bounds.");
                int beforeMouse = forwardedMouseDown;
                ClickMouse(window, native, 900, 700);
                Assert(forwardedMouseDown == beforeMouse + 1, "A world click was swallowed by the window hook.");
                MoveMouse(window, native, 120, 120);
                Assert((native.CaptureInput() & NativeUi.MouseCapture) != 0, "The panel did not capture mouse input.");
                Assert((native.CaptureInput() & NativeUi.KeyboardCapture) == 0, "Hovering the panel captured game keyboard input.");
                int beforeKey = forwardedKeyDown;
                SendMessageW(window, 0x0100, new IntPtr(0x41), new IntPtr(1));
                SendMessageW(window, 0x0101, new IntPtr(0x41), IntPtr.Zero);
                Assert(forwardedKeyDown == beforeKey + 1, "Hovering the panel swallowed a game key.");
                beforeMouse = forwardedMouseDown;
                ClickMouse(window, native, 290, 208);
                Assert(forwardedMouseDown == beforeMouse, "A fishing button click leaked into Terraria.");
                Assert(native.TakeActions() == NativeUi.StartFishing && native.TakeActions() == 0,
                    "Start did not deliver exactly one action.");
                fishing.Enabled = 2;
                native.SetFishing(ref fishing, "Waiting for your first manual cast.", "Sitting Duck's Fishing Pole");
                ClickMouse(window, native, 290, 208);
                Assert(native.TakeActions() == NativeUi.StopFishing, "Cancel start did not deliver a stop action.");
                fishing.Enabled = fishing.HasAim = 1;
                fishing.Retrievals = 7;
                native.SetFishing(ref fishing, "Waiting for a bite.", "Sitting Duck's Fishing Pole");
                ClickMouse(window, native, 290, 208);
                Assert(native.TakeActions() == NativeUi.StopFishing, "Stop did not deliver a stop action.");
                fishing.Enabled = fishing.CanStart = 0;
                native.SetFishing(ref fishing, "Game is paused.", "Sitting Duck's Fishing Pole");
                ClickMouse(window, native, 290, 208);
                Assert(native.TakeActions() == 0, "A disabled Start button emitted an action.");
                fishing.CanStart = 1;
                native.SetFishing(ref fishing, "Click Start, then cast once into the water.", "Sitting Duck's Fishing Pole");

                // Follow a panel-owned press outside the panel; the game must not receive it mid-drag.
                MoveMouse(window, native, 120, 120);
                SendMessageW(window, 0x0201, new IntPtr(1), new IntPtr(120 | (120 << 16)));
                native.BuildFrame(1024, 768, out frame);
                MoveMouse(window, native, 900, 700);
                Assert((native.CaptureInput() & NativeUi.MouseCapture) != 0, "An owned drag escaped into Terraria.");
                SendMessageW(window, 0x0202, IntPtr.Zero, new IntPtr(900 | (700 << 16)));
                MoveMouse(window, native, 900, 700);
                Assert(native.CaptureInput() == 0, "Moving off the panel did not restore mouse pass-through.");
                Console.WriteLine("PASS mouse pass-through, button isolation, dragging, keyboard independence, start/stop actions");

                ClickMouse(window, native, 132, 153);
                ClickMouse(window, native, 400, 144);
                Assert((native.CaptureInput() & NativeUi.KeyboardCapture) != 0, "Console filter did not capture typing.");
                beforeKey = forwardedKeyDown;
                PostMessageW(window, 0x0100, new IntPtr(0x41), new IntPtr(1));
                PostMessageW(window, 0x0101, new IntPtr(0x41), IntPtr.Zero);
                PumpMessages();
                Assert(forwardedKeyDown == beforeKey, "Console typing leaked through the message hook.");
                ClickMouse(window, native, 900, 700);
                Assert(native.CaptureInput() == 0, "Clicking back into Terraria did not release keyboard capture.");
                ClickMouse(window, native, 132, 112);
                ClickMouse(window, native, 132, 474);
                Assert(native.TakeActions() == NativeUi.Detach && native.TakeActions() == 0,
                    "Detach did not deliver exactly one action.");
                Console.WriteLine("PASS console typing isolation, return to game, detach action");

                using (var scene = new RenderTarget2D(device, 256, 192, false, SurfaceFormat.Color,
                    DepthFormat.None, 0, RenderTargetUsage.PreserveContents))
                {
                    device.Clear(Color.CornflowerBlue);
                    device.SetRenderTarget(scene);
                    device.Clear(Color.Green);
                    device.Viewport = new Viewport(16, 12, 128, 96);
                    device.ScissorRectangle = new Rectangle(18, 14, 100, 80);
                    var sceneViewport = device.Viewport;
                    var sceneScissor = device.ScissorRectangle;
                    renderer.DrawBackbuffer();
                    var bindings = device.GetRenderTargets();
                    Assert(bindings.Length == 1 && ReferenceEquals(bindings[0].RenderTarget, scene) &&
                        device.Viewport.Equals(sceneViewport) && device.ScissorRectangle == sceneScissor,
                        "Drawing at full resolution did not restore the scene target, viewport, and scissor.");
                    device.SetRenderTarget(null);
                    var scenePixels = new Color[256 * 192];
                    scene.GetData(scenePixels);
                    foreach (var pixel in scenePixels)
                        Assert(pixel == Color.Green, "The overlay was rendered into Terraria's reduced scene target.");
                }
                Console.WriteLine("PASS full-resolution backbuffer UI and restoration of reduced scene target/viewport");

                int normalRevision = native.ConfigureDisplay(1024, 768);
                native.Font(out font, out fontWidth, out fontHeight);
                int normalAtlasArea = fontWidth * fontHeight;
                float logicalWidth = frame.Width, logicalHeight = frame.Height;
                using (var denseTarget = new RenderTarget2D(device, 2048, 1536, false, SurfaceFormat.Color, DepthFormat.None))
                {
                    device.SetRenderTarget(denseTarget);
                    renderer.Prepare();
                    int denseRevision = native.ConfigureDisplay(2048, 1536);
                    native.Font(out font, out fontWidth, out fontHeight);
                    Assert(denseRevision != normalRevision && fontWidth * fontHeight > normalAtlasArea,
                        "2x rendering reused the low-resolution font atlas.");
                    Assert(native.ConfigureDisplay(2048, 1536) == denseRevision, "An unchanged density rebuilt the atlas.");
                    device.Clear(Color.CornflowerBlue);
                    Assert(native.BuildFrame(2048, 1536, out frame) == 1 && frame.Width == logicalWidth && frame.Height == logicalHeight,
                        "Pixel density changed the menu's logical size or input coordinates.");
                    renderer.Draw(frame);
                    device.SetRenderTarget(null);
                    using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "menu-preview-2x.png")))
                        denseTarget.SaveAsPng(output, 2048, 1536);
                }
                renderer.Prepare();
                Assert(native.ConfigureDisplay(1024, 768) != normalRevision, "Returning to 1x did not refresh the atlas.");
                Console.WriteLine("PASS 2x font rasterization, atlas caching, logical sizing, density changes");

                // Exercise GPU resource recreation through a real device reset and a different viewport size.
                parameters.BackBufferWidth = 800;
                parameters.BackBufferHeight = 600;
                device.Reset(parameters);
                renderer.Prepare();
                Assert(native.BuildFrame(800, 600, out frame) == 1, "Frame failed after device reset.");
                renderer.Draw(frame);
                Console.WriteLine("PASS device reset and viewport resize");
            }
            SendMessageW(window, 0x0100, new IntPtr(0x2D), new IntPtr(1));
            Assert(native.CaptureInput() == 0, "Insert did not close the menu.");
            Assert(native.BuildFrame(1024, 768, out frame) == 1 && frame.VertexCount == 0,
                "Closed menu kept rendering.");
            Assert(native.Shutdown() == 1 && native.Shutdown() == 1, "Shutdown was not safe to repeat.");
            Assert(native.Initialize(window) == 1, "Reattachment failed.");
            SendMessageW(window, 0x0100, new IntPtr(0x2D), new IntPtr(1));
            IntPtr overlayProc = GetWindowLongPtrW(window, -4);
            WindowProcedure chainedProc = (hwnd, message, wParam, lParam) =>
                CallWindowProcW(overlayProc, hwnd, message, wParam, lParam);
            SetWindowLongPtrW(window, -4, Marshal.GetFunctionPointerForDelegate(chainedProc));
            Assert(native.Shutdown() == 0, "Shutdown removed another component's window hook.");
            Assert(native.CaptureInput() == 0, "Deferred shutdown kept blocking Terraria's input.");
            SetWindowLongPtrW(window, -4, overlayProc);
            Assert(native.Shutdown() == 1, "Shutdown did not recover after the other hook was removed.");
            GC.KeepAlive(chainedProc);
            Console.WriteLine("PASS close, shutdown, reattachment, chained-hook unload protection");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            native?.Shutdown();
            if (window != IntPtr.Zero) DestroyWindow(window);
            GC.KeepAlive(inputObserver);
        }
    }

    private static unsafe void ValidateFrame(NativeUi.Frame frame)
    {
        var commands = (NativeUi.DrawCommand*)frame.Commands;
        var indices = (ushort*)frame.Indices;
        for (int i = 0; i < frame.CommandCount; i++)
        {
            var command = commands[i];
            Assert(command.TextureId == 1 && command.ElementCount % 3 == 0, "Unsupported texture or malformed triangle data.");
            Assert(command.IndexOffset >= 0 && command.IndexOffset + command.ElementCount <= frame.IndexCount,
                "Draw command reads beyond the index buffer.");
            for (int j = 0; j < command.ElementCount; j++)
                Assert(command.VertexOffset + indices[command.IndexOffset + j] < frame.VertexCount,
                    "Draw command reads beyond the vertex buffer.");
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void MoveMouse(IntPtr window, NativeUi native, int x, int y)
    {
        SendMessageW(window, 0x0200, IntPtr.Zero, new IntPtr(x | (y << 16)));
        NativeUi.Frame frame;
        for (int i = 0; i < 3; i++)
            Assert(native.BuildFrame(1024, 768, out frame) == 1, "Mouse input frame failed.");
    }

    private static void ClickMouse(IntPtr window, NativeUi native, int x, int y)
    {
        MoveMouse(window, native, x, y);
        var position = new IntPtr(x | (y << 16));
        NativeUi.Frame frame;
        SendMessageW(window, 0x0201, new IntPtr(1), position);
        Assert(native.BuildFrame(1024, 768, out frame) == 1, "Mouse-down frame failed.");
        SendMessageW(window, 0x0202, IntPtr.Zero, position);
        Assert(native.BuildFrame(1024, 768, out frame) == 1, "Mouse-up frame failed.");
        Assert(native.BuildFrame(1024, 768, out frame) == 1, "Mouse release frame failed.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal IntPtr Window;
        internal uint Id;
        internal IntPtr WParam, LParam;
        internal uint Time;
        internal int X, Y;
    }

    private static void PumpMessages()
    {
        Message message;
        while (PeekMessageW(out message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryW(string path);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DispatchMessageW(ref Message message);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    // This test executable is x86; these aliases are Win32's pointer-sized entry points.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CallWindowProcW(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

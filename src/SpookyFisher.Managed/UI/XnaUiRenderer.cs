using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SpookyFisher
{
    internal sealed class XnaUiRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex : IVertexType
        {
            internal Vector2 Position, TexCoord;
            internal Color Color;
            private static readonly VertexDeclaration declaration = new VertexDeclaration(
                new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
                new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
                new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0));
            VertexDeclaration IVertexType.VertexDeclaration => declaration;
        }

        private readonly GraphicsDevice device;
        private readonly NativeUi native;
        private readonly RasterizerState rasterizer = new RasterizerState
        {
            CullMode = CullMode.None, ScissorTestEnable = true
        };
        private BasicEffect effect;
        private Texture2D font;
        private DynamicVertexBuffer vertexBuffer;
        private DynamicIndexBuffer indexBuffer;
        private int fontRevision;
        private Vertex[] vertices = new Vertex[0];
        private ushort[] indices = new ushort[0];

        internal XnaUiRenderer(GraphicsDevice device, NativeUi native)
        {
            this.device = device;
            this.native = native;
            device.DeviceResetting += OnResetting;
        }

        internal GraphicsDevice Device => device;

        internal void DrawBackbuffer()
        {
            var presentation = device.PresentationParameters;
            Prepare(presentation.BackBufferWidth, presentation.BackBufferHeight);
            NativeUi.Frame frame;
            if (native.BuildFrame(presentation.BackBufferWidth, presentation.BackBufferHeight, out frame) == 0)
                throw new InvalidOperationException("ImGui could not build the menu frame.");
            if (frame.VertexCount == 0 || frame.IndexCount == 0) return;
            var targets = device.GetRenderTargets();
            var viewport = device.Viewport;
            var scissor = device.ScissorRectangle;
            try
            {
                // Terraria can leave a reduced scene viewport/target bound. UI belongs on
                // the final backbuffer, where font pixels match the displayed resolution.
                if (targets.Length != 0) device.SetRenderTarget(null);
                device.Viewport = new Viewport(0, 0, presentation.BackBufferWidth, presentation.BackBufferHeight);
                Draw(frame);
            }
            finally
            {
                if (targets.Length != 0) device.SetRenderTargets(targets);
                device.Viewport = viewport;
                device.ScissorRectangle = scissor;
            }
        }

        // Called only on the game's graphics thread, including after device reset.
        internal void Prepare()
        {
            Prepare(device.Viewport.Width, device.Viewport.Height);
        }

        private void Prepare(int framebufferWidth, int framebufferHeight)
        {
            int revision = native.ConfigureDisplay(framebufferWidth, framebufferHeight);
            if (revision == 0) throw new InvalidOperationException("ImGui display configuration failed.");
            if (font != null && !font.IsDisposed && fontRevision == revision) return;
            effect?.Dispose(); effect = null;
            font?.Dispose(); font = null;
            IntPtr pixels;
            int width, height;
            if (native.Font(out pixels, out width, out height) == 0)
                throw new InvalidOperationException("ImGui font atlas is unavailable.");
            var data = new byte[checked(width * height * 4)];
            Marshal.Copy(pixels, data, 0, data.Length);
            font = new Texture2D(device, width, height, false, SurfaceFormat.Color);
            font.SetData(data);
            effect = new BasicEffect(device)
            {
                TextureEnabled = true, VertexColorEnabled = true, Texture = font,
                View = Matrix.Identity
            };
            fontRevision = revision;
        }

        internal unsafe void Draw(NativeUi.Frame frame)
        {
            if (frame.VertexCount == 0 || frame.IndexCount == 0 || frame.Width <= 0 || frame.Height <= 0) return;
            Prepare();
            EnsureBuffers(frame.VertexCount, frame.IndexCount);
            var sourceVertices = (Vertex*)frame.Vertices;
            var sourceIndices = (ushort*)frame.Indices;
            for (int i = 0; i < frame.VertexCount; i++) vertices[i] = sourceVertices[i];
            for (int i = 0; i < frame.IndexCount; i++) indices[i] = sourceIndices[i];
            vertexBuffer.SetData(vertices, 0, frame.VertexCount, SetDataOptions.Discard);
            indexBuffer.SetData(indices, 0, frame.IndexCount, SetDataOptions.Discard);

            var oldBlend = device.BlendState;
            var oldDepth = device.DepthStencilState;
            var oldRasterizer = device.RasterizerState;
            var oldSampler = device.SamplerStates[0];
            var oldTexture = device.Textures[0];
            var oldVertices = device.GetVertexBuffers();
            var oldIndices = device.Indices;
            var oldViewport = device.Viewport;
            var oldScissor = device.ScissorRectangle;
            try
            {
                device.BlendState = BlendState.NonPremultiplied;
                device.DepthStencilState = DepthStencilState.None;
                device.RasterizerState = rasterizer;
                device.SamplerStates[0] = SamplerState.LinearClamp;
                device.SetVertexBuffer(vertexBuffer);
                device.Indices = indexBuffer;
                effect.Projection = Matrix.CreateOrthographicOffCenter(0, frame.Width, frame.Height, 0, 0, 1);
                float scaleX = oldViewport.Width / frame.Width, scaleY = oldViewport.Height / frame.Height;
                // XNA/Direct3D 9 needs a half framebuffer pixel, not half a logical UI pixel.
                effect.World = Matrix.CreateTranslation(-0.5f / scaleX, -0.5f / scaleY, 0);
                var commands = (NativeUi.DrawCommand*)frame.Commands;
                for (int i = 0; i < frame.CommandCount; i++)
                {
                    var command = commands[i];
                    if (command.TextureId != 1)
                        throw new NotSupportedException("Register additional UI textures before drawing images.");
                    int left = Math.Max(0, (int)Math.Floor(command.Left * scaleX));
                    int top = Math.Max(0, (int)Math.Floor(command.Top * scaleY));
                    int right = Math.Min(oldViewport.Width, (int)Math.Ceiling(command.Right * scaleX));
                    int bottom = Math.Min(oldViewport.Height, (int)Math.Ceiling(command.Bottom * scaleY));
                    if (right <= left || bottom <= top || command.ElementCount == 0) continue;
                    device.ScissorRectangle = new Rectangle(oldViewport.X + left, oldViewport.Y + top, right - left, bottom - top);
                    foreach (var pass in effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        device.DrawIndexedPrimitives(PrimitiveType.TriangleList, command.VertexOffset,
                            0, command.VertexCount, command.IndexOffset, command.ElementCount / 3);
                    }
                }
            }
            finally
            {
                device.SetVertexBuffers(oldVertices);
                device.Indices = oldIndices;
                device.Textures[0] = oldTexture;
                device.SamplerStates[0] = oldSampler;
                device.BlendState = oldBlend;
                device.DepthStencilState = oldDepth;
                device.RasterizerState = oldRasterizer;
                device.Viewport = oldViewport;
                device.ScissorRectangle = oldScissor;
            }
        }

        private void EnsureBuffers(int vertexCount, int indexCount)
        {
            if (vertexBuffer == null || vertices.Length < vertexCount)
            {
                vertexBuffer?.Dispose();
                vertices = new Vertex[Math.Max(1024, checked(vertexCount * 2))];
                vertexBuffer = new DynamicVertexBuffer(device, typeof(Vertex), vertices.Length, BufferUsage.WriteOnly);
            }
            if (indexBuffer == null || indices.Length < indexCount)
            {
                indexBuffer?.Dispose();
                indices = new ushort[Math.Max(2048, checked(indexCount * 2))];
                indexBuffer = new DynamicIndexBuffer(device, IndexElementSize.SixteenBits, indices.Length, BufferUsage.WriteOnly);
            }
        }

        private void OnResetting(object sender, EventArgs args) => DisposeResources();

        private void DisposeResources()
        {
            vertexBuffer?.Dispose(); vertexBuffer = null;
            indexBuffer?.Dispose(); indexBuffer = null;
            effect?.Dispose(); effect = null;
            font?.Dispose(); font = null;
        }

        public void Dispose()
        {
            device.DeviceResetting -= OnResetting;
            DisposeResources();
            rasterizer.Dispose();
        }
    }
}

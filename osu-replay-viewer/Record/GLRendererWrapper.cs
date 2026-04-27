using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osuTK.Graphics.ES30;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace osu_replay_renderer_netcore.Record;

public sealed class GLRendererWrapper : RenderWrapper
{
    private static readonly Type GLRendererType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.OpenGL.GLRenderer")
        ?? throw new InvalidOperationException("GLRenderer type not found.");

    private static readonly FieldInfo GLRendererOpenGLSurfaceField =
        GLRendererType.GetField("openGLSurface", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("openGLSurface field not found.");

    private const int PboCount = 3;

    private readonly IGraphicsSurface surface;
    private readonly IOpenGLGraphicsSurface openGLSurface;

    private int shaderProgram;
    private int yuvFbo;
    private int yuvTexture;
    private int sourceTexture;
    private int vao;
    private int vbo;

    private int uTextureLocation;
    private int uResolutionLocation;
    private int uPixelFormatLocation;
    private int uColorSpaceLocation;

    private bool resourcesInitialized;

    private readonly int[] pboIds = new int[PboCount];
    private readonly Queue<int> pendingPbos = new();

    private int pboIndex;
    private bool pboInitialized;
    private int pboSize;

    private readonly int yuvBufferSize;
    private readonly int yuvFboHeight;
    private readonly int pixelFormatUniform;
    private readonly int colorSpaceUniform;

    public static bool IsSupported(IRenderer renderer)
    {
        return renderer.GetType() == GLRendererType;
    }

    public GLRendererWrapper(
        IRenderer renderer,
        Size desiredSize,
        PixelFormatMode pixelFormat,
        ColorSpaceMode colorSpace)
        : base(desiredSize, pixelFormat, colorSpace)
    {
        if (renderer.GetType() != GLRendererType)
            throw new ArgumentException($"Not supported renderer: {renderer.GetType()}");

        object? graphicsSurfaceObj = GLRendererOpenGLSurfaceField.GetValue(renderer);

        if (graphicsSurfaceObj is null)
            throw new InvalidOperationException("graphicsSurface is null.");

        if (graphicsSurfaceObj is not IOpenGLGraphicsSurface oglSurface ||
            graphicsSurfaceObj is not IGraphicsSurface gfxSurface)
        {
            throw new NotSupportedException(
                $"graphicsSurface has unexpected type: {graphicsSurfaceObj.GetType()}");
        }

        surface = gfxSurface;
        openGLSurface = oglSurface;

        (yuvBufferSize, yuvFboHeight) = GetBufferSizeAndHeight(desiredSize, pixelFormat);

        pixelFormatUniform = GetPixelFormatUniform(pixelFormat);
        colorSpaceUniform = GetColorSpaceUniform(colorSpace);
    }

    private void WithGLContext(Action action)
    {
        IntPtr windowContext = openGLSurface.WindowContext;

        if (windowContext == IntPtr.Zero)
            throw new InvalidOperationException("OpenGL window context is not available.");

        IntPtr previousContext = openGLSurface.CurrentContext;
        bool switchedContext = previousContext != windowContext;

        if (switchedContext)
            openGLSurface.MakeCurrent(windowContext);

        try
        {
            action();
        }
        finally
        {
            if (switchedContext)
            {

                if (previousContext != IntPtr.Zero)
                    openGLSurface.MakeCurrent(previousContext);
                else
                    openGLSurface.ClearCurrent();
            }
        }
    }

    private static (int bufferSize, int fboHeight) GetBufferSizeAndHeight(
        Size size,
        PixelFormatMode pixelFormat)
    {
        return pixelFormat switch
        {
            PixelFormatMode.YUV444 => (size.Width * size.Height * 3, size.Height * 3),
            PixelFormatMode.NV12 => (size.Width * size.Height * 3 / 2, size.Height * 3 / 2),
            _ => (size.Width * size.Height * 3 / 2, size.Height * 3 / 2)
        };
    }

    private static int GetPixelFormatUniform(PixelFormatMode pixelFormat)
    {
        return pixelFormat switch
        {
            PixelFormatMode.YUV444 => 1,
            PixelFormatMode.NV12 => 2,
            _ => 0
        };
    }

    private static int GetColorSpaceUniform(ColorSpaceMode colorSpace)
    {
        return colorSpace switch
        {
            ColorSpaceMode.BT601 => 0,
            _ => 1
        };
    }

    public override void WriteFrame(EncoderBase encoder)
    {
        Size drawableSize = surface.GetDrawableSize();

        if (drawableSize.Width != DesiredSize.Width || drawableSize.Height != DesiredSize.Height)
        {
            Console.WriteLine($"Skipped frame: drawable={drawableSize}, desired={DesiredSize}");
            return;
        }

        WithGLContext(() =>
        {
            if (PixelFormat == PixelFormatMode.RGB)
                WriteFrameRGB(encoder, drawableSize);
            else
                WriteFrameYUV(encoder);
        });
    }

    private void WriteFrameYUV(EncoderBase encoder)
    {
        InitializeResources();
        InitializePBOs(yuvBufferSize);

        int[] oldViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, oldViewport);

        GL.GetInteger(GetPName.FramebufferBinding, out int oldFramebuffer);

        bool oldScissor = GL.IsEnabled(EnableCap.ScissorTest);
        bool oldBlend = GL.IsEnabled(EnableCap.Blend);
        bool oldDepthTest = GL.IsEnabled(EnableCap.DepthTest);
        bool oldCullFace = GL.IsEnabled(EnableCap.CullFace);

        try
        {
            /*
             * Important:
             * Copy the already rendered osu! frame BEFORE binding our own YUV FBO.
             * GL.CopyTexSubImage2D reads from the currently bound read framebuffer.
             */
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, sourceTexture);

            GL.CopyTexSubImage2D(
                All.Texture2D,
                0,
                0,
                0,
                0,
                0,
                DesiredSize.Width,
                DesiredSize.Height);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, yuvFbo);
            GL.Viewport(0, 0, DesiredSize.Width, yuvFboHeight);

            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);

            GL.UseProgram(shaderProgram);

            GL.Uniform1(uTextureLocation, 0);
            GL.Uniform2(uResolutionLocation, (float)DesiredSize.Width, (float)DesiredSize.Height);
            GL.Uniform1(uPixelFormatLocation, pixelFormatUniform);
            GL.Uniform1(uColorSpaceLocation, colorSpaceUniform);

            GL.BindVertexArray(vao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            GL.BindVertexArray(0);

            ReadToPboAndWriteOldestReady(
                encoder,
                DesiredSize.Width,
                yuvFboHeight,
                osuTK.Graphics.ES30.PixelFormat.Red,
                yuvBufferSize);
        }
        finally
        {
            GL.UseProgram(0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, oldFramebuffer);
            GL.Viewport(oldViewport[0], oldViewport[1], oldViewport[2], oldViewport[3]);

            RestoreCap(EnableCap.ScissorTest, oldScissor);
            RestoreCap(EnableCap.Blend, oldBlend);
            RestoreCap(EnableCap.DepthTest, oldDepthTest);
            RestoreCap(EnableCap.CullFace, oldCullFace);
        }
    }

    private void WriteFrameRGB(EncoderBase encoder, Size size)
    {
        int rgbBufferSize = DesiredSize.Width * DesiredSize.Height * 3;

        InitializePBOs(rgbBufferSize);

        ReadToPboAndWriteOldestReady(
            encoder,
            size.Width,
            size.Height,
            osuTK.Graphics.ES30.PixelFormat.Rgb,
            rgbBufferSize);
    }

    private static void RestoreCap(EnableCap cap, bool enabled)
    {
        if (enabled)
            GL.Enable(cap);
        else
            GL.Disable(cap);
    }

    private void InitializeResources()
    {
        if (resourcesInitialized)
            return;

        string basePath = AppDomain.CurrentDomain.BaseDirectory;

        string vertexShaderSource = File.ReadAllText(
            Path.Combine(basePath, "Record", "Shaders", "yuv_converter.vert"));

        string fragmentShaderSource = File.ReadAllText(
            Path.Combine(basePath, "Record", "Shaders", "yuv_converter.frag"));

        int vertexShader = CompileShader(ShaderType.VertexShader, vertexShaderSource);
        int fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentShaderSource);

        shaderProgram = GL.CreateProgram();

        GL.AttachShader(shaderProgram, vertexShader);
        GL.AttachShader(shaderProgram, fragmentShader);
        GL.LinkProgram(shaderProgram);

        GL.GetProgram(shaderProgram, All.LinkStatus, out int linkStatus);

        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);

        if (linkStatus == 0)
        {
            string infoLog = GL.GetProgramInfoLog(shaderProgram);
            GL.DeleteProgram(shaderProgram);
            shaderProgram = 0;

            throw new InvalidOperationException($"Shader program link failed: {infoLog}");
        }

        uTextureLocation = GL.GetUniformLocation(shaderProgram, "uTexture");
        uResolutionLocation = GL.GetUniformLocation(shaderProgram, "uResolution");
        uPixelFormatLocation = GL.GetUniformLocation(shaderProgram, "uPixelFormat");
        uColorSpaceLocation = GL.GetUniformLocation(shaderProgram, "uColorSpace");

        CreateSourceTexture();
        CreateYuvTextureAndFramebuffer();
        CreateFullscreenQuad();

        resourcesInitialized = true;
    }

    private void CreateSourceTexture()
    {
        sourceTexture = GL.GenTexture();

        GL.BindTexture(TextureTarget.Texture2D, sourceTexture);

        GL.TexImage2D(
            All.Texture2D,
            0,
            All.Rgb,
            DesiredSize.Width,
            DesiredSize.Height,
            0,
            All.Rgb,
            All.UnsignedByte,
            IntPtr.Zero);

        SetNearestClampTextureParameters();

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void CreateYuvTextureAndFramebuffer()
    {
        yuvTexture = GL.GenTexture();

        GL.BindTexture(TextureTarget.Texture2D, yuvTexture);

        GL.TexImage2D(
            All.Texture2D,
            0,
            (All)0x8229, // GL_R8
            DesiredSize.Width,
            yuvFboHeight,
            0,
            All.Red,
            All.UnsignedByte,
            IntPtr.Zero);

        SetNearestClampTextureParameters();

        GL.GenFramebuffers(1, out yuvFbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, yuvFbo);

        GL.FramebufferTexture2D(
            All.Framebuffer,
            All.ColorAttachment0,
            All.Texture2D,
            yuvTexture,
            0);

        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);

        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException($"Framebuffer incomplete: {status}");

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static void SetNearestClampTextureParameters()
    {
        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);

        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);

        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);

        GL.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
    }

    private void CreateFullscreenQuad()
    {
        float[] vertices =
        {
            -1f, -1f,
             1f, -1f,
            -1f,  1f,
             1f,  1f
        };

        GL.GenVertexArrays(1, out vao);
        GL.BindVertexArray(vao);

        GL.GenBuffers(1, out vbo);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);

        GL.BufferData(
            BufferTarget.ArrayBuffer,
            vertices.Length * sizeof(float),
            vertices,
            BufferUsageHint.StaticDraw);

        int aPosition = GL.GetAttribLocation(shaderProgram, "aPosition");

        if (aPosition < 0)
            throw new InvalidOperationException("Shader attribute 'aPosition' not found.");

        GL.EnableVertexAttribArray(aPosition);

        GL.VertexAttribPointer(
            aPosition,
            2,
            VertexAttribPointerType.Float,
            false,
            0,
            0);

        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);
    }

    private static int CompileShader(ShaderType shaderType, string source)
    {
        int shader = GL.CreateShader(shaderType);

        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);

        if (success == 0)
        {
            string infoLog = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);

            throw new InvalidOperationException($"{shaderType} compilation failed: {infoLog}");
        }

        return shader;
    }

    private unsafe void ReadToPboAndWriteOldestReady(
        EncoderBase encoder,
        int width,
        int height,
        osuTK.Graphics.ES30.PixelFormat format,
        int size)
    {
        int currentPbo = pboIds[pboIndex % PboCount];
        pboIndex++;

        GL.BindBuffer(BufferTarget.PixelPackBuffer, currentPbo);

        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);

        GL.ReadPixels(
            0,
            0,
            width,
            height,
            format,
            PixelType.UnsignedByte,
            IntPtr.Zero);

        GL.PixelStore(PixelStoreParameter.PackAlignment, 4);

        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

        pendingPbos.Enqueue(currentPbo);

        /*
         * Keep a few frames of distance between glReadPixels and glMapBufferRange.
         * This reduces CPU stalls because the GPU has more time to finish the transfer.
         */
        if (pendingPbos.Count >= PboCount)
            WriteOldestPendingPbo(encoder, size);
    }

    private unsafe void WriteOldestPendingPbo(EncoderBase encoder, int size)
    {
        int pbo = pendingPbos.Dequeue();

        GL.BindBuffer(BufferTarget.PixelPackBuffer, pbo);

        IntPtr dataPtr = GL.MapBufferRange(
            BufferTarget.PixelPackBuffer,
            IntPtr.Zero,
            size,
            BufferAccessMask.MapReadBit);

        if (dataPtr != IntPtr.Zero)
        {
            try
            {
                var span = new ReadOnlySpan<byte>(dataPtr.ToPointer(), size);
                encoder.WriteFrame(span);
            }
            finally
            {
                GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
            }
        }

        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
    }

    private void InitializePBOs(int size)
    {
        if (pboInitialized && pboSize == size)
            return;

        if (pboInitialized)
        {
            GL.DeleteBuffers(PboCount, pboIds);
            pendingPbos.Clear();
        }

        GL.GenBuffers(PboCount, pboIds);

        for (int i = 0; i < PboCount; i++)
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[i]);

            GL.BufferData(
                BufferTarget.PixelPackBuffer,
                size,
                IntPtr.Zero,
                BufferUsageHint.StreamRead);
        }

        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

        pboInitialized = true;
        pboSize = size;
        pboIndex = 0;
        pendingPbos.Clear();
    }

    public override unsafe void Finish(EncoderBase encoder)
    {
        if (!pboInitialized && !resourcesInitialized)
            return;

        WithGLContext(() =>
        {
            while (pboInitialized && pendingPbos.Count > 0)
                WriteOldestPendingPbo(encoder, pboSize);

            DeletePBOs();
            DeleteResources();
        });
    }

    private void DeletePBOs()
    {
        if (!pboInitialized)
            return;

        GL.DeleteBuffers(PboCount, pboIds);

        for (int i = 0; i < pboIds.Length; i++)
            pboIds[i] = 0;

        pendingPbos.Clear();

        pboInitialized = false;
        pboSize = 0;
        pboIndex = 0;
    }

    private void DeleteResources()
    {
        if (!resourcesInitialized)
            return;

        if (vbo != 0)
            GL.DeleteBuffer(vbo);

        if (vao != 0)
            GL.DeleteVertexArray(vao);

        if (sourceTexture != 0)
            GL.DeleteTexture(sourceTexture);

        if (yuvTexture != 0)
            GL.DeleteTexture(yuvTexture);

        if (yuvFbo != 0)
            GL.DeleteFramebuffer(yuvFbo);

        if (shaderProgram != 0)
            GL.DeleteProgram(shaderProgram);

        vbo = 0;
        vao = 0;
        sourceTexture = 0;
        yuvTexture = 0;
        yuvFbo = 0;
        shaderProgram = 0;

        resourcesInitialized = false;
    }
}
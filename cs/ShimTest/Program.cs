using System.Runtime.InteropServices;
using SkiaSharp;

const int W = 800, H = 600;

var bytes = File.ReadAllBytes(@"C:\Users\ShaboldaV\Downloads\animated-login-screen.riv");
var file = Native.rive_file_load(bytes, bytes.Length);
if (file == IntPtr.Zero) { Console.WriteLine("не загрузился"); return; }

using var bitmap = new SKBitmap(W, H);
using var canvas = new SKCanvas(bitmap);
canvas.Clear(new SKColor(0xFF1E1E1E));

var pathCache = new Dictionary<int, SKPath>();
var opacityStack = new Stack<float>();
float opacity = 1f;
int draws = 0, gradients = 0;

SKPath GetPath(int id)
{
    if (pathCache.TryGetValue(id, out var cached)) return cached;

    int nv = Native.rive_path_verb_count(id);
    int np = Native.rive_path_point_count(id);
    var verbs = new byte[nv];
    var pts = new float[np * 2];
    Native.rive_path_copy(id, verbs, pts);

    var p = new SKPath
    {
        FillType = Native.rive_path_fill_rule(id) == 1
            ? SKPathFillType.EvenOdd : SKPathFillType.Winding
    };

    int k = 0;
    foreach (var v in verbs)
    {
        switch (v)
        {
            case 0: p.MoveTo(pts[k * 2], pts[k * 2 + 1]); k++; break;
            case 1: p.LineTo(pts[k * 2], pts[k * 2 + 1]); k++; break;
            case 2: p.QuadTo(pts[k * 2], pts[k * 2 + 1],
                             pts[(k + 1) * 2], pts[(k + 1) * 2 + 1]); k += 2; break;
            case 4: p.CubicTo(pts[k * 2], pts[k * 2 + 1],
                              pts[(k + 1) * 2], pts[(k + 1) * 2 + 1],
                              pts[(k + 2) * 2], pts[(k + 2) * 2 + 1]); k += 3; break;
            case 5: p.Close(); break;
        }
    }

    pathCache[id] = p;
    return p;
}

var cb = new Native.Callbacks
{
    save = _ => { canvas.Save(); opacityStack.Push(opacity); },
    restore = _ => { canvas.Restore(); if (opacityStack.Count > 0) opacity = opacityStack.Pop(); },
    transform = (_, m) =>
    {
        var v = new float[6];
        Marshal.Copy(m, v, 0, 6);
        var mat = new SKMatrix(v[0], v[2], v[4],
                               v[1], v[3], v[5],
                               0, 0, 1);
        canvas.Concat(in mat);
    },
    clipPath = (_, id) => canvas.ClipPath(GetPath(id), SKClipOperation.Intersect, true),
    drawPath = (_, pid, paintId) =>
    {
        Native.rive_paint_get(paintId, out int style, out uint color,
                              out float thickness, out int join,
                              out int cap, out int blend, out float feather,
                              out int shaderId);

        byte a = (byte)Math.Clamp((color >> 24) * opacity, 0, 255);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor((byte)(color >> 16), (byte)(color >> 8), (byte)color, a),
            Style = style == 1 ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
            StrokeWidth = thickness,
            StrokeJoin = (SKStrokeJoin)join,
            StrokeCap = (SKStrokeCap)cap,
        };

        if (shaderId >= 0)
        {
            int cnt = Native.rive_shader_info(shaderId, out int type,
                                              out float ga, out float gb,
                                              out float gc, out float gd);
            if (cnt > 0 && cnt < 1024)
            {
                var cols = new uint[cnt];
                var stops = new float[cnt];
                Native.rive_shader_stops(shaderId, cols, stops);

                var skCols = new SKColor[cnt];
                for (int i = 0; i < cnt; i++)
                {
                    byte ca = (byte)Math.Clamp((cols[i] >> 24) * opacity, 0, 255);
                    skCols[i] = new SKColor((byte)(cols[i] >> 16), (byte)(cols[i] >> 8),
                                            (byte)cols[i], ca);
                }

                paint.Shader = type == 0
                    ? SKShader.CreateLinearGradient(new SKPoint(ga, gb), new SKPoint(gc, gd),
                                                    skCols, stops, SKShaderTileMode.Clamp)
                    : SKShader.CreateRadialGradient(new SKPoint(ga, gb), gc,
                                                    skCols, stops, SKShaderTileMode.Clamp);
                gradients++;
            }
        }

        canvas.DrawPath(GetPath(pid), paint);
        draws++;
    },
    modulateOpacity = (_, o) => opacity *= o,
};
Native.rive_set_callbacks(cb);

var ab = Native.rive_artboard_instance(file, 0);
var sm = Native.rive_sm_instance(ab);
if (sm == IntPtr.Zero) sm = Native.rive_sm_instance_at(ab, 0);

for (int i = 0; i < 30; i++)
    if (sm != IntPtr.Zero) Native.rive_sm_advance(sm, 1f / 60f);
    else Native.rive_artboard_advance(ab, 1f / 60f);

Native.rive_artboard_draw_fit(ab, IntPtr.Zero, W, H);

var outPath = Path.Combine(AppContext.BaseDirectory, "frame.png");
using (var img = SKImage.FromBitmap(bitmap))
using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
using (var fs = File.OpenWrite(outPath))
    data.SaveTo(fs);

Console.WriteLine($"путей: {draws}, градиентов: {gradients}");
Console.WriteLine(outPath);

foreach (var p in pathCache.Values) p.Dispose();
if (sm != IntPtr.Zero) Native.rive_sm_destroy(sm);
Native.rive_artboard_instance_destroy(ab);
Native.rive_file_destroy(file);
GC.KeepAlive(cb);

internal static class Native
{
    const string L = "rive_shim";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void VoidFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void MatFn(IntPtr ctx, IntPtr m);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void IntFn(IntPtr ctx, int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Int2Fn(IntPtr ctx, int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FloatFn(IntPtr ctx, float f);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        public VoidFn save, restore;
        public MatFn transform;
        public IntFn clipPath;
        public Int2Fn drawPath;
        public FloatFn modulateOpacity;
    }

    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_set_callbacks(Callbacks cb);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rive_file_load(byte[] b, int len);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_file_destroy(IntPtr f);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rive_artboard_instance(IntPtr f, int i);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_artboard_instance_destroy(IntPtr a);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_artboard_advance(IntPtr a, float dt);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rive_sm_instance(IntPtr a);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rive_sm_instance_at(IntPtr a, int index);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_sm_destroy(IntPtr s);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_sm_advance(IntPtr s, float dt);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_artboard_draw(IntPtr a, IntPtr ctx);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_artboard_draw_fit(IntPtr a, IntPtr ctx, float w, float h);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_path_verb_count(int id);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_path_point_count(int id);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_path_fill_rule(int id);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_path_copy(int id, byte[] verbs, float[] points);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_paint_get(int id, out int style, out uint color, out float th, out int join, out int cap, out int blend, out float feather, out int shaderId);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_shader_info(int id, out int type, out float a, out float b, out float c, out float d);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_shader_stops(int id, uint[] colors, float[] stops);
}
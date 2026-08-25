using System.Runtime.InteropServices;
using SkiaSharp;
namespace RiveSkia;
internal sealed class RiveScene : IDisposable
{
    readonly RiveFile _file;
    readonly bool _ownsFile; // true — файл создан этой сценой и её же и разрушается; false — общий, чужой
    readonly IntPtr _artboard, _sm;
    readonly Dictionary<int, (int version, SKPath path)> _paths = new();
    readonly Stack<float> _opacityStack = new();
    readonly Native.Callbacks _cb;     // поле, а не локальная: держит делегаты от GC

    SKCanvas _canvas;
    float _opacity = 1f;

    public RiveScene(RiveFile file, bool ownsFile)
    {
        _file = file;
        _ownsFile = ownsFile;

        _artboard = Native.rive_artboard_instance(_file.Handle, 0);
        _sm = Native.rive_sm_instance(_artboard);
        if (_sm == IntPtr.Zero) _sm = Native.rive_sm_instance_at(_artboard, 0);

        _cb = new Native.Callbacks
        {
            save = _ => { _canvas.Save(); _opacityStack.Push(_opacity); },
            restore = _ => { _canvas.Restore(); if (_opacityStack.Count > 0) _opacity = _opacityStack.Pop(); },
            transform = (_, m) =>
            {
                var v = new float[6];
                Marshal.Copy(m, v, 0, 6);
                var mat = new SKMatrix(v[0], v[2], v[4], v[1], v[3], v[5], 0, 0, 1);
                _canvas.Concat(in mat);
            },
            clipPath = (_, id) => _canvas.ClipPath(GetPath(id), SKClipOperation.Intersect, true),
            drawPath = (_, pid, paintId) => Draw(pid, paintId),
            modulateOpacity = (_, o) => _opacity *= o,
        };
    }

    // Advance (UI-поток) и Render (поток компоновки Avalonia) — подтверждено эмпирически,
    // это два разных потока (см. коммит/обсуждение) — оба трогают один и тот же нативный
    // ArtboardInstance/StateMachineInstance без какой-либо защиты со стороны Rive core,
    // поэтому сериализуем их здесь; Render также защищает состояние _canvas/_opacity,
    // которое иначе могло бы быть частично перезаписано параллельным вызовом
    readonly object _sync = new();

    public void Advance(float dt)
    {
        lock (_sync)
        {
            if (_sm != IntPtr.Zero) Native.rive_sm_advance(_sm, dt);
            else Native.rive_artboard_advance(_artboard, dt);
        }
    }

    // размер контрола на последнем кадре — нужен, чтобы пересчитать координаты указателя
    // из пикселей контрола в локальные координаты артборда той же формулой Fit::contain,
    // что и при рисовании (иначе клики/наведение будут смещены)
    float _lastW, _lastH;

    public void Render(SKCanvas canvas, float w, float h)
    {
        lock (_sync)
        {
            _lastW = w;
            _lastH = h;
            _canvas = canvas;
            _opacity = 1f;
            _opacityStack.Clear();
            Native.rive_set_callbacks(_cb);
            Native.rive_artboard_draw_fit(_artboard, IntPtr.Zero, w, h);
            _canvas = null;
        }
    }

    // ---------- указатель ----------
    // Возвращают true, если попали в интерактивный элемент (полезно, например, чтобы решить,
    // менять ли курсор на "руку"). Без стейт-машины (её нет в файле) — молча ничего не делают.
    public bool PointerMove(float x, float y)
    {
        lock (_sync)
        {
            if (_sm == IntPtr.Zero || _lastW <= 0 || _lastH <= 0) return false;
            return Native.rive_sm_pointer_move(_sm, _artboard, _lastW, _lastH, x, y) != 0;
        }
    }
    public bool PointerDown(float x, float y)
    {
        lock (_sync)
        {
            if (_sm == IntPtr.Zero || _lastW <= 0 || _lastH <= 0) return false;
            return Native.rive_sm_pointer_down(_sm, _artboard, _lastW, _lastH, x, y) != 0;
        }
    }
    public bool PointerUp(float x, float y)
    {
        lock (_sync)
        {
            if (_sm == IntPtr.Zero || _lastW <= 0 || _lastH <= 0) return false;
            return Native.rive_sm_pointer_up(_sm, _artboard, _lastW, _lastH, x, y) != 0;
        }
    }
    public bool PointerExit(float x, float y)
    {
        lock (_sync)
        {
            if (_sm == IntPtr.Zero || _lastW <= 0 || _lastH <= 0) return false;
            return Native.rive_sm_pointer_exit(_sm, _artboard, _lastW, _lastH, x, y) != 0;
        }
    }

    // ---------- входы стейт-машины ----------
    // Имя, которого нет в файле, — тихий no-op (get возвращает default), как и остальной
    // API библиотеки: опечатка в имени входа не должна ронять приложение.
    public void SetBool(string name, bool value) { lock (_sync) Native.rive_sm_set_bool(_sm, name, value ? 1 : 0); }
    public bool GetBool(string name) { lock (_sync) return Native.rive_sm_get_bool(_sm, name) != 0; }
    public void SetNumber(string name, float value) { lock (_sync) Native.rive_sm_set_number(_sm, name, value); }
    public float GetNumber(string name) { lock (_sync) return Native.rive_sm_get_number(_sm, name); }
    public void FireTrigger(string name) { lock (_sync) Native.rive_sm_fire_trigger(_sm, name); }

    // геометрия кэшируется по (id, version): ядро Rive переиспользует один и тот же
    // RenderPath (тот же id) для фигур с меняющейся геометрией, перезаписывая его через
    // rewind() — нативная сторона увеличивает version при любой такой мутации, поэтому
    // кэш остаётся корректным (перечитывает только реально изменившееся), а не наивно
    // замороженным на первом кадре и не перечитывающим неизменную геометрию каждый раз
    SKPath GetPath(int id)
    {
        int version = Native.rive_path_version(id);
        if (_paths.TryGetValue(id, out var cached) && cached.version == version)
            return cached.path;

        var p = BuildPath(id);
        if (cached.path != null) cached.path.Dispose(); // старая геометрия под этим id больше не нужна
        _paths[id] = (version, p);
        return p;
    }

    SKPath BuildPath(int id)
    {
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

        return p;
    }

    void Draw(int pid, int paintId)
    {
        Native.rive_paint_get(paintId, out int style, out uint color,
                              out float thickness, out int join,
                              out int cap, out int blend, out float feather,
                              out int shaderId);

        byte a = (byte)Math.Clamp((color >> 24) * _opacity, 0, 255);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor((byte)(color >> 16), (byte)(color >> 8), (byte)color, a),
            Style = style == 1 ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
            StrokeWidth = thickness,
            StrokeJoin = (SKStrokeJoin)join,
            StrokeCap = (SKStrokeCap)cap,
        };

        if (feather > 0f)
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, feather);

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
                    byte ca = (byte)Math.Clamp((cols[i] >> 24) * _opacity, 0, 255);
                    skCols[i] = new SKColor((byte)(cols[i] >> 16), (byte)(cols[i] >> 8),
                                            (byte)cols[i], ca);
                }

                paint.Shader = type == 0
                    ? SKShader.CreateLinearGradient(new SKPoint(ga, gb), new SKPoint(gc, gd),
                                                    skCols, stops, SKShaderTileMode.Clamp)
                    : SKShader.CreateRadialGradient(new SKPoint(ga, gb), gc,
                                                    skCols, stops, SKShaderTileMode.Clamp);
            }
        }

        _canvas.DrawPath(GetPath(pid), paint);
    }

    public void Dispose()
    {
        foreach (var (_, path) in _paths.Values) path.Dispose();
        _paths.Clear();
        if (_sm != IntPtr.Zero) Native.rive_sm_destroy(_sm);
        if (_artboard != IntPtr.Zero) Native.rive_artboard_instance_destroy(_artboard);
        if (_ownsFile) _file.Dispose();
    }
}
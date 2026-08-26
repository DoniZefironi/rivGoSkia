using System.Runtime.InteropServices;
using SkiaSharp;
namespace RiveSkia;
internal sealed class RiveScene : IDisposable
{
    readonly RiveFile _file;
    readonly bool _ownsFile; // true — файл создан этой сценой и её же и разрушается; false — общий, чужой
    readonly IntPtr _artboard, _sm;
    readonly Dictionary<int, PathEntry> _paths = new();
    // изображения декодируются один раз (не по кадру, как пути) и живут в кэше до ~ShimImage
    readonly Dictionary<int, SKImage> _images = new();

    // Держит и SKPath, и переиспользуемые буферы под сырые verb/point данные — на кэш-промахе
    // (частый случай для анимированной геометрии) буферы просто дозаполняются заново, а не
    // аллоцируются с нуля каждый кадр; растут только когда путь стал больше, чем был.
    sealed class PathEntry
    {
        public int Version = -1;
        public SKPath Path;
        public byte[] VerbBuf = Array.Empty<byte>();
        public float[] PointBuf = Array.Empty<float>();
    }
    readonly Stack<float> _opacityStack = new();
    readonly Native.Callbacks _cb;     // поле, а не локальная: держит делегаты от GC

    SKCanvas _canvas;
    float _opacity = 1f;

    public RiveScene(RiveFile file, bool ownsFile) : this(file, ownsFile, null, null) { }

    // artboardName/stateMachineName — null означает "по умолчанию" (нулевой артборд, его
    // стейт-машина по умолчанию либо первая по индексу). Имя, которого нет в файле, — тихий
    // no-op на уровне всей сцены: _artboard/_sm остаются IntPtr.Zero, а Advance/Render/входы
    // и так уже проверяют это перед каждым обращением, так что ничего не рисуется и не падает.
    public RiveScene(RiveFile file, bool ownsFile, string artboardName, string stateMachineName)
    {
        _file = file;
        _ownsFile = ownsFile;

        _artboard = string.IsNullOrEmpty(artboardName)
            ? Native.rive_artboard_instance(_file.Handle, 0)
            : Native.rive_artboard_instance_named(_file.Handle, artboardName);

        _sm = string.IsNullOrEmpty(stateMachineName)
            ? Native.rive_sm_instance(_artboard)
            : Native.rive_sm_instance_named(_artboard, stateMachineName);
        if (_sm == IntPtr.Zero && string.IsNullOrEmpty(stateMachineName))
            _sm = Native.rive_sm_instance_at(_artboard, 0);

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
            drawImage = (_, imgId, opacity) => DrawImage(imgId, opacity),
        };
    }

    // Advance (UI-поток) и Render (поток компоновки Avalonia) — подтверждено эмпирически,
    // это два разных потока — оба трогают нативный ArtboardInstance/StateMachineInstance
    // без защиты со стороны Rive core. Лок статический (не на экземпляр!) — потому что
    // шимка (rive_shim.cpp) хранит геометрию/краски/шейдеры ВСЕХ сцен в общих на процесс
    // std::unordered_map (g_paths/g_paints/g_shaders) и общем g_cb. При нескольких
    // одновременно анимируемых RiveControl их Advance/Render/Dispose гонялись бы за эти
    // общие мапы из разных потоков без всякой синхронизации между собой — реальный краш
    // (access violation внутри rive_artboard_draw_fit), воспроизведён и исправлен здесь.
    static readonly object s_sync = new();

    // Ядро Rive регулярно заводит новый объект геометрии вместо переиспользования старого через
    // rewind() — измерено напрямую: ~117 тысяч раз за 15 секунд на 15 одновременно открытых
    // реальных файлах. Без этой очистки запись в _paths осталась бы висеть в кэше навсегда
    // (её id больше никогда не встретится, а сама SKPath не освободится) — самый настоящий,
    // а не гипотетический рост кэша. id глобально уникален на весь процесс, поэтому одной
    // статической мапы id → владеющая сцена достаточно, чтобы единственный на процесс колбэк
    // из ~ShimPath (не пересчитывается на каждый Render, как g_cb — может сработать в любой
    // момент из любого потока) знал, откуда убрать запись.
    static readonly Dictionary<int, RiveScene> s_pathOwners = new();
    static readonly Native.IdCallback s_onPathDestroyed = OnPathDestroyed; // поле — держит делегат от GC

    static RiveScene()
    {
        Native.rive_set_path_destroyed_callback(s_onPathDestroyed);
        Native.rive_set_image_destroyed_callback(s_onImageDestroyed);
    }

    static void OnPathDestroyed(int id)
    {
        lock (s_sync)
        {
            if (!s_pathOwners.Remove(id, out var scene)) return;
            if (scene._paths.Remove(id, out var entry)) entry.Path?.Dispose();
        }
    }

    // тот же приём, что и для путей: изображение декодируется один раз при импорте файла,
    // а не по кадру, но всё равно может быть разрушено (например, файл-владелец выгружен) в
    // произвольный момент — без этого колбэка кэшированный SKImage повис бы в _images навсегда
    static readonly Dictionary<int, RiveScene> s_imageOwners = new();
    static readonly Native.IdCallback s_onImageDestroyed = OnImageDestroyed;

    static void OnImageDestroyed(int id)
    {
        lock (s_sync)
        {
            if (!s_imageOwners.Remove(id, out var scene)) return;
            if (scene._images.Remove(id, out var img)) img?.Dispose();
        }
    }

    // Композитор Avalonia строит сцену на UI-потоке, а реально рисует чуть позже на
    // потоке композиции — RiveDrawOp.Render может быть уже поставлен в очередь на момент,
    // когда пользователь удаляет контрол и Dispose() успевает выполниться раньше. Лок сам
    // по себе это не ловит (порядок вызовов правильный, объект просто уже мёртв к моменту
    // выполнения) — нужна явная проверка, иначе Render словит use-after-free на _artboard/_sm.
    bool _disposed;

    public void Advance(float dt)
    {
        lock (s_sync)
        {
            if (_disposed) return;
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
        lock (s_sync)
        {
            if (_disposed) return;
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
        lock (s_sync)
        {
            if (_disposed || _sm == IntPtr.Zero || _lastW <= 0 || _lastH <= 0) return false;
            return Native.rive_sm_pointer_move(_sm, _artboard, _lastW, _lastH, x, y) != 0;
        }
    }
    public bool PointerDown(float x, float y)
    {
        lock (s_sync)
        {
            if (_disposed || _sm == IntPtr.Zero || _lastW <= 0 || _lastH <= 0) return false;
            return Native.rive_sm_pointer_down(_sm, _artboard, _lastW, _lastH, x, y) != 0;
        }
    }
    public bool PointerUp(float x, float y)
    {
        lock (s_sync)
        {
            if (_disposed || _sm == IntPtr.Zero || _lastW <= 0 || _lastH <= 0) return false;
            return Native.rive_sm_pointer_up(_sm, _artboard, _lastW, _lastH, x, y) != 0;
        }
    }
    public bool PointerExit(float x, float y)
    {
        lock (s_sync)
        {
            if (_disposed || _sm == IntPtr.Zero || _lastW <= 0 || _lastH <= 0) return false;
            return Native.rive_sm_pointer_exit(_sm, _artboard, _lastW, _lastH, x, y) != 0;
        }
    }

    // ---------- входы стейт-машины ----------
    // Имя, которого нет в файле, — тихий no-op (get возвращает default), как и остальной
    // API библиотеки: опечатка в имени входа не должна ронять приложение.
    public void SetBool(string name, bool value) { lock (s_sync) { if (!_disposed) Native.rive_sm_set_bool(_sm, name, value ? 1 : 0); } }
    public bool GetBool(string name) { lock (s_sync) return !_disposed && Native.rive_sm_get_bool(_sm, name) != 0; }
    public void SetNumber(string name, float value) { lock (s_sync) { if (!_disposed) Native.rive_sm_set_number(_sm, name, value); } }
    public float GetNumber(string name) { lock (s_sync) return _disposed ? 0f : Native.rive_sm_get_number(_sm, name); }
    public void FireTrigger(string name) { lock (s_sync) { if (!_disposed) Native.rive_sm_fire_trigger(_sm, name); } }

    // список входов — чтобы не гадать имена из редактора Rive, а спросить у самого файла
    public IReadOnlyList<RiveInput> GetInputs()
    {
        lock (s_sync)
        {
            if (_disposed || _sm == IntPtr.Zero) return Array.Empty<RiveInput>();

            int count = Native.rive_sm_input_count(_sm);
            var result = new RiveInput[count];
            var buf = new byte[256];
            for (int i = 0; i < count; i++)
            {
                var kind = Native.rive_sm_input_type(_sm, i) switch
                {
                    0 => RiveInputKind.Bool,
                    1 => RiveInputKind.Number,
                    2 => RiveInputKind.Trigger,
                    _ => RiveInputKind.Bool, // неизвестный тип ядра — не должно происходить на текущих файлах
                };
                int len = Native.rive_sm_input_name(_sm, i, buf, buf.Length);
                var name = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(len, buf.Length));
                result[i] = new RiveInput(name, kind);
            }
            return result;
        }
    }

    // геометрия кэшируется по (id, version): ядро Rive переиспользует один и тот же
    // RenderPath (тот же id) для фигур с меняющейся геометрией, перезаписывая его через
    // rewind() — нативная сторона увеличивает version при любой такой мутации, поэтому
    // кэш остаётся корректным (перечитывает только реально изменившееся), а не наивно
    // замороженным на первом кадре и не перечитывающим неизменную геометрию каждый раз
    SKPath GetPath(int id)
    {
        // один вызов вместо пяти (verb/point count, fill rule и version разом)
        Native.rive_path_info(id, out int nv, out int np, out int fillRule, out int version);

        if (!_paths.TryGetValue(id, out var entry))
        {
            entry = new PathEntry();
            _paths[id] = entry;
            // GetPath зовётся только из колбэков drawPath/clipPath внутри Render(), т.е. уже
            // под s_sync — отдельный lock тут не нужен
            s_pathOwners[id] = this;
        }
        if (entry.Path != null && entry.Version == version)
            return entry.Path;

        // буферы растут только когда путь стал больше, чем был раньше под этим id —
        // на статичном/повторяющемся размере геометрии на кэш-промахе больше нет new[]
        if (entry.VerbBuf.Length < nv) entry.VerbBuf = new byte[nv];
        if (entry.PointBuf.Length < np * 2) entry.PointBuf = new float[np * 2];
        Native.rive_path_copy(id, entry.VerbBuf, entry.PointBuf);

        var p = new SKPath
        {
            FillType = fillRule == 1 ? SKPathFillType.EvenOdd : SKPathFillType.Winding
        };

        var verbs = entry.VerbBuf;
        var pts = entry.PointBuf;
        int k = 0;
        for (int i = 0; i < nv; i++)
        {
            switch (verbs[i])
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

        entry.Path?.Dispose(); // старая геометрия под этим id больше не нужна
        entry.Path = p;
        entry.Version = version;
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

    // изображения декодированы на нативной стороне один раз при импорте (см. ShimImage) —
    // в отличие от путей, пиксели не меняются кадр к кадру, поэтому версия не нужна: раз
    // построенный SKImage переиспользуется, пока сама сцена или файл не будут разрушены
    SKImage GetImage(int id)
    {
        if (_images.TryGetValue(id, out var cached)) return cached;

        Native.rive_image_info(id, out int w, out int h);
        if (w <= 0 || h <= 0) return null;

        var pixels = new byte[w * h * 4];
        Native.rive_image_copy(id, pixels);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var image = SKImage.FromPixelCopy(info, pixels);

        _images[id] = image;
        // GetImage зовётся только из drawImage-колбэка внутри Render(), т.е. уже под s_sync
        s_imageOwners[id] = this;
        return image;
    }

    // без w/h ImageSampler'а (wrap/фильтрация) и blendMode — рисуется как обычный SrcOver-блит
    // на прямоугольник (0,0)-(width,height) в текущей системе координат: этот прямоугольник и
    // трансформацию уже подготовил вызывающий код в ядре Rive (Image::draw), drawImage сам
    // ничего не подгоняет под размер контрола — контролю (см. Fit::contain) в трансформации
    void DrawImage(int id, float opacity)
    {
        var img = GetImage(id);
        if (img == null) return;

        byte a = (byte)Math.Clamp(255 * opacity * _opacity, 0, 255);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, a) };
        _canvas.DrawImage(img, new SKRect(0, 0, img.Width, img.Height), paint);
    }

    public void Dispose()
    {
        // Dispose тоже под общим локом: деструкторы ShimPath/ShimPaint/ShimShader на нативной
        // стороне стирают записи из тех же общих g_paths/g_paints/g_shaders, что читают/пишут
        // Advance/Render других живых сцен — без лока это была бы гонка на удаление из мапы.
        lock (s_sync)
        {
            if (_disposed) return; // идемпотентно — и защищает от повторного разрушения
            _disposed = true;

            foreach (var (id, entry) in _paths)
            {
                entry.Path?.Dispose();
                s_pathOwners.Remove(id);
            }
            _paths.Clear();
            foreach (var (id, img) in _images)
            {
                img?.Dispose();
                s_imageOwners.Remove(id);
            }
            _images.Clear();
            if (_sm != IntPtr.Zero) Native.rive_sm_destroy(_sm);
            if (_artboard != IntPtr.Zero) Native.rive_artboard_instance_destroy(_artboard);
            if (_ownsFile) _file.Dispose();
        }
    }
}

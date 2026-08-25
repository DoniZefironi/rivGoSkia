using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace RiveSkia;

public class RiveControl : Control, IDisposable
{
    // сцена (владеет нативными ресурсами и логикой отрисовки)
    readonly RiveScene _scene;
    // таймер (двигатель анимации)
    readonly DispatcherTimer _timer;

    // fixed-timestep с аккумулятором: в Rive всегда уходит стабильный шаг 1/60, сколько бы
    // раз он ни повторился за один тик; реальное время влияет только на то, сколько таких
    // шагов накопилось, а не на размер самого шага — иначе скачок dt после подтормозившего
    // кадра дестабилизирует пружины/физику внутри анимации (см. CONTEXT.md)
    readonly Stopwatch _clock = Stopwatch.StartNew();
    TimeSpan _last;
    double _accumulator;
    const float FixedStep = 1f / 60f;

    bool _disposed;

    // загружает файл самостоятельно и владеет им единолично (обратная совместимость)
    public RiveControl(string rivPath) : this(new RiveFile(rivPath), ownsFile: true) { }

    // переиспользует уже загруженный файл — не парсит .riv заново, полезно при множестве
    // экземпляров одной и той же анимации (не владеет им, не разрушает при Dispose)
    public RiveControl(RiveFile file) : this(file, ownsFile: false) { }

    RiveControl(RiveFile file, bool ownsFile)
    {
        _scene = new RiveScene(file, ownsFile);
        // 4 мс, а не 16: DispatcherTimer на Windows огрубляет интервал грануляцией системного
        // таймера — на 16 мс реальная частота тиков проседает примерно до 30-40 Гц вместо ~60,
        // а на 4 мс уже ничем не ограничена, кроме честного vsync монитора (см. обсуждение)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    void OnTick(object sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = (now - _last).TotalSeconds;
        _last = now;
        _accumulator += Math.Min(dt, 0.25); // защита от гигантского скачка (пауза окна и т.п.)
        while (_accumulator >= FixedStep)
        {
            _scene.Advance(FixedStep);
            _accumulator -= FixedStep;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
        => context.Custom(new RiveDrawOp(new Rect(Bounds.Size), _scene));

    // ---------- указатель ----------
    // Позиция уже приходит в тех же логических пикселях контрола, что и Bounds/DrawingContext —
    // RiveScene сама пересчитывает её в координаты артборда с учётом Fit::contain.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_scene.PointerMove((float)p.X, (float)p.Y)) InvalidateVisual();
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (_scene.PointerDown((float)p.X, (float)p.Y)) InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (_scene.PointerUp((float)p.X, (float)p.Y)) InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        var p = e.GetPosition(this);
        if (_scene.PointerExit((float)p.X, (float)p.Y)) InvalidateVisual();
    }

    // ---------- входы стейт-машины ----------
    // Имя входа берётся из редактора Rive (вкладка State Machine). Обращение к
    // несуществующему имени — не ошибка, просто ничего не происходит.
    public void SetInputBool(string name, bool value) => _scene.SetBool(name, value);
    public bool GetInputBool(string name) => _scene.GetBool(name);
    public void SetInputNumber(string name, float value) => _scene.SetNumber(name, value);
    public float GetInputNumber(string name) => _scene.GetNumber(name);
    public void FireInputTrigger(string name) => _scene.FireTrigger(name);

    // автоматически освобождает нативные ресурсы и останавливает таймер, когда контрол
    // убирают из визуального дерева — без этого RiveScene и её нативный ArtboardInstance/
    // StateMachineInstance жили бы вечно, а таймер продолжал бы вхолостую тикать
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _scene.Dispose();
        GC.SuppressFinalize(this);
    }

    // держит ссылку на постоянную сценку и текущие границы
    sealed class RiveDrawOp : ICustomDrawOperation
    {
        readonly RiveScene _scene;
        public Rect Bounds { get; }

        public RiveDrawOp(Rect bounds, RiveScene scene) { Bounds = bounds; _scene = scene; }

        // зовётся когда авалония исполняет отрисовку
        public void Render(ImmediateDrawingContext context)
        {
            // способ спросить у текущего рендер-таргета
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            // если фичи нет - молча ничего не рисуем
            if (feature is null) return;
            // временная аренда канваса с гарантией, что гпу-контекст не будет тронут, пока lease не задиспозен
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;

            float w = (float)Bounds.Width;
            float h = (float)Bounds.Height;

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, w, h));
            _scene.Render(canvas, w, h);
            canvas.Restore();
        }

        // используется авалонией для попадания указателя мыши в контрол, тут просто прямоугольник
        public bool HitTest(Point p) => Bounds.Contains(p);
        // всегда фолз, авалония иногда сравнивает дроу-операции между кадрами, чтобы пропустить
        // перерисовку неизменившегося контента. Здесь это специально отключено, но ценой того, что даже если анимация
        // встала на паузу, перерисовка всё равно будет считаться изменившейся
        public bool Equals(ICustomDrawOperation other) => false;
        // пустой, сам RiveDrawOp не владеет ничем, что нужно освобождать.
        public void Dispose() { }
    }
}

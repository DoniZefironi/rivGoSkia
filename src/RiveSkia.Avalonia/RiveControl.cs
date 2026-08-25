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
    // сцена (владеет нативными ресурсами и логикой отрисовки) — null, когда контрол смотрит
    // на общий RiveSharedInstance вместо того, чтобы иметь свою собственную
    readonly RiveScene _scene;
    // общий инстанс на несколько контролов — null в обычном режиме (свой _scene)
    readonly RiveSharedInstance _shared;
    // сцена, которую реально использовать для указателя/входов — своя или общая
    RiveScene ActiveScene => _scene ?? _shared.Scene;
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

    // во сколько раз реже перерисовывать относительно реального тика — 1 (по умолчанию) значит
    // каждый кадр, 3 — раз в три. Advance всё равно идёт каждый реальный кадр по-честному
    // (это дёшево и не влияет на точность срабатывания триггеров/таймингов) — экономится
    // только перерисовка, самая дорогая часть. Приложение само решает, кому это нужно —
    // библиотека не знает, что сейчас не в фокусе или вне экрана.
    public int RenderEveryNthFrame
    {
        get => _renderEveryNthFrame;
        set => _renderEveryNthFrame = Math.Max(1, value);
    }
    int _renderEveryNthFrame = 1;
    int _frameSkip;

    // полностью останавливает тик контрола — ни Advance, ни перерисовки, пока не позовут
    // Resume(). На контроле, смотрящем на RiveSharedInstance, останавливает только его
    // собственную перерисовку — сам общий инстанс продолжает жить для остальных подписчиков.
    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (_disposed || IsPaused) return;
        IsPaused = true;
        _timer.Stop();
    }

    public void Resume()
    {
        if (_disposed || !IsPaused) return;
        IsPaused = false;
        // без сброса первый тик после паузы попытался бы наверстать весь пропущенный простой
        // одним аккумулированным скачком
        _last = _clock.Elapsed;
        _accumulator = 0;
        _frameSkip = 0;
        _timer.Start();
    }

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

    // делит один живой артборд/стейт-машину с другими такими же RiveControl вместо того,
    // чтобы заводить свой — сам не продвигает время (это уже делает таймер самого
    // RiveSharedInstance, один раз на всех подписчиков) и, при совпадении размера с другим
    // контролом на этот же инстанс в одном кадре, переиспользует уже готовую картинку кадра
    // вместо повторного построения путей/красок. См. RiveSharedInstance про цену: контролы
    // перестают быть независимыми — указатель/входы бьют по общей стейт-машине.
    public RiveControl(RiveSharedInstance shared)
    {
        _shared = shared;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4) };
        _timer.Tick += OnTickShared;
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
        if (++_frameSkip >= _renderEveryNthFrame)
        {
            _frameSkip = 0;
            InvalidateVisual();
        }
    }

    // общий инстанс продвигает время сам за всех своих подписчиков — этому контролу остаётся
    // только просить перерисоваться (на полной частоте или реже — см. RenderEveryNthFrame)
    void OnTickShared(object sender, EventArgs e)
    {
        if (++_frameSkip >= _renderEveryNthFrame)
        {
            _frameSkip = 0;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
        => context.Custom(_scene != null
            ? new RiveDrawOp(new Rect(Bounds.Size), _scene)
            : new RiveDrawOp(new Rect(Bounds.Size), _shared));

    // ---------- указатель ----------
    // Позиция уже приходит в тех же логических пикселях контрола, что и Bounds/DrawingContext —
    // RiveScene сама пересчитывает её в координаты артборда с учётом Fit::contain.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (ActiveScene.PointerMove((float)p.X, (float)p.Y)) InvalidateVisual();
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (ActiveScene.PointerDown((float)p.X, (float)p.Y)) InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (ActiveScene.PointerUp((float)p.X, (float)p.Y)) InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        var p = e.GetPosition(this);
        if (ActiveScene.PointerExit((float)p.X, (float)p.Y)) InvalidateVisual();
    }

    // ---------- входы стейт-машины ----------
    // Имя входа берётся из редактора Rive (вкладка State Machine). Обращение к
    // несуществующему имени — не ошибка, просто ничего не происходит.
    // На общем RiveSharedInstance это бьёт по одной стейт-машине на всех подписчиков.
    public void SetInputBool(string name, bool value) => ActiveScene.SetBool(name, value);
    public bool GetInputBool(string name) => ActiveScene.GetBool(name);
    public void SetInputNumber(string name, float value) => ActiveScene.SetNumber(name, value);
    public float GetInputNumber(string name) => ActiveScene.GetNumber(name);
    public void FireInputTrigger(string name) => ActiveScene.FireTrigger(name);

    // список входов стейт-машины (имя + тип) — чтобы не подбирать имена вслепую,
    // а спросить у самого файла, что в нём вообще есть
    public IReadOnlyList<RiveInput> GetInputs() => ActiveScene.GetInputs();

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
        // подписан ровно один из двух — отписка от второго безопасный no-op
        _timer.Tick -= OnTick;
        _timer.Tick -= OnTickShared;
        // общий RiveSharedInstance не наш — его освобождает тот, кто его создал,
        // он обычно переживает любой один конкретный контрол
        _scene?.Dispose();
        GC.SuppressFinalize(this);
    }

    // держит ссылку на постоянную сценку (свою или общую) и текущие границы
    sealed class RiveDrawOp : ICustomDrawOperation
    {
        readonly RiveScene _scene;
        readonly RiveSharedInstance _shared;
        public Rect Bounds { get; }

        public RiveDrawOp(Rect bounds, RiveScene scene) { Bounds = bounds; _scene = scene; }
        public RiveDrawOp(Rect bounds, RiveSharedInstance shared) { Bounds = bounds; _shared = shared; }

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
            if (_scene != null)
            {
                _scene.Render(canvas, w, h);
            }
            else
            {
                var img = _shared.GetImage(lease.GrContext, w, h);
                if (img != null) canvas.DrawImage(img, 0, 0);
            }
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

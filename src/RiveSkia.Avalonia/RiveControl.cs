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
    // XAML/биндинг-friendly способ задать файл — путь на диске или "avares://.../file.riv"
    // для файла, встроенного в сборку как ресурс. Меняется на лету: смена значения грузит
    // новый файл заново и освобождает предыдущий (см. OnSourceChanged).
    public static readonly StyledProperty<string> SourceProperty =
        AvaloniaProperty.Register<RiveControl, string>(nameof(Source));

    public string Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    // имя артборда/стейт-машины, как задал дизайнер в редакторе Rive (вкладки Artboards и
    // State Machine) — null означает "по умолчанию" (нулевой артборд, его стейт-машина по
    // умолчанию либо первая по индексу). Не нужно подбирать вслепую — RiveFile.GetArtboards()
    // возвращает то, что реально есть в файле. Меняются на лету так же, как Source.
    public static readonly StyledProperty<string> ArtboardProperty =
        AvaloniaProperty.Register<RiveControl, string>(nameof(Artboard));
    public static readonly StyledProperty<string> StateMachineProperty =
        AvaloniaProperty.Register<RiveControl, string>(nameof(StateMachine));

    public string Artboard
    {
        get => GetValue(ArtboardProperty);
        set => SetValue(ArtboardProperty, value);
    }
    public string StateMachine
    {
        get => GetValue(StateMachineProperty);
        set => SetValue(StateMachineProperty, value);
    }

    static RiveControl()
    {
        SourceProperty.Changed.AddClassHandler<RiveControl>((c, e) => c.OnSourceChanged());
        ArtboardProperty.Changed.AddClassHandler<RiveControl>((c, e) => c.OnSourceChanged());
        StateMachineProperty.Changed.AddClassHandler<RiveControl>((c, e) => c.OnSourceChanged());
    }

    // сцена (владеет нативными ресурсами и логикой отрисовки) — null, когда контрол смотрит
    // на общий RiveSharedInstance вместо того, чтобы иметь свою собственную, или пока Source
    // ещё не задан
    RiveScene _scene;
    // общий инстанс на несколько контролов — null в обычном режиме (свой _scene)
    RiveSharedInstance _shared;
    // сцена, которую реально использовать для указателя/входов — своя или общая; null, пока
    // ничего не загружено (например, Source ещё не задан)
    RiveScene ActiveScene => _scene ?? _shared?.Scene;
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

    // для XAML/биндинга: <rive:RiveControl Source="avares://MyApp/Assets/teddy.riv" /> —
    // файл грузится, когда Source получит значение (сразу, если задан здесь же в объектном
    // инициализаторе после конструктора)
    public RiveControl()
    {
        // 4 мс, а не 16: DispatcherTimer на Windows огрубляет интервал грануляцией системного
        // таймера — на 16 мс реальная частота тиков проседает примерно до 30-40 Гц вместо ~60,
        // а на 4 мс уже ничем не ограничена, кроме честного vsync монитора (см. обсуждение)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // загружает файл самостоятельно и владеет им единолично — сахар над Source/Artboard/StateMachine
    public RiveControl(string rivPath, string artboard = null, string stateMachine = null) : this()
    {
        Artboard = artboard;
        StateMachine = stateMachine;
        Source = rivPath; // последним — иначе перечитает файл ещё раз при смене Artboard/StateMachine
    }

    // переиспользует уже загруженный файл — не парсит .riv заново, полезно при множестве
    // экземпляров одной и той же анимации (не владеет им, не разрушает при Dispose)
    public RiveControl(RiveFile file, string artboard = null, string stateMachine = null) : this()
    {
        _scene = new RiveScene(file, ownsFile: false, artboard, stateMachine);
    }

    // делит один живой артборд/стейт-машину с другими такими же RiveControl вместо того,
    // чтобы заводить свой — сам не продвигает время (это уже делает таймер самого
    // RiveSharedInstance, один раз на всех подписчиков) и, при совпадении размера с другим
    // контролом на этот же инстанс в одном кадре, переиспользует уже готовую картинку кадра
    // вместо повторного построения путей/красок. См. RiveSharedInstance про цену: контролы
    // перестают быть независимыми — указатель/входы бьют по общей стейт-машине.
    public RiveControl(RiveSharedInstance shared) : this() { _shared = shared; }

    // (пере)загружает файл, на который сейчас указывает Source, с учётом текущих Artboard/
    // StateMachine — старая сцена (если была) освобождается, чтобы не оставить висящий
    // нативный артборд при смене файла/артборда/стейт-машины на лету
    void OnSourceChanged()
    {
        if (_disposed) return;
        _scene?.Dispose();
        var path = Source;
        _scene = string.IsNullOrEmpty(path)
            ? null
            : new RiveScene(new RiveFile(path), ownsFile: true, Artboard, StateMachine);
        _accumulator = 0;
        _last = _clock.Elapsed;
    }

    void OnTick(object sender, EventArgs e)
    {
        // ничего не загружено — Source ещё не задан на контроле без общего инстанса
        if (_scene == null && _shared == null) return;

        if (_shared == null)
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
        }
        // на общем RiveSharedInstance время продвигает его собственный таймер один раз на
        // всех подписчиков — этому контролу остаётся только попроситься перерисоваться
        // (на полной частоте или реже — см. RenderEveryNthFrame)

        if (++_frameSkip >= _renderEveryNthFrame)
        {
            _frameSkip = 0;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_scene != null) context.Custom(new RiveDrawOp(new Rect(Bounds.Size), _scene));
        else if (_shared != null) context.Custom(new RiveDrawOp(new Rect(Bounds.Size), _shared));
    }

    // ---------- указатель ----------
    // Позиция уже приходит в тех же логических пикселях контрола, что и Bounds/DrawingContext —
    // RiveScene сама пересчитывает её в координаты артборда с учётом Fit::contain.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (ActiveScene?.PointerMove((float)p.X, (float)p.Y) == true) InvalidateVisual();
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (ActiveScene?.PointerDown((float)p.X, (float)p.Y) == true) InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (ActiveScene?.PointerUp((float)p.X, (float)p.Y) == true) InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        var p = e.GetPosition(this);
        if (ActiveScene?.PointerExit((float)p.X, (float)p.Y) == true) InvalidateVisual();
    }

    // ---------- входы стейт-машины ----------
    // Имя входа берётся из редактора Rive (вкладка State Machine). Обращение к
    // несуществующему имени — не ошибка, просто ничего не происходит. Пока ничего не
    // загружено (Source ещё не задан) — тоже тихий no-op, а не исключение.
    // На общем RiveSharedInstance это бьёт по одной стейт-машине на всех подписчиков.
    public void SetInputBool(string name, bool value) => ActiveScene?.SetBool(name, value);
    public bool GetInputBool(string name) => ActiveScene?.GetBool(name) ?? false;
    public void SetInputNumber(string name, float value) => ActiveScene?.SetNumber(name, value);
    public float GetInputNumber(string name) => ActiveScene?.GetNumber(name) ?? 0f;
    public void FireInputTrigger(string name) => ActiveScene?.FireTrigger(name);

    // список входов стейт-машины (имя + тип) — чтобы не подбирать имена вслепую,
    // а спросить у самого файла, что в нём вообще есть
    public IReadOnlyList<RiveInput> GetInputs() => ActiveScene?.GetInputs() ?? Array.Empty<RiveInput>();

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

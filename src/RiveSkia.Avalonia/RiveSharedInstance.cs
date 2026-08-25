using Avalonia.Threading;
using SkiaSharp;

namespace RiveSkia;

/// <summary>
/// Один живой артборд/стейт-машина, на которые может смотреть сразу несколько
/// <see cref="RiveControl"/>. Продвигается по времени только один раз за тик (не по разу
/// на каждый смотрящий на него контрол), а отрисованный кадр кэшируется как растровая
/// <see cref="SKImage"/> (не как <see cref="SKPicture"/> — переигрывание записанных векторных
/// команд всё равно растеризует их заново на каждый повтор, экономии на самом дорогом почти
/// не даёт; проверено на 49 копиях — с SKPicture-кэшем FPS был даже ниже, чем вообще без
/// расшаривания) на каждый уникальный размер контрола. Если несколько контролов одного
/// размера показывают этот инстанс в одном и том же кадре, реальную работу (обход дерева
/// артборда, построение путей/красок и их растеризация) делает только первый из них —
/// остальные просто копируют уже готовую текстуру.
///
/// Подходит для множества одинаковых неинтерактивных копий одной анимации (например, палитра
/// повторяющихся иконок) — расплата за экономию в том, что копии не независимы: указатель и
/// входы стейт-машины у всех контролов, созданных от одного <see cref="RiveSharedInstance"/>,
/// бьют по общей стейт-машине, так что взаимодействие с одной копией видно во всех остальных.
/// Если копии должны жить и реагировать независимо — используй обычный <see cref="RiveFile"/>.
/// </summary>
public sealed class RiveSharedInstance : IDisposable
{
    internal readonly RiveScene Scene;
    readonly DispatcherTimer _timer;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    TimeSpan _last;
    double _accumulator;
    const float FixedStep = 1f / 60f;

    readonly object _cacheSync = new();
    int _frame;
    readonly Dictionary<(int w, int h), (int frame, SKImage img)> _cache = new();

    bool _disposed;

    public RiveSharedInstance(string rivPath) : this(new RiveFile(rivPath), ownsFile: true) { }
    public RiveSharedInstance(RiveFile file) : this(file, ownsFile: false) { }

    RiveSharedInstance(RiveFile file, bool ownsFile)
    {
        Scene = new RiveScene(file, ownsFile);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    void OnTick(object sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = (now - _last).TotalSeconds;
        _last = now;
        _accumulator += Math.Min(dt, 0.25);

        bool advanced = false;
        while (_accumulator >= FixedStep)
        {
            Scene.Advance(FixedStep);
            _accumulator -= FixedStep;
            advanced = true;
        }
        // версия кадра растёт только когда реально был Advance — иначе контролы этого размера
        // без нужды пересчитывали бы одну и ту же картинку кадра заново
        if (advanced) lock (_cacheSync) _frame++;
    }

    // растровая картинка кадра на конкретный размер — первый контрол этого размера в этом
    // кадре платит за rive_artboard_draw_fit, построение путей/красок и их растеризацию,
    // остальные просто получают готовую текстуру. gr — GRContext того же рендер-таргета,
    // на котором эту текстуру потом будут рисовать (у Avalonia он один на всё окно), иначе
    // созданная поверхность будет несовместима с канвасом, в который её рисуют
    internal SKImage GetImage(GRContext gr, float w, float h)
    {
        var key = ((int)MathF.Round(w), (int)MathF.Round(h));
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.frame == _frame)
                return entry.img;

            entry.img?.Dispose();

            int iw = Math.Max(1, (int)MathF.Ceiling(w));
            int ih = Math.Max(1, (int)MathF.Ceiling(h));
            using var surface = SKSurface.Create(gr, budgeted: true,
                new SKImageInfo(iw, ih, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            Scene.Render(canvas, w, h);
            var img = surface.Snapshot();

            _cache[key] = (_frame, img);
            return img;
        }
    }

    public IReadOnlyList<RiveInput> GetInputs() => Scene.GetInputs();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        lock (_cacheSync)
        {
            foreach (var entry in _cache.Values) entry.img?.Dispose();
            _cache.Clear();
        }
        Scene.Dispose();
    }
}

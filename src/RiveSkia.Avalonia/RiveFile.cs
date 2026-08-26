using Avalonia.Platform;

namespace RiveSkia;

/// <summary>
/// Распарсенный .riv-файл в памяти ядра Rive. Один RiveFile можно передать
/// в несколько RiveControl — каждый создаст свой независимый инстанс артборда
/// поверх одной и той же уже распарсенной геометрии, вместо повторного чтения
/// и парсинга файла с диска на каждый экземпляр.
/// </summary>
public sealed class RiveFile : IDisposable
{
    internal IntPtr Handle { get; private set; }

    // rivPath — физический путь на диске либо "avares://Сборка/путь/файл.riv" для файла,
    // встроенного в сборку как AvaloniaResource (см. AvaloniaResource в .csproj потребителя)
    public RiveFile(string rivPath)
    {
        var bytes = LoadBytes(rivPath);
        Handle = Native.rive_file_load(bytes, bytes.Length);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("не удалось загрузить .riv");
    }

    static byte[] LoadBytes(string rivPath)
    {
        if (!rivPath.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllBytes(rivPath);

        using var stream = AssetLoader.Open(new Uri(rivPath));
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.rive_file_destroy(Handle);
            Handle = IntPtr.Zero;
        }
    }
}

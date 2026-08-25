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

    public RiveFile(string rivPath)
    {
        var bytes = File.ReadAllBytes(rivPath);
        Handle = Native.rive_file_load(bytes, bytes.Length);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("не удалось загрузить .riv");
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

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

    // список артбордов файла и их стейт-машин — чтобы выбрать по имени, не подбирая вслепую
    // (см. RiveControl(rivPath, artboard, stateMachine) / Artboard / StateMachine)
    public IReadOnlyList<RiveArtboardInfo> GetArtboards()
    {
        int count = Native.rive_artboard_count(Handle);
        var result = new RiveArtboardInfo[count];
        var buf = new byte[256];
        for (int i = 0; i < count; i++)
        {
            int len = Native.rive_artboard_name(Handle, i, buf, buf.Length);
            var name = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(len, buf.Length));

            int smCount = Native.rive_state_machine_count(Handle, i);
            var sms = new string[smCount];
            for (int j = 0; j < smCount; j++)
            {
                int smLen = Native.rive_state_machine_name(Handle, i, j, buf, buf.Length);
                sms[j] = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(smLen, buf.Length));
            }
            result[i] = new RiveArtboardInfo(name, sms);
        }
        return result;
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

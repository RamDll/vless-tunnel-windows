namespace VlessTunnel.Core;

/// <summary>
/// Логи службы с ротацией (план, раздел 2: "%ProgramData%\vless-tunnel\logs\
/// с ротацией + Event Log для службы") — Event Log сам по себе не даёт
/// удобно вытащить историю для саппорта/отладки (записи усечены, нет
/// простого способа скопировать файлом), поэтому пишем ещё и в файл
/// рядом. Ротация по размеру, не по дате — служба может простоять месяцы
/// без перезапуска, дата-ротация с полночным переключением добавила бы
/// точку сбоя ровно в момент записи ровно в полночь без всякой пользы.
/// </summary>
public sealed class RotatingFileLogger : IDisposable
{
    private readonly string _logDir;
    private readonly string _baseName;
    private readonly long _maxBytes;
    private readonly int _maxBackups;
    private readonly Lock _lock = new();
    private StreamWriter _writer;
    private string _currentPath;

    public RotatingFileLogger(string logDir, string baseName = "service.log", long maxBytes = 5 * 1024 * 1024, int maxBackups = 4)
    {
        _logDir = logDir;
        _baseName = baseName;
        _maxBytes = maxBytes;
        _maxBackups = maxBackups;
        Directory.CreateDirectory(_logDir);
        _currentPath = Path.Combine(_logDir, _baseName);
        _writer = Open(_currentPath);
    }

    private static StreamWriter Open(string path) =>
        new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

    public void WriteLine(string message)
    {
        lock (_lock)
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            if (new FileInfo(_currentPath).Length >= _maxBytes)
                Rotate();
        }
    }

    // Вызывается только под _lock изнутри WriteLine.
    private void Rotate()
    {
        _writer.Dispose();

        for (var i = _maxBackups - 1; i >= 1; i--)
        {
            var src = Path.Combine(_logDir, $"{_baseName}.{i}");
            var dst = Path.Combine(_logDir, $"{_baseName}.{i + 1}");
            if (!File.Exists(src)) continue;
            File.Delete(dst); // File.Move не перезаписывает — File.Delete на несуществующем не бросает
            File.Move(src, dst);
        }

        var first = Path.Combine(_logDir, $"{_baseName}.1");
        File.Delete(first);
        File.Move(_currentPath, first);

        _writer = Open(_currentPath);
    }

    public void Dispose()
    {
        lock (_lock) { _writer.Dispose(); }
    }
}

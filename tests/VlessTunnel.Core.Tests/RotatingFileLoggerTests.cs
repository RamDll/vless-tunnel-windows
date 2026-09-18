using VlessTunnel.Core;
using Xunit;

namespace VlessTunnel.Core.Tests;

public class RotatingFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vt-log-tests-" + Guid.NewGuid());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WriteLine_CreatesLogFileWithMessage()
    {
        using var logger = new RotatingFileLogger(_dir, maxBytes: 1024 * 1024);
        logger.WriteLine("hello");
        logger.Dispose();

        var content = File.ReadAllText(Path.Combine(_dir, "service.log"));
        Assert.Contains("hello", content);
    }

    [Fact]
    public void ExceedingMaxBytes_RotatesToBackupOne()
    {
        using var logger = new RotatingFileLogger(_dir, maxBytes: 100, maxBackups: 4);
        for (var i = 0; i < 20; i++) logger.WriteLine(new string('x', 20));
        logger.Dispose();

        Assert.True(File.Exists(Path.Combine(_dir, "service.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "service.log.1")));
    }

    [Fact]
    public void RotationRespectsMaxBackupCount()
    {
        using var logger = new RotatingFileLogger(_dir, maxBytes: 50, maxBackups: 2);
        for (var i = 0; i < 40; i++) logger.WriteLine(new string('x', 20));
        logger.Dispose();

        Assert.True(File.Exists(Path.Combine(_dir, "service.log.1")));
        Assert.True(File.Exists(Path.Combine(_dir, "service.log.2")));
        Assert.False(File.Exists(Path.Combine(_dir, "service.log.3")));
    }

    [Fact]
    public void OldestBackup_ContainsOldestSurvivingContent()
    {
        // Каждая строка сама по себе больше maxBytes, так что ротация
        // происходит после КАЖДОЙ записи: .1 -> .2 -> выпадает. После
        // трёх записей с maxBackups=2 самая первая (AAAA) должна быть
        // вытеснена, а .2/.1 — содержать вторую/третью соответственно
        // (не перепутан порядок при сдвиге).
        using var logger = new RotatingFileLogger(_dir, maxBytes: 50, maxBackups: 2);
        logger.WriteLine("AAAA marker one, padded to trigger rotation soon xxxxxxxxxxxxxxxxxxxx");
        logger.WriteLine("BBBB marker two, padded to trigger rotation soon xxxxxxxxxxxxxxxxxxxx");
        logger.WriteLine("CCCC marker three, padded to trigger rotation soon xxxxxxxxxxxxxxxxxxxx");
        logger.Dispose();

        var log2 = File.ReadAllText(Path.Combine(_dir, "service.log.2"));
        var log1 = File.ReadAllText(Path.Combine(_dir, "service.log.1"));
        Assert.Contains("BBBB", log2);
        Assert.Contains("CCCC", log1);
        Assert.DoesNotContain("AAAA", log2);
        Assert.DoesNotContain("AAAA", log1);
    }
}

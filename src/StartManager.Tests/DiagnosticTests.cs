using System.ComponentModel;
using System.Text.Json;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public void RecoveryDirectoryInsideFileHasRussianDiagnosticAndKeepsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "bn-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var parent = Path.Combine(root, "profile");
            File.WriteAllText(parent, "original content");
            var error = Assert.ThrowsAny<IOException>(() => Directory.CreateDirectory(Path.Combine(parent, "Recovery")));
            var message = LegacyImport.ErrorMessage(error);
            Assert.Matches("[А-Яа-я]", message);
            Assert.DoesNotContain("Cannot create", message);
            Assert.Equal("original content", File.ReadAllText(parent));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"AfterLaunch\":\"Minimize\"}")]
    [InlineData("{\"DoubleClick\":[]}")]
    public void InvalidJsonExplainsProblemInRussianAndKeepsFile(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bn-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json"); File.WriteAllText(path, json);
            var store = new LibraryStore(path);
            var error = Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Contains("некорректный JSON", error.Message);
            Assert.IsType<JsonException>(error.InnerException);
            Assert.Throws<InvalidDataException>(() => store.Save(new()));
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NativeLaunchErrorsAreActionableWithoutRawSystemText()
    {
        Assert.Contains("исполняемый файл", Launcher.ErrorMessage(new Win32Exception(193, "raw English diagnostic")));
        Assert.Contains("не поддерживается", Launcher.ErrorMessage(new PlatformNotSupportedException("raw English diagnostic")));
        Assert.Equal("Каталог базы не найден.", Launcher.ErrorMessage(new ArgumentException("Каталог базы не найден.")));
    }

    [Fact]
    public void FileFailuresHaveRussianMessagesAndPreserveSpecificRecoveryInstructions()
    {
        Assert.Contains("Нет доступа", LegacyImport.ErrorMessage(new UnauthorizedAccessException("raw English")));
        Assert.Contains("не найден", LegacyImport.ErrorMessage(new FileNotFoundException("raw English")));
        // A Win32 code in an HRESULT on Windows, the raw errno elsewhere.
        Assert.Contains("занят", LegacyImport.ErrorMessage(new IOException("raw English", OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : 11)));
        Assert.Contains("свободного места", LegacyImport.ErrorMessage(new IOException("raw English", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28)));
        Assert.Contains("слишком длинный", LegacyImport.ErrorMessage(new PathTooLongException("raw English")));
        Assert.Contains("кодировк", LegacyImport.ErrorMessage(new System.Text.DecoderFallbackException("raw English")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Недопустимый путь", LegacyImport.ErrorMessage(new IOException("raw English", unchecked((int)0x8007007B))));
            Assert.Contains("слишком длинный", LegacyImport.ErrorMessage(new IOException("raw English", unchecked((int)0x800700CE))));
            // 16 is ERROR_CURRENT_DIRECTORY on Windows, not a busy file; 11 is ERROR_BAD_FORMAT, 28 ERROR_OUT_OF_PAPER.
            foreach (var other in new[] { 0x80070010, 0x8007000B, 0x8007001C })
                Assert.Equal("raw English", LegacyImport.ErrorMessage(new IOException("raw English", unchecked((int)other))));
            string Win32(int code) => LegacyImport.ErrorMessage(new IOException("raw English", unchecked((int)0x80070000) | code));
            foreach (var full in new[] { 39, 1295 }) Assert.Contains("свободного места", Win32(full));
            // The network codes of a share that went away, the logon refused after the session was dropped, one user per server.
            foreach (var network in new[] { 51, 53, 54, 55, 58, 59, 64, 67, 88, 1222, 1225, 1231, 1232, 1236, 1256, 2250 })
                Assert.Contains("Сетевая папка недоступна", Win32(network));
            foreach (var logon in new[] { 1326, 86 }) Assert.Contains("имя пользователя или пароль", Win32(logon));
            Assert.Contains("net use", Win32(1219));
            Assert.Contains("Истекло время ожидания", Win32(121));
        }
        else
        {
            // .NET puts the raw errno into HResult. EBUSY is what a CIFS share answers for a file another computer holds.
            foreach (var busy in new[] { 11, 16, 26 })
                Assert.Contains("занят другим процессом", LegacyImport.ErrorMessage(new IOException("raw English", busy)));
            foreach (var full in new[] { 28, 122 })
                Assert.Contains("свободного места", LegacyImport.ErrorMessage(new IOException("raw English", full)));
            foreach (var network in new[] { 100, 101, 103, 104, 107, 111, 112, 113, 116, 121 })
                Assert.Contains("Сетевая папка недоступна", LegacyImport.ErrorMessage(new IOException("raw English", network)));
            foreach (var logon in new[] { 127, 129 })
                Assert.Contains("имя пользователя или пароль", LegacyImport.ErrorMessage(new IOException("raw English", logon)));
            Assert.Contains("Истекло время ожидания", LegacyImport.ErrorMessage(new IOException("raw English", 110)));
            // EIO is a disk as well as a share: no claim that the network is gone.
            var io = LegacyImport.ErrorMessage(new IOException("Input/output error", 5));
            Assert.Contains("Ошибка ввода-вывода", io); Assert.DoesNotContain("Сетевая папка недоступна", io);
            // Windows codes mean other things here: 39 is ENOTEMPTY, 32 EPIPE.
            foreach (var other in new[] { 39, 32 })
                Assert.Equal("raw English", LegacyImport.ErrorMessage(new IOException("raw English", other)));
        }
        Assert.Equal("Исходное содержимое восстановлено.", LegacyImport.ErrorMessage(new IOException("Исходное содержимое восстановлено.")));
        // A message our code already wrote for the user stays, whatever code it carries (the folder sync of FilePersistence).
        foreach (var code in new[] { 5, 28, 112, unchecked((int)0x80070070) })
            Assert.Equal("Каталог не синхронизирован с диском: /x", LegacyImport.ErrorMessage(new ExplainedIOException("Каталог не синхронизирован с диском: /x", code)));
    }
}

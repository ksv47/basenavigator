namespace StartManager.Core;

/// <summary>
/// Menu entry of the program itself in the freedesktop application menu (Linux):
/// <c>&lt;data&gt;/applications/basenavigator.desktop</c> and the icon in the hicolor theme.
/// </summary>
public static class DesktopEntry
{
    public const string FileName = "basenavigator.desktop";
    public const string IconName = "basenavigator";
    public const string FallbackIcon = "application-x-executable";
    public const string OwnerKey = "X-BaseNavigator-Owner";
    // Avalonia X11 sets WM_CLASS from X11PlatformOptions.WmClass, by default the entry assembly name (AssemblyName in the csproj).
    public const string WindowClass = "BaseNavigator";

    /// <summary><c>$XDG_DATA_HOME</c> when set and absolute, otherwise <c>$HOME/.local/share</c>.</summary>
    public static string DataHome(Func<string, string?> variable)
    {
        var xdg = variable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)) return xdg;
        var home = variable("HOME");
        if (string.IsNullOrEmpty(home) || !Path.IsPathRooted(home))
            throw new InvalidOperationException("Не найден домашний каталог пользователя (HOME).");
        return Path.Combine(home, ".local", "share");
    }

    public static string EntryPath(string dataHome) => Path.Combine(dataHome, "applications", FileName);
    public static string IconPath(string dataHome) => Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", IconName + ".png");
    public static string AssetPath(string executable) => Path.Combine(Path.GetDirectoryName(executable) ?? "", "Assets", IconName + ".png");

    /// <summary>
    /// Text of the menu entry. Exec has no arguments: the portable mode is chosen by the marker next to the program,
    /// so the same command starts the program in its current mode. <paramref name="iconHashes"/> are the SHA-256 of the
    /// icon files this program owns (<see cref="IconHashKey"/>, a <c>;</c>-terminated list); none when the icon is not ours.
    /// </summary>
    public static string Content(string executable, bool icon, IReadOnlyCollection<string>? iconHashes = null)
    {
        if (!Path.IsPathRooted(executable)) throw new ArgumentException("Путь к программе должен быть полным.");
        return "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Version=1.0\n" +
            "Name=БазоНавигатор\n" +
            "GenericName=Список баз 1С\n" +
            "Comment=Запуск информационных баз 1С\n" +
            "Exec=" + ShortcutRequest.DesktopExec(executable, []) + "\n" +
            "Icon=" + (icon ? IconName : FallbackIcon) + "\n" +
            "Terminal=false\n" +
            "Categories=Office;\n" +
            "Keywords=1С;1C;базы;\n" +
            "StartupNotify=true\n" +
            "StartupWMClass=" + WindowClass + "\n" +
            (icon && iconHashes is { Count: > 0 } ? IconHashKey + "=" + string.Concat(iconHashes.Select(hash => hash + ";")) + "\n" : "") +
            OwnerKey + "=true\n";
    }

    /// <summary>Key of our entry with the SHA-256 (lowercase hex) of the icon files this program owns, as a list.</summary>
    public const string IconHashKey = "X-BaseNavigator-Icon-SHA256";

    /// <summary>Largest icon file hashed; a bigger one is not ours.</summary>
    public const int MaxIconSize = 16 * 1024 * 1024;

    /// <summary>Largest .desktop file read when checking ownership; a bigger file is not ours.</summary>
    public const int MaxEntrySize = 64 * 1024;

    /// <summary>
    /// True when the file is a menu entry written by this program: the ownership key is in the <c>[Desktop Entry]</c>
    /// group. At most <see cref="MaxEntrySize"/> bytes are read; BOM and CRLF are tolerated. A link or a non-regular
    /// file (FIFO, device) is not ours and is not opened.
    /// </summary>
    public static bool IsOwn(string path) => EntryGroup(path)?.Contains(OwnerKey + "=true") == true;

    /// <summary>Trimmed lines of the <c>[Desktop Entry]</c> group; null when the file is absent, not a regular file or too large.</summary>
    private static List<string>? EntryGroup(string path)
    {
        if (!File.Exists(path) || Kind(path) is not null) return null;
        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var buffer = new byte[MaxEntrySize + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0) length += read;
            if (length > MaxEntrySize) return null;
            using var reader = new StreamReader(new MemoryStream(buffer, 0, length), new System.Text.UTF8Encoding(false), true);
            text = reader.ReadToEnd();
        }
        var lines = new List<string>();
        var inside = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inside = line == "[Desktop Entry]"; continue; }
            if (inside) lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// Text of the refusal when the path itself is a symbolic link (even a broken one), exists but is not a regular
    /// file (directory, FIFO, device, socket), or exists and its type cannot be determined; null for a regular file or
    /// nothing. Checked without opening the path, so a FIFO cannot block. Parent folders that are links are accepted on
    /// purpose: dotfile managers (stow and the like) link ~/.local/share/applications, and it is the user's own home.
    /// </summary>
    public static string? FileProblem(string path) => Kind(path) switch
    {
        null => null,
        UnknownKind => "Не удалось определить тип файла " + path + "; БазоНавигатор его не изменяет.",
        var kind => "Файл " + path + " — " + kind + "; БазоНавигатор его не изменяет.",
    };

    private const string LinkKind = "символическая ссылка";
    private const string SpecialKind = "не обычный файл";
    private const string UnknownKind = "тип файла не определён";

    /// <summary>Result of <see cref="TypeProbe"/> for a path that does not exist.</summary>
    internal const int AbsentType = -1;
    /// <summary>Result of <see cref="TypeProbe"/> when the type could not be determined.</summary>
    internal const int UnknownType = -2;
    internal const int RegularType = 0x8000; // S_IFREG

    /// <summary>
    /// S_IFMT bits of the path itself (not following a link), <see cref="AbsentType"/> or <see cref="UnknownType"/>.
    /// Null uses statx on Unix and skips the check on Windows; tests replace it to simulate a failing probe on any OS.
    /// </summary>
    internal static Func<string, int>? TypeProbe { get; set; }

    private static string? Kind(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) return LinkKind;
        if (Directory.Exists(path)) return SpecialKind;
        var probe = TypeProbe ?? (OperatingSystem.IsWindows() ? null : UnixType);
        if (probe is null) return null;
        // Fail closed: a path that exists (checked without opening it) but whose type is unknown is refused, so a FIFO
        // is never opened just because statx failed.
        return probe(path) switch
        {
            AbsentType => null,
            UnknownType => File.Exists(path) ? UnknownKind : null,
            RegularType => null,
            _ => SpecialKind,
        };
    }

    /// <summary>
    /// .NET has no public file-type API: on Unix FileAttributes reports a FIFO or a device as Normal. statx gives the
    /// type without opening the path, and struct statx has one layout on every architecture (unlike struct stat; glibc
    /// before 2.33 does not even export lstat). statx is in glibc 2.28+, which .NET 10 on linux-x64 requires anyway, so
    /// the unavailable branch should not happen in practice; it is still treated as an unknown type (fail closed).
    /// </summary>
    private static int UnixType(string path)
    {
        var buffer = new byte[256]; // struct statx; stx_mode (u16) at offset 28
        try
        {
            if (statx(-100, path, 0x100, 1, buffer) != 0) // AT_FDCWD, AT_SYMLINK_NOFOLLOW, STATX_TYPE
                return System.Runtime.InteropServices.Marshal.GetLastPInvokeError() is 2 or 20 ? AbsentType : UnknownType; // ENOENT, ENOTDIR
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return UnknownType; }
        return BitConverter.ToUInt16(buffer, 28) & 0xF000;
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int statx(int directory, string path, int flags, uint mask, byte[] buffer);

    private static void Refuse(params string[] paths)
    {
        foreach (var path in paths)
            if (FileProblem(path) is { } problem) throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// Installs or updates the menu entry and the icon. A repeated call rewrites the path to the program.
    /// A foreign <see cref="FileName"/> (without the ownership key) is not overwritten; a link, a non-regular file or a
    /// file of unknown type at the entry or icon path is refused. The icon is ours only when its SHA-256 is among the
    /// hashes our own entry records (<see cref="IconHashKey"/>); nothing else, not even equality with the asset, makes
    /// it ours. An absent or own icon is replaced by the asset: the entry is written first and lists the asset's hash
    /// together with the current icon's (the next install trims the list); if the icon write then fails, the entry is
    /// rewritten best-effort with the hash of the icon actually on disk only, and the error is reported. A foreign icon
    /// stays, is reported, and the entry records no hash. An icon that exists but cannot be read stays, is reported, and
    /// the entry keeps the hashes recorded before. Without a readable asset the icon is left as it is and the entry keeps
    /// its hash while it is ours. The entry text is built (and the path validated) before anything is written.
    /// Not guarded against a concurrent change between the checks and the write: the user's home is not a security boundary.
    /// </summary>
    /// <returns>Path of the written .desktop file and a note about a kept foreign icon, null when there is none.</returns>
    public static (string Entry, string? Note) Install(string executable, Func<string, string?> variable)
    {
        var data = DataHome(variable);
        var entry = EntryPath(data);
        var iconPath = IconPath(data);
        Refuse(entry, iconPath);
        var group = EntryGroup(entry);
        var own = group?.Contains(OwnerKey + "=true") == true;
        if (File.Exists(entry) && !own)
            throw new InvalidOperationException("Ярлык " + entry + " создан не БазоНавигатором; он не изменён. Удалите или переименуйте его и повторите.");
        var recorded = own ? RecordedHashes(group) : [];
        var exists = File.Exists(iconPath);
        var (current, unreadable) = exists ? IconHash(iconPath) : (null, false);
        var ours = current is not null && recorded.Contains(current) ? current : null;
        var asset = ReadIcon(AssetPath(executable));
        var hashes = new List<string>();
        string? note = null;
        var write = false;
        if (unreadable)
        {
            // Cannot tell whose it is now: leave it and keep what our entry recorded, so ownership survives a transient error.
            hashes.AddRange(recorded);
            note = "Значок " + iconPath + " не удалось прочитать — оставлен.";
        }
        else if (asset is not null && (!exists || ours is not null))
        {
            hashes.Add(Hash(asset));
            if (ours is not null && ours != hashes[0]) hashes.Add(ours);
            write = true;
        }
        else if (ours is not null) hashes.Add(ours);
        else if (asset is not null) note = "Значок " + iconPath + " не наш — оставлен.";
        var icon = asset is not null || hashes.Count > 0;
        var text = Content(executable, icon, hashes);
        Replace(entry, temp => File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false)));
        if (write && asset is not null)
        {
            try { Replace(iconPath, temp => File.WriteAllBytes(temp, asset)); }
            catch (Exception failure)
            {
                // The asset's hash was only planned: list just the icon that is actually on disk, then report the failure.
                var previous = Content(executable, icon, ours is null ? [] : [ours]);
                try { Replace(entry, temp => File.WriteAllText(temp, previous, new System.Text.UTF8Encoding(false))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (failure is IOException or UnauthorizedAccessException) throw new IconNotWrittenException(failure);
                throw;
            }
        }
        return (entry, note);
    }

    /// <summary>
    /// Removes the own menu entry and icon. The .desktop is removed only with the ownership key; a link, a non-regular
    /// file or a file of unknown type at the entry path is refused. The icon is removed only when our entry records its
    /// SHA-256 (<see cref="IconHashKey"/>); the asset is never read, so a missing or unreadable one cannot block removal.
    /// An icon we wrote but that was changed since, an icon that cannot be read, or a link, a non-regular file or a file
    /// of unknown type at the icon path, stays and is reported while our entry is still removed.
    /// Not guarded against a concurrent change between the checks and the deletion: the user's home is not a security boundary.
    /// </summary>
    /// <returns>Removed files (empty when nothing of ours was installed) and a note about a kept icon, null when there is none.</returns>
    public static (IReadOnlyList<string> Removed, string? Note) Uninstall(Func<string, string?> variable)
    {
        var data = DataHome(variable);
        var entry = EntryPath(data);
        var icon = IconPath(data);
        Refuse(entry);
        var removed = new List<string>();
        var group = EntryGroup(entry);
        var own = group?.Contains(OwnerKey + "=true") == true;
        string? note = null;
        if (own && Kind(icon) is { } kind) note = "Значок " + icon + " — " + kind + " — оставлен.";
        else if (own && File.Exists(icon))
        {
            var recorded = RecordedHashes(group);
            var (current, unreadable) = IconHash(icon);
            if (unreadable) note = "Значок " + icon + " не удалось прочитать — оставлен.";
            else if (current is not null && recorded.Contains(current)) { File.Delete(icon); removed.Add(icon); }
            else if (recorded.Count > 0) note = "Значок " + icon + " изменён — оставлен.";
        }
        if (own) { File.Delete(entry); removed.Add(entry); }
        return (removed, note);
    }

    /// <summary>Most hashes read from <see cref="IconHashKey"/>; the rest of a longer list is ignored.</summary>
    public const int MaxIconHashes = 8;

    /// <summary>
    /// Hashes listed in the first <see cref="IconHashKey"/> of the entry group: a <c>;</c>-separated list (Desktop Entry
    /// list syntax), spaces and empty items tolerated, items other than 64 hex digits ignored, at most
    /// <see cref="MaxIconHashes"/> items considered. Lowercase.
    /// </summary>
    private static HashSet<string> RecordedHashes(List<string>? group)
    {
        var line = group?.FirstOrDefault(line => line.StartsWith(IconHashKey + "=", StringComparison.Ordinal));
        if (line is null) return [];
        return line[(IconHashKey.Length + 1)..].Split(';').Take(MaxIconHashes)
            .Select(item => item.Trim().ToLowerInvariant())
            .Where(item => item.Length == 64 && item.All(char.IsAsciiHexDigitLower))
            .ToHashSet();
    }

    /// <summary>
    /// Bytes of a regular file up to <see cref="MaxIconSize"/>; null when it is absent, not regular, larger or unreadable.
    /// </summary>
    private static byte[]? ReadIcon(string path)
    {
        if (!File.Exists(path) || Kind(path) is not null || new FileInfo(path).Length > MaxIconSize) return null;
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// SHA-256 of the icon file; null for a file that is not hashed (absent, not regular, larger than
    /// <see cref="MaxIconSize"/>). <c>Unreadable</c> when it exists but reading failed: then nothing is known about it.
    /// </summary>
    private static (string? Hash, bool Unreadable) IconHash(string path)
    {
        if (!File.Exists(path) || Kind(path) is not null || new FileInfo(path).Length > MaxIconSize) return (null, false);
        try { return (Hash(File.ReadAllBytes(path)), false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, true); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static void Replace(string path, Action<string> write)
    {
        var folder = Path.GetDirectoryName(path) ?? throw new ArgumentException("Неверный путь ярлыка.");
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        try
        {
            write(temp);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

/// <summary>The menu entry was written, but writing the icon failed: <see cref="Reason"/> tells why.</summary>
public sealed class IconNotWrittenException(Exception reason) : IOException(reason.Message, reason)
{
    public Exception Reason { get; } = reason;
}

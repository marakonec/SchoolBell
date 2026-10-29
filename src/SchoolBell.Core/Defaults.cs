namespace SchoolBell.Core;

/// <summary>
/// Стандартні звуки тривоги й відбою, що постачаються разом із програмою
/// (папка sounds поруч із SchoolBell.exe). Підставляються, якщо користувач свого файлу не обрав
/// або обраний файл зник, щоб тривога ніколи не звучала як резервний «біп».
/// </summary>
public static class DefaultSounds
{
    public const string AirAlertFile = "air-alert.mp3";
    public const string AllClearFile = "all-clear.mp3";

    /// <summary>Папка зі стандартними звуками поруч із програмою.</summary>
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "sounds");

    /// <summary>
    /// Підставляє стандартні звуки тривоги й відбою, де їх бракує.
    /// Повертає true, якщо налаштування змінено (їх треба зберегти).
    /// </summary>
    public static bool EnsureDefaults(AppSettings s, string bundledDir)
    {
        bool changed = false;
        changed |= Ensure(s, SoundKind.AirAlert, Path.Combine(bundledDir, AirAlertFile));
        changed |= Ensure(s, SoundKind.AllClear, Path.Combine(bundledDir, AllClearFile));
        return changed;
    }

    private static bool Ensure(AppSettings s, SoundKind kind, string bundled)
    {
        var current = kind == SoundKind.AirAlert ? s.AirAlertSound : s.AllClearSound;
        if (current is not null && File.Exists(current.StoredPath)) return false;
        if (!File.Exists(bundled)) return false;
        try
        {
            var imported = SettingsStore.ImportSound(bundled, kind);
            imported.OriginalName = "Стандартний: " + Path.GetFileName(bundled);
            s.SetSound(kind, imported);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Не вдалося підставити стандартний звук {bundled}", ex);
            return false;
        }
    }
}

/// <summary>
/// Чи оголошувати тривогу, яку програма застала вже активною під час першої перевірки
/// (ПК увімкнули або програму перезапустили посеред тривоги).
/// </summary>
public static class OngoingAlertPolicy
{
    /// <summary>
    /// Оголошуємо, якщо сьогодні цю тривогу ще не оголошували: після останнього відбою
    /// в журналі дня немає запису «тривога почалась». Так уранці, коли ПК вмикають під час
    /// нічної тривоги, люди її почують, а перезапуск програми посеред тривоги не дублює сирену.
    /// </summary>
    public static bool ShouldAnnounce(IEnumerable<JournalEntry> todayEntries)
    {
        bool announced = false;
        foreach (var e in todayEntries.OrderBy(e => e.At))
        {
            if (e.Kind == JournalKind.AlertEnded) announced = false;
            else if (e.Kind == JournalKind.AlertStarted) announced = true;
        }
        return !announced;
    }
}

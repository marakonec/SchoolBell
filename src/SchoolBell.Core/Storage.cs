using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolBell.Core;

public static class AppPaths
{
    /// <summary>%ProgramData%\SchoolBell (можна перевизначити змінною SCHOOLBELL_DATA для тестів).</summary>
    public static string DataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("SCHOOLBELL_DATA");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SchoolBell");
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string SoundsDir => Path.Combine(DataDir, "sounds");
    public static string LogsDir => Path.Combine(DataDir, "logs");
}

/// <summary>Невеликий прихований файл помилок (до ~1 МБ, з одним архівом).</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                var file = Path.Combine(AppPaths.LogsDir, "error.log");
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    var old = Path.Combine(AppPaths.LogsDir, "error.old.log");
                    File.Copy(file, old, overwrite: true);
                    File.Delete(file);
                }
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ").AppendLine(message);
                if (ex is not null) sb.AppendLine(ex.ToString());
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Журнал ніколи не повинен валити програму.
        }
    }
}

internal sealed class TimeOnlyHmConverter : JsonConverter<TimeOnly>
{
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? "";
        if (TimeParsing.TryParse(s, out var t)) return t;
        throw new JsonException($"Невірний час: '{s}'");
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("HH:mm", CultureInfo.InvariantCulture));
}

public static class TimeParsing
{
    private static readonly string[] Formats = { "H:mm", "HH:mm", "H.mm", "HH.mm", "HH:mm:ss", "H:mm:ss" };

    /// <summary>Приймає «8:30», «08:30», «8.30».</summary>
    public static bool TryParse(string? text, out TimeOnly time)
        => TimeOnly.TryParseExact((text ?? "").Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string Format(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}

public sealed class LoadResult
{
    public required AppSettings Settings { get; init; }

    /// <summary>Повідомлення для користувача, якщо файл налаштувань був пошкоджений.</summary>
    public string? Warning { get; init; }
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new TimeOnlyHmConverter() }
    };

    public static LoadResult Load()
    {
        var main = AppPaths.SettingsFile;
        var backup = main + ".bak";

        if (!File.Exists(main) && !File.Exists(backup))
            return new LoadResult { Settings = AppSettings.CreateInitial() };

        if (TryRead(main, out var s, out var error))
            return new LoadResult { Settings = s! };

        ErrorLog.Write($"Не вдалося прочитати {main}: {error}");
        if (TryRead(backup, out s, out var error2))
            return new LoadResult
            {
                Settings = s!,
                Warning = "Файл налаштувань був пошкоджений, відновлено з резервної копії. Перевірте розклад."
            };

        ErrorLog.Write($"Не вдалося прочитати {backup}: {error2}");
        return new LoadResult
        {
            Settings = AppSettings.CreateInitial(),
            Warning = "Файл налаштувань пошкоджений, завантажено стандартні налаштування. Перевірте розклад і звуки."
        };
    }

    private static bool TryRead(string path, out AppSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        try
        {
            if (!File.Exists(path)) { error = "файл відсутній"; return false; }
            var json = File.ReadAllText(path, Encoding.UTF8);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null) { error = "порожній файл"; return false; }
            Normalize(settings);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Normalize(AppSettings s)
    {
        s.Lessons ??= new();
        s.BellDays ??= new();
        s.SilenceDays ??= new();
        s.InactivityPeriods ??= new();
        s.Alerts ??= new();
        s.Alerts.RegionChain ??= new();
        foreach (var p in s.InactivityPeriods) p.Days ??= new();
    }

    /// <summary>Атомарне збереження: тимчасовий файл → заміна з резервною копією.</summary>
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var main = AppPaths.SettingsFile;
        var tmp = main + ".tmp";
        var backup = main + ".bak";

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            w.Write(json);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }

        // Пошкоджений основний файл не повинен затерти справну резервну копію.
        if (File.Exists(main) && !TryRead(main, out _, out _))
            File.Delete(main);

        if (File.Exists(main))
            File.Replace(tmp, main, backup, ignoreMetadataErrors: true);
        else
            File.Move(tmp, main);

        CleanupSounds(settings);
    }

    public static AppSettings Clone(AppSettings s)
        => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions)!;

    /// <summary>
    /// Копіює обраний користувачем файл у папку програми, щоб дзвінок не зник,
    /// якщо оригінал видалили або флешку витягли.
    /// </summary>
    public static SoundFile ImportSound(string sourcePath, SoundKind kind)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Файл не знайдено", sourcePath);
        Directory.CreateDirectory(AppPaths.SoundsDir);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp3";
        var name = $"{kind}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext.ToLowerInvariant()}";
        var dest = Path.Combine(AppPaths.SoundsDir, name);
        File.Copy(sourcePath, dest, overwrite: true);
        return new SoundFile { StoredPath = dest, OriginalName = Path.GetFileName(sourcePath) };
    }

    /// <summary>Видаляє з папки sounds файли, на які більше немає посилань.</summary>
    private static void CleanupSounds(AppSettings settings)
    {
        try
        {
            if (!Directory.Exists(AppPaths.SoundsDir)) return;
            var used = new HashSet<string>(
                settings.AllSoundFiles().Select(f => Path.GetFullPath(f.StoredPath)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(AppPaths.SoundsDir))
            {
                if (used.Contains(Path.GetFullPath(file))) continue;
                // Не чіпаємо щойно імпортовані файли (ще не збережені в налаштуваннях).
                if (DateTime.Now - File.GetCreationTime(file) < TimeSpan.FromHours(1)) continue;
                try { File.Delete(file); } catch { /* можливо, зараз відтворюється */ }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Очищення папки звуків", ex);
        }
    }
}

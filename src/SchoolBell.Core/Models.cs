namespace SchoolBell.Core;

/// <summary>Усі звуки, які вміє відтворювати програма.</summary>
public enum SoundKind
{
    BellStart,   // дзвінок на урок
    BellEnd,     // дзвінок з уроку
    Silence,     // хвилина мовчання
    AirAlert,    // повітряна тривога
    AllClear     // відбій
}

public static class SoundKindNames
{
    public static string Title(SoundKind kind) => kind switch
    {
        SoundKind.BellStart => "Дзвінок на урок",
        SoundKind.BellEnd => "Дзвінок з уроку",
        SoundKind.Silence => "Хвилина мовчання",
        SoundKind.AirAlert => "Повітряна тривога",
        SoundKind.AllClear => "Відбій тривоги",
        _ => kind.ToString()
    };
}

/// <summary>Звуковий файл, скопійований у папку даних програми.</summary>
public sealed class SoundFile
{
    /// <summary>Повний шлях до копії у %ProgramData%\SchoolBell\sounds.</summary>
    public string StoredPath { get; set; } = "";

    /// <summary>Ім'я файлу, яке обрав користувач (для показу в інтерфейсі).</summary>
    public string OriginalName { get; set; } = "";
}

public sealed class Lesson
{
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }

    public Lesson() { }
    public Lesson(TimeOnly start, TimeOnly end) { Start = start; End = end; }
}

/// <summary>
/// Період, коли нічого не відтворюється автоматично.
/// Якщо From == To — неактивний увесь день.
/// Якщо From &gt; To — період переходить через північ (відлік від дня початку).
/// </summary>
public sealed class InactivityPeriod
{
    public string Name { get; set; } = "";
    public List<DayOfWeek> Days { get; set; } = new();
    public TimeOnly From { get; set; }
    public TimeOnly To { get; set; }

    public bool IsWholeDay => From == To;
}

public sealed class AlertSettings
{
    /// <summary>Відстежувати тривогу через API.</summary>
    public bool MonitorEnabled { get; set; }

    /// <summary>Автоматично відтворювати звуки тривоги та відбою.</summary>
    public bool AutoPlay { get; set; } = true;

    public string? RegionId { get; set; }
    public string? RegionName { get; set; }

    /// <summary>Ланцюжок ID від області до обраного регіону (область → район → громада).</summary>
    public List<string> RegionChain { get; set; } = new();

    /// <summary>Ключ api.ukrainealarm.com. Якщо порожній — використовується публічне дзеркало siren.pp.ua.</summary>
    public string? ApiKey { get; set; }

    public int PollSeconds { get; set; } = 15;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Головний вимикач (канікули/свята). Вимикає все автоматичне, включно з тривогою.</summary>
    public bool Enabled { get; set; } = true;

    public bool StartMinimized { get; set; }

    public List<Lesson> Lessons { get; set; } = new();
    public List<DayOfWeek> BellDays { get; set; } = new();

    public bool SilenceEnabled { get; set; } = true;
    public TimeOnly SilenceTime { get; set; } = new(9, 0);
    public List<DayOfWeek> SilenceDays { get; set; } = new();

    /// <summary>Окремі файли для дзвінка на урок і з уроку.</summary>
    public bool SeparateBellFiles { get; set; }

    public SoundFile? BellStartSound { get; set; }
    public SoundFile? BellEndSound { get; set; }
    public SoundFile? SilenceSound { get; set; }
    public SoundFile? AirAlertSound { get; set; }
    public SoundFile? AllClearSound { get; set; }

    public List<InactivityPeriod> InactivityPeriods { get; set; } = new();

    public AlertSettings Alerts { get; set; } = new();

    public static readonly DayOfWeek[] Workdays =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
    };

    public static AppSettings CreateDefault() => new()
    {
        Lessons =
        {
            new(new(8, 30), new(9, 15)),
            new(new(9, 25), new(10, 10)),
            new(new(10, 25), new(11, 10)),
            new(new(11, 25), new(12, 10)),
            new(new(12, 20), new(13, 5)),
            new(new(13, 15), new(14, 0)),
            new(new(14, 10), new(14, 55)),
        },
        BellDays = Workdays.ToList(),
        SilenceDays = Workdays.ToList(),
        InactivityPeriods =
        {
            new InactivityPeriod
            {
                Name = "Вихідні",
                Days = { DayOfWeek.Saturday, DayOfWeek.Sunday },
                From = new(0, 0),
                To = new(0, 0)
            }
        }
    };

    /// <summary>
    /// Налаштування першого запуску (коли settings.json ще немає).
    /// Профіль «лише тривога»: у закладі вже є своя система дзвінків, тому дзвінки на пари
    /// і хвилина мовчання вимкнені, а періодів неактивності немає. Тривога лунає в будь-який день,
    /// коли ПК увімкнений. Розклад і хвилину мовчання можна увімкнути в інтерфейсі.
    /// </summary>
    public static AppSettings CreateInitial()
    {
        var s = CreateDefault();
        s.BellDays.Clear();
        s.SilenceEnabled = false;
        s.InactivityPeriods.Clear();
        return s;
    }

    /// <summary>Файл, який реально треба відтворити для звуку (з урахуванням «одного дзвінка»).</summary>
    public SoundFile? ResolveSound(SoundKind kind) => kind switch
    {
        SoundKind.BellStart => BellStartSound,
        SoundKind.BellEnd => SeparateBellFiles ? BellEndSound : BellStartSound,
        SoundKind.Silence => SilenceSound,
        SoundKind.AirAlert => AirAlertSound,
        SoundKind.AllClear => AllClearSound,
        _ => null
    };

    public void SetSound(SoundKind kind, SoundFile? file)
    {
        switch (kind)
        {
            case SoundKind.BellStart: BellStartSound = file; break;
            case SoundKind.BellEnd: BellEndSound = file; break;
            case SoundKind.Silence: SilenceSound = file; break;
            case SoundKind.AirAlert: AirAlertSound = file; break;
            case SoundKind.AllClear: AllClearSound = file; break;
        }
    }

    public IEnumerable<SoundFile> AllSoundFiles()
    {
        foreach (var f in new[] { BellStartSound, BellEndSound, SilenceSound, AirAlertSound, AllClearSound })
            if (f is not null) yield return f;
    }

    /// <summary>Перевірка перед збереженням. Повертає список помилок українською.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        for (int i = 0; i < Lessons.Count; i++)
        {
            var l = Lessons[i];
            if (l.End <= l.Start)
                errors.Add($"Урок {i + 1}: кінець ({l.End:HH\\:mm}) має бути пізніше за початок ({l.Start:HH\\:mm}).");
            if (i > 0 && l.Start < Lessons[i - 1].End)
                errors.Add($"Урок {i + 1} починається раніше, ніж закінчується урок {i}.");
        }
        foreach (var p in InactivityPeriods)
            if (p.Days.Count == 0)
                errors.Add($"Період неактивності «{p.Name}»: не обрано жодного дня.");
        if (Alerts.PollSeconds < 5 || Alerts.PollSeconds > 300)
            errors.Add("Інтервал перевірки тривоги має бути від 5 до 300 секунд.");
        if (Alerts.MonitorEnabled && string.IsNullOrWhiteSpace(Alerts.RegionId))
            errors.Add("Для відстеження тривоги оберіть громаду або район.");
        return errors;
    }
}

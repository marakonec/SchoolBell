using System;
using System.Net.Http;
using System.Windows.Threading;
using SchoolBell.Core;

namespace SchoolBell.App;

/// <summary>
/// «Серце» програми: налаштування, таймер розкладу, черга відтворення, монітор тривоги.
/// Працює незалежно від того, відкрите вікно чи ні.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly DueEventTracker _due = new();
    private volatile AppSettings _settings;

    public AppSettings Settings => _settings;
    public AudioOutput Audio { get; }
    public PlaybackCoordinator Player { get; }
    public AlertMonitor AlertMonitor { get; }
    public HttpClient Http { get; }

    /// <summary>Журнал подій поточного дня (для стрічки на головній вкладці).</summary>
    public DayJournal Journal { get; }

    public AlertSnapshot? LastAlert { get; private set; }

    /// <summary>Попередження, яке треба показати користувачу (пошкоджені налаштування, проблеми зі звуком).</summary>
    public string? Warning { get; private set; }

    /// <summary>Щосекундний «пульс» для оновлення інтерфейсу.</summary>
    public event Action? Tick;
    public event Action? SettingsChanged;
    public event Action? AlertChanged;
    public event Action? WarningChanged;

    public AppHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        var load = SettingsStore.Load();
        _settings = load.Settings;
        Warning = load.Warning;

        // Стандартні звуки тривоги й відбою з папки програми, якщо свої не обрано.
        try
        {
            if (DefaultSounds.EnsureDefaults(_settings, DefaultSounds.BundledDir))
                SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Не вдалося зберегти стандартні звуки в налаштуваннях", ex);
        }

        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SchoolBell/1.0");

        Audio = new AudioOutput(dispatcher);
        Audio.Problem += msg => SetWarning(msg);
        Player = new PlaybackCoordinator(Audio, () => _settings, () => DateTime.Now);
        Journal = new DayJournal(DateTime.Now);

        AlertMonitor = new AlertMonitor(Http, () => _settings);
        AlertMonitor.Updated += snap => _dispatcher.InvokeAsync(() =>
        {
            LastAlert = snap;
            AlertChanged?.Invoke();
        });
        AlertMonitor.Transition += (t, info) => _dispatcher.InvokeAsync(() => OnAlertTransition(t, info));

        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => OnTimer();
    }

    public void Start()
    {
        _timer.Start();
        AlertMonitor.Start();
    }

    private DateTime _lastUiTick;

    private void OnTimer()
    {
        var now = DateTime.Now;
        try
        {
            Journal.EnsureDay(now);
            foreach (var e in _due.Poll(_settings, now))
            {
                // Захист від повтору, якщо програму перезапустили одразу після дзвінка.
                if (Journal.HasScheduled(e.Key)) continue;
                Journal.Add(new JournalEntry
                {
                    At = now, Kind = JournalKind.Scheduled, Sound = e.Sound, Title = e.Title, ScheduleKey = e.Key
                });
                Player.RequestAuto(e.Sound, e.Title, e.Key);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Помилка таймера розкладу", ex);
        }

        if ((now - _lastUiTick).TotalMilliseconds >= 900 || now < _lastUiTick)
        {
            _lastUiTick = now;
            try { Tick?.Invoke(); }
            catch (Exception ex) { ErrorLog.Write("Помилка оновлення інтерфейсу", ex); }
        }
    }

    private void OnAlertTransition(AlertTransition t, AirAlertInfo? info)
    {
        var s = _settings;
        var now = DateTime.Now;
        string? whyNoSound = !s.Alerts.AutoPlay ? "автовідтворення вимкнено"
            : !s.Enabled ? "розклад вимкнено"
            : Schedule.FindInactivePeriod(s, now) is { } p ? $"неактивний період «{p.Name}»"
            : null;
        string levelSuffix = info is { Level: not AlertLevel.Unspecified } ? $" — {info.LevelTitle}" : "";

        switch (t)
        {
            case AlertTransition.Started:
            {
                var e = Journal.Add(new JournalEntry
                {
                    At = now, Kind = JournalKind.AlertStarted, Sound = SoundKind.AirAlert, Level = info?.Level,
                    Title = "Повітряна тривога" + levelSuffix,
                    Detail = Join(info?.Reason, whyNoSound is null ? null : "сигнал не відтворено: " + whyNoSound)
                });
                if (whyNoSound is null) Player.RequestAuto(SoundKind.AirAlert, "Повітряна тривога", e.Id);
                break;
            }
            case AlertTransition.Ended:
            {
                var e = Journal.Add(new JournalEntry
                {
                    At = now, Kind = JournalKind.AlertEnded, Sound = SoundKind.AllClear,
                    Title = "Відбій тривоги",
                    Detail = whyNoSound is null ? null : "сигнал не відтворено: " + whyNoSound
                });
                if (whyNoSound is null) Player.RequestAuto(SoundKind.AllClear, "Відбій тривоги", e.Id);
                break;
            }
            case AlertTransition.LevelChanged:
                Journal.Add(new JournalEntry
                {
                    At = now, Kind = JournalKind.AlertLevelChanged, Level = info?.Level,
                    Title = "Рівень тривоги змінився" + levelSuffix, Detail = info?.Reason
                });
                break;
            case AlertTransition.AlreadyActive when whyNoSound is null && OngoingAlertPolicy.ShouldAnnounce(Journal.Entries):
            {
                // ПК увімкнули (або програму запустили) посеред тривоги, яку сьогодні ще не оголошували.
                var e = Journal.Add(new JournalEntry
                {
                    At = now, Kind = JournalKind.AlertStarted, Sound = SoundKind.AirAlert, Level = info?.Level,
                    Title = "Повітряна тривога" + levelSuffix,
                    Detail = Join(info?.Reason, "тривала на момент запуску програми")
                });
                Player.RequestAuto(SoundKind.AirAlert, "Повітряна тривога", e.Id);
                break;
            }
            case AlertTransition.AlreadyActive:
                // Не дублюємо запис, якщо тривога вже є в журналі (напр. після перезапуску програми).
                if (Journal.LastAlertEntry()?.Kind is null or JournalKind.AlertEnded)
                    Journal.Add(new JournalEntry
                    {
                        At = now, Kind = JournalKind.AlertOngoing, Level = info?.Level,
                        Title = "Триває повітряна тривога" + levelSuffix,
                        Detail = Join(info?.Reason, "виявлено під час першої перевірки, сигнал не відтворювався")
                    });
                break;
        }
    }

    private static string? Join(params string?[] parts)
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var p in parts) if (!string.IsNullOrWhiteSpace(p)) list.Add(p!);
        return list.Count == 0 ? null : string.Join("; ", list);
    }

    /// <summary>Ручний запуск звуку кнопкою (записується в журнал дня).</summary>
    public void PlayManual(SoundKind kind)
    {
        var title = kind == SoundKind.BellStart && !_settings.SeparateBellFiles ? "Дзвінок" : SoundKindNames.Title(kind);
        var e = Journal.Add(new JournalEntry
        {
            At = DateTime.Now, Kind = JournalKind.Manual, Sound = kind, Title = title, Detail = "запущено вручну"
        });
        Player.RequestManual(kind, null, title + " (вручну)", e.Id);
    }

    /// <summary>Зберегти нові налаштування і застосувати їх. Кидає виняток, якщо запис не вдався.</summary>
    public void ApplySettings(AppSettings s)
    {
        SettingsStore.Save(s);
        _settings = s;
        AlertMonitor.PollNow();
        SettingsChanged?.Invoke();
    }

    /// <summary>Головний вимикач (канікули).</summary>
    public void SetEnabled(bool enabled)
    {
        if (_settings.Enabled == enabled) return;
        var copy = SettingsStore.Clone(_settings);
        copy.Enabled = enabled;
        ApplySettings(copy);
        Journal.Add(new JournalEntry
        {
            At = DateTime.Now, Kind = JournalKind.ScheduleToggled, Enabled = enabled,
            Title = enabled ? "Розклад увімкнено" : "Розклад вимкнено",
            Detail = "головний вимикач"
        });
        if (!enabled) Player.StopAll();
    }

    public void SetWarning(string? message)
    {
        Warning = message;
        WarningChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        try { Player.StopAll(); } catch { /* ignore */ }
        AlertMonitor.Dispose();
        Http.Dispose();
    }
}

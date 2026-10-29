using SchoolBell.Core;

// Простий набір тестів без сторонніх пакетів: `dotnet run --project tests/SchoolBell.Core.Tests`
var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));
void Eq<T>(T expected, T actual, string? what = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what ?? "значення"}: очікувалось «{expected}», отримано «{actual}»");
}
void True(bool cond, string what) { if (!cond) throw new Exception(what); }

// Дати для тестів: 2026-10-12 — понеділок, 2026-10-16 — п'ятниця, 2026-10-17 — субота.
var mon = new DateOnly(2026, 10, 12);
var fri = new DateOnly(2026, 10, 16);
var sat = new DateOnly(2026, 10, 17);
DateTime At(DateOnly d, int h, int m, int s = 0) => d.ToDateTime(new TimeOnly(h, m, s));

AppSettings Simple()
{
    var s = AppSettings.CreateDefault();
    s.InactivityPeriods.Clear();
    s.Lessons = new() { new(new(8, 30), new(9, 15)), new(new(9, 25), new(10, 10)) };
    return s;
}

// ---------- Розклад ----------

Test("Будній день: 2 дзвінки на урок + 2 з уроку + хвилина мовчання", () =>
{
    var e = Schedule.EventsOn(Simple(), mon);
    Eq(5, e.Count, "кількість подій");
    Eq(SoundKind.BellStart, e[0].Sound);
    Eq(At(mon, 9, 0), e[1].At, "хвилина мовчання о 9:00");
    Eq(SoundKind.Silence, e[1].Sound);
});

Test("Субота поза днями розкладу — подій немає", () =>
{
    Eq(0, Schedule.EventsOn(Simple(), sat).Count);
});

Test("Дні хвилини мовчання налаштовуються окремо", () =>
{
    var s = Simple();
    s.SilenceDays.Add(DayOfWeek.Saturday);
    var e = Schedule.EventsOn(s, sat);
    Eq(1, e.Count);
    Eq(SoundKind.Silence, e[0].Sound);
});

Test("Хвилина мовчання йде першою, якщо збігається з дзвінком", () =>
{
    var s = Simple();
    s.SilenceTime = new(8, 30);
    var e = Schedule.EventsOn(s, mon);
    Eq(SoundKind.Silence, e[0].Sound);
    Eq(SoundKind.BellStart, e[1].Sound);
});

Test("Неактивність: увесь день", () =>
{
    var s = Simple();
    s.InactivityPeriods.Add(new() { Name = "Вихідні", Days = { DayOfWeek.Saturday }, From = new(0, 0), To = new(0, 0) });
    True(Schedule.IsInactive(s, At(sat, 12, 0)), "субота неактивна");
    True(!Schedule.IsInactive(s, At(fri, 12, 0)), "п'ятниця активна");
});

Test("Неактивність через північ (пн–пт 16:00–07:30)", () =>
{
    var s = Simple();
    s.InactivityPeriods.Add(new() { Name = "Ніч", Days = AppSettings.Workdays.ToList(), From = new(16, 0), To = new(7, 30) });
    True(Schedule.IsInactive(s, At(mon, 17, 0)), "пн 17:00");
    True(!Schedule.IsInactive(s, At(mon, 12, 0)), "пн 12:00 активний");
    True(Schedule.IsInactive(s, At(sat, 7, 0)), "сб 07:00 — продовження п'ятничного періоду");
    True(!Schedule.IsInactive(s, At(sat, 8, 0)), "сб 08:00 вже поза періодом");
    True(!Schedule.IsInactive(s, At(mon, 7, 0)), "пн 07:00 — неділя не входить у дні");
});

Test("Наступна подія: після останнього уроку в п'ятницю → понеділок", () =>
{
    var n = Schedule.Next(Simple(), At(fri, 15, 0));
    True(n is not null, "подія існує");
    Eq(At(fri.AddDays(3), 8, 30), n!.At);
});

Test("Наступна подія пропускає період неактивності", () =>
{
    var s = Simple();
    s.InactivityPeriods.Add(new() { Name = "Збори", Days = { DayOfWeek.Monday }, From = new(8, 0), To = new(9, 20) });
    var n = Schedule.Next(s, At(mon, 7, 0));
    Eq(At(mon, 9, 25), n!.At);
});

Test("Головний вимикач: наступної події немає", () =>
{
    var s = Simple();
    s.Enabled = false;
    Eq(null, Schedule.Next(s, At(mon, 7, 0)));
});

// ---------- Відстеження настання подій ----------

Test("Подія спрацьовує один раз", () =>
{
    var s = Simple();
    var t = new DueEventTracker();
    Eq(0, t.Poll(s, At(mon, 8, 29, 59)).Count);
    var fired = t.Poll(s, At(mon, 8, 30, 0));
    Eq(1, fired.Count);
    Eq(0, t.Poll(s, At(mon, 8, 30, 1)).Count, "повтор");
});

Test("Запуск ПК через хвилину після дзвінка — дзвінок ще лунає", () =>
{
    var t = new DueEventTracker();
    Eq(1, t.Poll(Simple(), At(mon, 8, 31, 0)).Count);
});

Test("Запуск ПК через 5 хвилин після дзвінка — старий дзвінок не лунає", () =>
{
    var t = new DueEventTracker();
    Eq(0, t.Poll(Simple(), At(mon, 8, 35, 0)).Count);
});

Test("Вихід зі сну через 2 години — пропущені дзвінки не лунають", () =>
{
    var s = Simple();
    var t = new DueEventTracker();
    t.Poll(s, At(mon, 8, 0));
    Eq(0, t.Poll(s, At(mon, 10, 0)).Count);
});

Test("Годинник перевели назад — дзвінок не повторюється", () =>
{
    var s = Simple();
    var t = new DueEventTracker();
    t.Poll(s, At(mon, 8, 29, 59));
    Eq(1, t.Poll(s, At(mon, 8, 30, 1)).Count);
    Eq(0, t.Poll(s, At(mon, 8, 29, 50)).Count);
    Eq(0, t.Poll(s, At(mon, 8, 30, 5)).Count);
});

Test("Вимкнено / неактивність — подія не спрацьовує", () =>
{
    var s = Simple();
    s.Enabled = false;
    var t = new DueEventTracker();
    Eq(0, t.Poll(s, At(mon, 8, 30, 0)).Count);

    var s2 = Simple();
    s2.InactivityPeriods.Add(new() { Name = "x", Days = { DayOfWeek.Monday }, From = new(8, 0), To = new(9, 0) });
    var t2 = new DueEventTracker();
    Eq(0, t2.Poll(s2, At(mon, 8, 30, 0)).Count);
});

Test("Перехід через північ не губить подію о 00:00", () =>
{
    var s = Simple();
    s.Lessons = new() { new(new(0, 0), new(0, 45)) };
    var t = new DueEventTracker();
    t.Poll(s, At(mon.AddDays(-1), 23, 59, 59)); // неділя — не в днях, але перевіряємо механіку
    var fired = t.Poll(s, At(mon, 0, 0, 1));
    Eq(1, fired.Count);
});

// ---------- Черга відтворення ----------

var clockNow = At(mon, 8, 0);
var settingsForPlayback = Simple();
var output = new FakeOutput();
var pc = new PlaybackCoordinator(output, () => settingsForPlayback, () => clockNow);

Test("Тривога перериває дзвінок", () =>
{
    pc.StopAll(); output.Log.Clear();
    pc.RequestAuto(SoundKind.BellStart, "дзвінок");
    pc.RequestAuto(SoundKind.AirAlert, "тривога");
    Eq("play:BellStart,stop,play:AirAlert", string.Join(",", output.Log));
    Eq(SoundKind.AirAlert, pc.Current!.Sound);
});

Test("Дзвінок під час хвилини мовчання чекає в черзі", () =>
{
    pc.StopAll(); output.Log.Clear();
    pc.RequestAuto(SoundKind.Silence, "хвилина");
    pc.RequestAuto(SoundKind.BellEnd, "дзвінок");
    Eq(SoundKind.Silence, pc.Current!.Sound);
    output.End();
    Eq(SoundKind.BellEnd, pc.Current!.Sound);
    output.End();
    Eq(null, pc.Current);
});

Test("Прострочений дзвінок з черги пропускається", () =>
{
    pc.StopAll(); output.Log.Clear();
    pc.RequestAuto(SoundKind.AirAlert, "тривога");
    pc.RequestAuto(SoundKind.BellStart, "дзвінок");
    clockNow = clockNow.AddMinutes(10);
    output.End();
    Eq(null, pc.Current);
});

Test("Ручна кнопка перериває будь-що, Стоп очищує чергу", () =>
{
    pc.StopAll(); output.Log.Clear();
    pc.RequestAuto(SoundKind.AirAlert, "тривога");
    pc.RequestManual(SoundKind.BellStart);
    Eq(SoundKind.BellStart, pc.Current!.Sound);
    pc.RequestAuto(SoundKind.Silence, "хвилина");
    pc.StopAll();
    Eq(null, pc.Current);
    Eq(0, pc.Queue.Count);
});

Test("Один файл дзвінка: «з уроку» бере файл «на урок»", () =>
{
    var s = Simple();
    s.BellStartSound = new() { StoredPath = "a.mp3" };
    s.BellEndSound = new() { StoredPath = "b.mp3" };
    Eq("a.mp3", s.ResolveSound(SoundKind.BellEnd)!.StoredPath);
    s.SeparateBellFiles = true;
    Eq("b.mp3", s.ResolveSound(SoundKind.BellEnd)!.StoredPath);
});

// ---------- API тривог ----------

const string RegionsJson = """
{"states":[
  {"regionId":"14","regionName":"Київська область","regionType":"State","regionChildIds":[
    {"regionId":"137","regionName":"Бучанський район","regionType":"District","regionChildIds":[
      {"regionId":"1286","regionName":"Ірпінська територіальна громада","regionType":"Community","regionChildIds":[]},
      {"regionId":"1287","regionName":"Бучанська територіальна громада","regionType":"Community","regionChildIds":[]}
    ]}
  ]},
  {"regionId":"31","regionName":"м. Київ","regionType":"State","regionChildIds":[]}
]}
""";

Test("Розбір дерева регіонів і шлях до громади", () =>
{
    var roots = AlertParsing.ParseRegions(RegionsJson);
    Eq(2, roots.Count);
    var path = RegionTree.FindPath(roots, "1287");
    Eq("14,137,1287", string.Join(",", path.Select(p => p.Id)));
});

Test("Тривога в сусідній громаді не зачіпає нашу", () =>
{
    const string json = """
    [{"regionId":"14","regionName":"Київська область","activeAlerts":[
        {"regionId":"1286","regionType":"Community","type":"AIR","lastUpdate":"2026-10-10T08:00:00Z"}]}]
    """;
    var regions = AlertParsing.ParseAirAlerts(json);
    True(AlertParsing.StatusFor(regions, new[] { "14", "137", "1287" }) is null, "Буча без тривоги");
    True(AlertParsing.StatusFor(regions, new[] { "14", "137", "1286" }) is not null, "Ірпінь з тривогою");
});

Test("Тривога на всю область зачіпає громаду", () =>
{
    const string json = """
    [{"regionId":"14","activeAlerts":[{"regionId":"14","regionType":"State","type":"AIR"}]}]
    """;
    var regions = AlertParsing.ParseAirAlerts(json);
    True(AlertParsing.StatusFor(regions, new[] { "14", "137", "1287" }) is not null, "громада під тривогою області");
});

Test("Інші типи загроз не вважаються повітряною тривогою", () =>
{
    const string json = """
    [{"regionId":"14","activeAlerts":[{"regionId":"14","type":"ARTILLERY"}]}]
    """;
    Eq(0, AlertParsing.ParseAirAlerts(json).Count);
});

Test("Рівні тривоги: червоний, жовтий, без рівня", () =>
{
    const string json = """
    [{"regionId":"14","activeAlerts":[
        {"regionId":"1286","type":"AIR","activeAlertLevels":[{"alertLevel":"Yellow","reason":"Загроза БпЛА (жовтий рівень)"}]},
        {"regionId":"1287","type":"AIR","activeAlertLevels":[{"alertLevel":"Yellow"},{"alertLevel":"Red","reason":"Ракетна загроза (червоний рівень)"}]},
        {"regionId":"137","type":"AIR"}]}]
    """;
    var a = AlertParsing.ParseAirAlerts(json);
    Eq(AlertLevel.Yellow, a["1286"].Level, "Ірпінь");
    Eq("Загроза БпЛА (жовтий рівень)", a["1286"].Reason);
    Eq(AlertLevel.Red, a["1287"].Level, "Буча — береться червоний");
    Eq("Ракетна загроза (червоний рівень)", a["1287"].Reason);
    Eq(AlertLevel.Unspecified, a["137"].Level, "район без рівня");
});

Test("Рівень для громади з урахуванням району: найсерйозніший", () =>
{
    const string json = """
    [{"regionId":"14","activeAlerts":[
        {"regionId":"1286","type":"AIR","activeAlertLevels":[{"alertLevel":"Yellow"}]},
        {"regionId":"137","type":"AIR","activeAlertLevels":[{"alertLevel":"Red"}]}]}]
    """;
    var a = AlertParsing.ParseAirAlerts(json);
    Eq(AlertLevel.Red, AlertParsing.StatusFor(a, new[] { "14", "137", "1286" })!.Level);
    var onlyYellow = AlertParsing.ParseAirAlerts("""[{"regionId":"14","activeAlerts":[{"regionId":"1286","type":"AIR","activeAlertLevels":[{"alertLevel":"Yellow"}]}]}]""");
    Eq(AlertLevel.Yellow, AlertParsing.StatusFor(onlyYellow, new[] { "14", "137", "1286" })!.Level);
});

Test("Трекер: перше спостереження не дає сигналу, далі — тривога/відбій", () =>
{
    var t = new AlertStateTracker();
    var now = At(mon, 10, 0);
    var red = new AirAlertInfo(AlertLevel.Red, null);
    var yellow = new AirAlertInfo(AlertLevel.Yellow, null);
    Eq(AlertTransition.AlreadyActive, t.OnSuccess(red, now), "перше спостереження");
    Eq(AlertTransition.None, t.OnSuccess(red, now));
    Eq(AlertTransition.LevelChanged, t.OnSuccess(yellow, now), "зміна рівня — не нова тривога");
    Eq(AlertLevel.Yellow, t.Info!.Level);
    Eq(AlertTransition.Ended, t.OnSuccess(null, now));
    Eq(null, t.Info);
    t.OnFailure("x", now.AddSeconds(15)); t.OnFailure("x", now.AddSeconds(30));
    True(t.ConnectionLost, "зв'язок втрачено");
    Eq(now.AddSeconds(30), t.LastAttempt, "остання спроба фіксується і при помилці");
    Eq(AlertTransition.Started, t.OnSuccess(red, now), "стан збережено після збою");
    True(!t.ConnectionLost, "зв'язок відновлено");
});

// ---------- Збереження налаштувань ----------

var tmpDir = Path.Combine(Path.GetTempPath(), "schoolbell-tests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("SCHOOLBELL_DATA", tmpDir);

Test("Збереження і завантаження зберігають розклад", () =>
{
    var s = Simple();
    s.SilenceTime = new(9, 1);
    s.Alerts.RegionChain = new() { "14", "137" };
    SettingsStore.Save(s);
    var json = File.ReadAllText(AppPaths.SettingsFile);
    True(json.Contains("\"09:01\""), "час у форматі HH:mm");
    var loaded = SettingsStore.Load();
    Eq(null, loaded.Warning);
    Eq(new TimeOnly(9, 1), loaded.Settings.SilenceTime);
    Eq(2, loaded.Settings.Lessons.Count);
    Eq("14,137", string.Join(",", loaded.Settings.Alerts.RegionChain));
});

Test("Пошкоджений файл → відновлення з резервної копії", () =>
{
    var s = Simple();
    s.SilenceTime = new(9, 2);
    SettingsStore.Save(s);       // створює .bak з попередньої версії
    SettingsStore.Save(s);       // .bak тепер = 9:02
    File.WriteAllText(AppPaths.SettingsFile, "{ зламано");
    var loaded = SettingsStore.Load();
    True(loaded.Warning is not null, "є попередження");
    Eq(new TimeOnly(9, 2), loaded.Settings.SilenceTime);

    // Збереження після цього не повинно затерти справну копію зламаним файлом.
    SettingsStore.Save(loaded.Settings);
    Eq(null, SettingsStore.Load().Warning);
});

Test("Імпорт звуку копіює файл у папку програми", () =>
{
    var src = Path.Combine(tmpDir, "my bell.mp3");
    File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
    var f = SettingsStore.ImportSound(src, SoundKind.BellStart);
    True(File.Exists(f.StoredPath), "копія існує");
    True(f.StoredPath.StartsWith(AppPaths.SoundsDir), "у папці sounds");
    Eq("my bell.mp3", f.OriginalName);
});

Test("Перевірка: кінець уроку раніше за початок", () =>
{
    var s = Simple();
    s.Lessons[0].End = new(8, 0);
    True(s.Validate().Count > 0, "є помилка");
    True(Simple().Validate().Count == 0, "стандартні налаштування валідні");
});

Test("Розбір часу: 8:30, 08.30", () =>
{
    True(TimeParsing.TryParse("8:30", out var a) && a == new TimeOnly(8, 30), "8:30");
    True(TimeParsing.TryParse("08.30", out var b) && b == new TimeOnly(8, 30), "08.30");
    True(!TimeParsing.TryParse("25:00", out _), "25:00 невалідний");
});

// ---------- Стрічка подій дня ----------

Test("Стрічка: виконані, пропущені, наступна, позначка «зараз»", () =>
{
    var s = Simple(); // 8:30, 9:00 (мовчання), 9:15, 9:25, 10:10
    var j = new List<JournalEntry>
    {
        new() { At = At(mon, 8, 30, 1), Kind = JournalKind.Scheduled, ScheduleKey = $"{At(mon, 8, 30):yyyyMMddHHmm}:BellStart", Title = "x" },
        new() { At = At(mon, 9, 5), Kind = JournalKind.Manual, Sound = SoundKind.BellStart, Title = "Дзвінок" },
    };
    var items = Timeline.Build(s, mon, j, At(mon, 9, 20), null);
    var titles = items.Select(i => $"{i.At:HH:mm}:{i.State}").ToList();
    Eq("08:30:Done,09:00:Missed,09:05:Logged,09:15:Missed,09:20:Now,09:25:Next,10:10:Upcoming", string.Join(",", titles));
});

Test("Стрічка: неактивний період і вимкнений розклад", () =>
{
    var s = Simple();
    s.InactivityPeriods.Add(new() { Name = "Збори", Days = { DayOfWeek.Monday }, From = new(9, 20), To = new(9, 30) });
    var items = Timeline.Build(s, mon, new List<JournalEntry>(), At(mon, 9, 0, 30), null);
    var r = items.First(i => i.At == At(mon, 9, 25));
    Eq(TimelineState.Off, r.State);
    True(r.Detail!.Contains("Збори"), "назва періоду");
    Eq(TimelineState.Next, items.First(i => i.At == At(mon, 9, 15)).State);

    s.Enabled = false;
    var off = Timeline.Build(s, mon, new List<JournalEntry>(), At(mon, 9, 0, 30), null);
    True(off.All(i => i.State != TimelineState.Next), "немає «наступного» при вимкненому розкладі");
    True(off.First(i => i.At == At(mon, 8, 30)).Detail!.Contains("вимкнено"), "минуле пояснено вимкненням");
});

Test("Стрічка: вимикач за журналом пояснює пропуск", () =>
{
    var s = Simple(); // зараз увімкнено, але вранці вимикали
    var j = new List<JournalEntry>
    {
        new() { At = At(mon, 8, 0), Kind = JournalKind.ScheduleToggled, Enabled = false, Title = "Розклад вимкнено" },
        new() { At = At(mon, 8, 50), Kind = JournalKind.ScheduleToggled, Enabled = true, Title = "Розклад увімкнено" },
    };
    var items = Timeline.Build(s, mon, j, At(mon, 9, 20), null);
    True(items.First(i => i.At == At(mon, 8, 30)).Detail!.Contains("вимкнено"), "8:30 — вимкнено");
    True(items.First(i => i.At == At(mon, 9, 15)).Detail!.Contains("програма"), "9:15 — інша причина");
});

Test("Стрічка: подія, що звучить, і тривога з рівнем", () =>
{
    var s = Simple();
    var key = $"{At(mon, 9, 0):yyyyMMddHHmm}:Silence";
    var alert = new JournalEntry { At = At(mon, 9, 10), Kind = JournalKind.AlertStarted, Level = AlertLevel.Yellow, Title = "Повітряна тривога" };
    var items = Timeline.Build(s, mon, new List<JournalEntry> { alert }, At(mon, 9, 0, 20), key);
    Eq(TimelineState.Playing, items.First(i => i.Tag == key).State);
    var a = items.First(i => i.Kind == TimelineKind.Alert);
    Eq(AlertLevel.Yellow, a.Level);
});

Test("Журнал дня зберігається і перечитується", () =>
{
    var now = At(mon, 10, 0);
    var j = new DayJournal(now);
    j.Add(new JournalEntry { At = now, Kind = JournalKind.Scheduled, ScheduleKey = "k1", Title = "t" });
    var again = new DayJournal(now.AddMinutes(1));
    True(again.HasScheduled("k1"), "запис є після перезапуску");
    again.EnsureDay(now.AddDays(1));
    Eq(0, again.Entries.Count, "новий день — порожній журнал");
});

// ---------- Профіль «лише тривога», стандартні звуки, тривога під час запуску ----------

Test("Перший запуск: дзвінків і хвилини мовчання немає, неактивності немає", () =>
{
    var s = AppSettings.CreateInitial();
    Eq(0, Schedule.EventsOn(s, mon).Count, "подій у понеділок");
    Eq(0, Schedule.EventsOn(s, sat).Count, "подій у суботу");
    Eq(0, s.InactivityPeriods.Count, "періодів неактивності");
    Eq(null, Schedule.FindInactivePeriod(s, At(sat, 10, 0))?.Name, "субота не глушить тривогу");
});

Test("Без settings.json завантажується профіль «лише тривога»", () =>
{
    var dir = Path.Combine(tmpDir, "fresh-" + Guid.NewGuid().ToString("N"));
    var old = Environment.GetEnvironmentVariable("SCHOOLBELL_DATA");
    Environment.SetEnvironmentVariable("SCHOOLBELL_DATA", dir);
    try
    {
        var s = SettingsStore.Load().Settings;
        Eq(0, s.BellDays.Count, "дні дзвінків");
        True(!s.SilenceEnabled, "хвилина мовчання вимкнена");
    }
    finally { Environment.SetEnvironmentVariable("SCHOOLBELL_DATA", old); }
});

Test("Стандартні звуки підставляються, лише коли свого файлу немає", () =>
{
    var bundled = Path.Combine(tmpDir, "bundled");
    Directory.CreateDirectory(bundled);
    File.WriteAllText(Path.Combine(bundled, DefaultSounds.AirAlertFile), "a");
    File.WriteAllText(Path.Combine(bundled, DefaultSounds.AllClearFile), "c");

    var s = AppSettings.CreateInitial();
    True(DefaultSounds.EnsureDefaults(s, bundled), "перший раз змінено");
    True(File.Exists(s.AirAlertSound!.StoredPath), "копія тривоги в папці програми");
    True(File.Exists(s.AllClearSound!.StoredPath), "копія відбою в папці програми");
    True(!DefaultSounds.EnsureDefaults(s, bundled), "повторно нічого не змінюється");

    File.Delete(s.AirAlertSound.StoredPath);
    True(DefaultSounds.EnsureDefaults(s, bundled), "зниклий файл замінено стандартним");
    True(File.Exists(s.AirAlertSound!.StoredPath), "новий файл є");

    var own = Path.Combine(tmpDir, "own.mp3");
    File.WriteAllText(own, "x");
    s.AllClearSound = SettingsStore.ImportSound(own, SoundKind.AllClear);
    var before = s.AllClearSound.StoredPath;
    DefaultSounds.EnsureDefaults(s, bundled);
    Eq(before, s.AllClearSound!.StoredPath, "свій файл не перезаписується");

    var empty = AppSettings.CreateInitial();
    True(!DefaultSounds.EnsureDefaults(empty, Path.Combine(tmpDir, "no-such-dir")), "без стандартних файлів нічого не робимо");
});

Test("Тривога під час запуску: оголошується, якщо сьогодні ще не оголошували", () =>
{
    JournalEntry E(JournalKind k, int h) => new() { At = At(mon, h, 0), Kind = k, Title = "t" };
    True(OngoingAlertPolicy.ShouldAnnounce(Array.Empty<JournalEntry>()), "порожній журнал (ранок, нічна тривога)");
    True(!OngoingAlertPolicy.ShouldAnnounce(new[] { E(JournalKind.AlertStarted, 9) }), "перезапуск посеред оголошеної тривоги");
    True(OngoingAlertPolicy.ShouldAnnounce(new[] { E(JournalKind.AlertStarted, 9), E(JournalKind.AlertEnded, 10) }), "нова тривога після відбою");
    True(!OngoingAlertPolicy.ShouldAnnounce(new[] { E(JournalKind.AlertEnded, 8), E(JournalKind.AlertStarted, 11), E(JournalKind.AlertLevelChanged, 12) }), "зміна рівня не скидає");
    True(OngoingAlertPolicy.ShouldAnnounce(new[] { E(JournalKind.AlertOngoing, 9) }), "старий запис «триває» без звуку");
});

// ---------- Запуск ----------

int failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"  OK    {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.Message}"); }
}
try { Directory.Delete(tmpDir, true); } catch { }
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} пройдено");
return failed == 0 ? 0 : 1;

sealed class FakeOutput : IAudioOutput
{
    public List<string> Log { get; } = new();
    public event EventHandler? PlaybackEnded;
    public void Play(SoundKind kind, string? path) => Log.Add($"play:{kind}");
    public void Stop() => Log.Add("stop");
    public void End() => PlaybackEnded?.Invoke(this, EventArgs.Empty);
}

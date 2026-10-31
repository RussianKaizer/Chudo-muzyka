// Chudo-muzyka — консольный плеер.
// Берёт песни из папки с программой, ищет текст в интернете (LRCLIB, запасной вариант — lyrics.ovh)
// Проигрывает песню и выводит строки текста синхронно с музыкой, возможна авторская настройка синхронизации текста с песней.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

record LyricLine(double Time, string Text);
record Lyrics(List<LyricLine> Lines, bool Synced, string Source, string? RawLrc = null, string? RawPlain = null);
record Entry(string Artist, string Title, double Duration, string Synced, string Plain);

static class Program
{
    static readonly string[] Extensions = { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".aiff" };
    static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{2})(?:[.:,](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly HttpClient Http = CreateHttp();

    // Сколько секунд ждём ответ от сайта
    const int TimeoutSeconds = 30;

    // Один и тот же исполнитель в базах текстов может называться по-разному
    // Список можно дополнять своими группами.
    static readonly string[][] ArtistAliases =
    {
        new[] { "Коммунизм", "Егор Летов", "Гражданская оборона", "Kommunizm", "Egor Letov", "Grazhdanskaya Oborona", "Letov" },
    };

    // Сайты, где заведомо нет текстов песен: в результатах поиска по сайтам их пропускаем
    static readonly string[] SkipDomains =
    {
        "youtube.", "youtu.be", "spotify.", "apple.com", "music.yandex", "wikipedia.", "discogs.", "last.fm",
        "vk.com", "facebook.", "instagram.", "ok.ru", "shazam.", "deezer.", "soundcloud.", "bandcamp.", "ozon.", "wildberries."
    };

    // Теги делят страницу на блоки чтобы вырезать со страницы блок с текстом песни
    static readonly Regex BlockTag = new(
        @"</?(div|p|ul|ol|li|table|tr|td|th|h[1-6]|section|article|header|footer|nav|form|blockquote|aside|main)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Куда идти после песни: 1 — к следующей, -1 — к предыдущей клавишники n и p
    static int step = 1;

    static HttpClient CreateHttp()
    {
        // автоматическая распаковка gzip если сайт дал сжатую страницу
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ConsoleLyricsPlayer/1.0 (personal project)");
        return client;
    }

    // вход

    static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   //  чтоб читать старые сайты и файлы в windows-1251
        Console.OutputEncoding = Encoding.UTF8;   // чтоб кириллица выводились
        Console.Title = "Chudo-muzyka";
        try { Console.CursorVisible = false; } catch { }

        try
        {
            var songs = CollectSongs(args, out string folder);
            if (songs.Count == 0)
            {
                Warn($"В папке нет песен: {folder}");
                Warn("Положите сюда mp3 / flac / wav / m4a и запустите программу снова.");
                return 1;
            }

            // Теперь в списке всегда все песни папки, а выбранный номер — это только место, откуда начинаем.
            // Раньше после выбора номера в очереди оставалась одна песня, и клавише N переключаться было некуда:
            // очередь кончалась и программа доходила до надписи «Нажмите любую клавишу для выхода».
            int index = 0;
            if (songs.Count > 1)
            {
                Console.WriteLine("Найдены песни:\n");
                for (int i = 0; i < songs.Count; i++)
                    Console.WriteLine($"  {i + 1}. {Path.GetFileName(songs[i])}");

                Console.Write("\nНомер песни, с которой начать (Enter — с первой; дальше песни идут по порядку): ");
                try { Console.CursorVisible = true; } catch { }
                var answer = Console.ReadLine();
                try { Console.CursorVisible = false; } catch { }

                if (int.TryParse(answer, out int n) && n >= 1 && n <= songs.Count)
                    index = n - 1;
            }

            while (index >= 0 && index < songs.Count)
            {
                var song = songs[index];
                step = 1;   // по умолчанию после песни идём к следующей; клавиши N и P
                try
                {
                    if (!await PlayAsync(song, index < songs.Count - 1, index > 0)) break;   // false — бальзак нажал q
                }
                catch (Exception ex)
                {
                    Warn($"Не удалось проиграть «{Path.GetFileName(song)}»: {ex.Message}");
                }
                index += step;
            }
            return 0;
        }
        finally
        {
            Console.ResetColor();
            try { Console.CursorVisible = true; } catch { }
            // Выбрасываем нажатия, накопившиеся в буфере
            try { while (Console.KeyAvailable) Console.ReadKey(true); } catch { }
            Console.WriteLine("\nНажмите любую клавишу для выхода...");
            try { Console.ReadKey(true); } catch { }
        }
    }

    // берёт из папки с программой. Можно также перетащить файл или папку на exe.
    static List<string> CollectSongs(string[] args, out string folder)
    {
        folder = AppContext.BaseDirectory;

        if (args.Length > 0)
        {
            var arg = args[0].Trim('"');
            if (File.Exists(arg))
            {
                var full = Path.GetFullPath(arg);
                folder = Path.GetDirectoryName(full) ?? folder;
                return new List<string> { full };
            }
            if (Directory.Exists(arg)) folder = arg;
        }

        return Directory.EnumerateFiles(folder)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                return Array.Exists(Extensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // воспроизведение

    // Возвращает false, если пользователь попросил выйти
    static async Task<bool> PlayAsync(string path, bool hasNext, bool hasPrev)
    {
        using var audio = new AudioFileReader(path);
        double duration = audio.TotalTime.TotalSeconds;
        var (artist, title, album) = ReadMeta(path);

        string who = artist.Length > 0 ? artist + " — " : "";
        string length = audio.TotalTime.ToString(@"m\:ss");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"♪ {who}{title}  [{length}]");
        Console.ResetColor();

        Console.WriteLine("Ищу текст песни в интернете...");
        var lyrics = await FindLyricsAsync(path, artist, title, album, duration);

        if (lyrics == null)
        {
            Warn("Текст не найден — просто играю музыку.");
            Hint("Нажмите L во время песни, чтобы указать текст вручную (другое имя исполнителя, ссылку или файл).");   // (добавлено)
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Текст найден: {lyrics.Source}, строк: {lyrics.Lines.Count}");
            if (!lyrics.Synced)
                Console.WriteLine("У этого текста нет таймкодов — время строк рассчитано приблизительно.");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Пробел — пауза | N — следующая | P — предыдущая | Q — выход | +/- — сдвиг текста на 0.2 с");
        Console.WriteLine("L — найти текст вручную (другое имя исполнителя, ссылка на страницу или файл .txt)");
        Console.WriteLine(new string('-', 70));
        Console.ResetColor();

        var lines = lyrics?.Lines ?? new List<LyricLine>();

        using var output = new WaveOutEvent { DesiredLatency = 150 };
        output.Init(audio);
        output.Play();

        int next = 0;          // индекс следующей строки, которую ещё не показали
        double offset = 0;     // ручная поправка синхрона
        bool quit = false;

        while (output.PlaybackState != PlaybackState.Stopped)
        {
            while (Console.KeyAvailable && output.PlaybackState != PlaybackState.Stopped)   // после остановки лишние нажатия не разбираем
            {
                switch (Console.ReadKey(true).Key)
                {
                    case ConsoleKey.Spacebar:
                        if (output.PlaybackState == PlaybackState.Playing) output.Pause();
                        else output.Play();
                        break;

                    case ConsoleKey.N:
                    case ConsoleKey.RightArrow:
                        // если следующей  нет, программа не закрывается, а просто сообщает об этом
                        if (hasNext) { step = 1; output.Stop(); }
                        else Hint("Это последняя песня в списке, дальше переключаться некуда.");
                        break;

                    case ConsoleKey.P:
                    case ConsoleKey.LeftArrow:
                        //  предыдущая
                        if (hasPrev) { step = -1; output.Stop(); }
                        else Hint("Это первая песня в списке.");
                        break;

                    case ConsoleKey.L:
                    {
                        // ручной поиск текста пока вы вводите запрос музыка стоит
                        bool wasPlaying = output.PlaybackState == PlaybackState.Playing;
                        if (wasPlaying) output.Pause();

                        var manual = await AskManualAsync(path, artist, album, duration);
                        if (manual != null)
                        {
                            lines = manual.Lines;
                            double pos = (double)output.GetPosition() / output.OutputWaveFormat.AverageBytesPerSecond + offset;
                            next = lines.FindIndex(l => l.Time > pos);   // строки которые уже прозвучали не выводим
                            if (next < 0) next = lines.Count;
                        }

                        if (wasPlaying) output.Play();
                        break;
                    }

                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        quit = true;
                        output.Stop();
                        break;

                    case ConsoleKey.OemPlus:
                    case ConsoleKey.Add:
                        offset += 0.2;
                        Hint($"сдвиг текста: {offset:+0.0;-0.0;0.0} с");
                        break;

                    case ConsoleKey.OemMinus:
                    case ConsoleKey.Subtract:
                        offset -= 0.2;
                        Hint($"сдвиг текста: {offset:+0.0;-0.0;0.0} с");
                        break;
                }
            }

            if (output.PlaybackState == PlaybackState.Stopped) break;

            // реальна позиция воспроизведения то что слышно сейчас
            double now = (double)output.GetPosition() / output.OutputWaveFormat.AverageBytesPerSecond + offset;

            while (next < lines.Count && lines[next].Time <= now)
                PrintLine(lines[next++]);

            await Task.Delay(20);
        }

        if (!quit) Hint("— конец песни —");
        return !quit;
    }

    static void PrintLine(LyricLine line)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(TimeSpan.FromSeconds(line.Time).ToString(@"mm\:ss") + "  ");

        if (string.IsNullOrWhiteSpace(line.Text))
        {
            Console.WriteLine("♪");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(line.Text);
        }
        Console.ResetColor();
    }

    // теги и имя

    static (string Artist, string Title, string Album) ReadMeta(string path)
    {
        string artist = "", title = "", album = "";
        try
        {
            using var f = TagLib.File.Create(path);
            artist = First(f.Tag.FirstPerformer, f.Tag.FirstAlbumArtist);
            title = First(f.Tag.Title);
            album = First(f.Tag.Album);
        }
        catch { /* тегов нет — возьмём всё из имени файла */ }

        // Имя файла вида 01 - Исполнитель - Название.mp3 или Исполнитель - Название.mp3
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(path), @"^\d{1,3}\s*[.\-_)]\s*", "");
        name = name.Replace('_', ' ').Trim();   // (добавлено) «Исполнитель_-_Название» тоже понимаем

        // Делим по " - " номера треков и годы (06, 1987) выбрасываем название — последняя часть
        // исполнитель — первая Так понимаются и имена вида «Исполнитель - Альбом - 06 - Название»
        var parts = Regex.Split(name, @"\s+[-–—]\s+")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && !Regex.IsMatch(p, @"^\d{1,4}$"))
            .ToList();

        if (artist.Length == 0 && parts.Count >= 2) artist = parts[0];
        if (title.Length == 0) title = parts.Count >= 1 ? parts[^1] : name;

        // В тегах название иногда записано как «Исполнитель - Название»
        if (artist.Length > 0 && title.StartsWith(artist + " - ", StringComparison.OrdinalIgnoreCase))
            title = title.Substring(artist.Length + 3);

        return (artist.Trim(), title.Trim(), album.Trim());
    }

    static string First(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // парсинг текста и поиск

    static async Task<Lyrics?> FindLyricsAsync(string path, string artist, string title, string album, double duration, bool useLocal = true)
    {
        // 1 Уже скачанный ранее файл .lrc рядом с песней
        string lrcPath = Path.ChangeExtension(path, ".lrc");
        string txtPath = Path.ChangeExtension(path, ".txt");
        if (useLocal && File.Exists(lrcPath))   // useLocal = false при ручном поиске: сохранённые файлы пропускаем
        {
            var local = ParseLrc(File.ReadAllText(lrcPath, Encoding.UTF8));
            if (local.Count > 0)
                return new Lyrics(local, true, "файл " + Path.GetFileName(lrcPath) + " (с таймкодами)");
        }

        // 1 Обычный текст без таймкодов в файле .txt рядом с песней
        if (useLocal && File.Exists(txtPath))
        {
            var fromTxt = FromRawText(ReadTextFile(txtPath), duration, "файл " + Path.GetFileName(txtPath));
            if (fromTxt != null) return fromTxt;
        }

        Lyrics? fallback = null;

        // 2 LRCLIB  база текстов с таймкодами
        Hint("  ищу в LRCLIB...");   // (добавлено)
        try
        {
            var found = await FromLrclibAsync(artist, title, album, duration);
            if (found is { Synced: true })
            {
                TrySave(lrcPath, found.RawLrc);   // кэшируем,
                return found;
            }
            fallback = found;
        }
        catch (Exception ex)
        {
            Warn($"LRCLIB недоступен: {Explain(ex)}");
        }

        // 3 Запасной вариант - обычный текст без таймкодов
        if (fallback == null)
        {
            // Genius, lyrics.ovh и поиск по сайтам идут одновременно
            Hint("  ищу обычный текст: Genius, lyrics.ovh, поиск по сайтам...");
            var genius = SafeAsync("Genius", () => FromGeniusAsync(artist, title, duration));
            var ovh = artist.Length > 0
                ? SafeAsync("lyrics.ovh", () => FromLyricsOvhAnyAsync(artist, title, duration))
                : Task.FromResult<Lyrics?>(null);
            var web = SafeAsync("Поиск по сайтам", () => FromWebSearchAsync(artist, title, duration));

            await Task.WhenAll(genius, ovh, web);
            fallback = genius.Result ?? ovh.Result ?? web.Result;
        }

        // Обычный текст тоже запоминаем рядом с песней чтобы в следующий раз не искать заново.
        // Если там оказался не тот текст, удалите файл .txt или нажмите L во время песни.
        if (fallback is { Synced: false })
            TrySave(txtPath, fallback.RawPlain);

        return fallback;
    }

    static async Task<Lyrics?> FromLrclibAsync(string artist, string title, string album, double duration)
    {
        string clean = CleanTitle(title);
        int dur = (int)Math.Round(duration);
        string q = (artist + " " + clean).Trim();
        var urls = new List<string>();

        // Точный запрос по документации четыре параметра
        if (artist.Length > 0 && album.Length > 0)
            urls.Add($"https://lrclib.net/api/get?artist_name={U(artist)}&track_name={U(title)}&album_name={U(album)}&duration={dur}");

        // поиск по исполнителю и названию затем просто по строке
        if (artist.Length > 0)
        {
            urls.Add($"https://lrclib.net/api/search?track_name={U(title)}&artist_name={U(artist)}");
            if (clean != title)
                urls.Add($"https://lrclib.net/api/search?track_name={U(clean)}&artist_name={U(artist)}");
        }
        urls.Add($"https://lrclib.net/api/search?q={U(q)}");

        // Другие имена того же исполнителя
        foreach (var alt in AltArtists(artist).Take(4))
            urls.Add($"https://lrclib.net/api/search?track_name={U(clean)}&artist_name={U(alt)}");

        // Поиск только по названию: вдруг в базе исполнитель записан совсем иначе
        // подходящую запись потом выбирает Pick — по названию, исполнителю и длительности
        urls.Add($"https://lrclib.net/api/search?track_name={U(clean)}");
        //  Все запросы идут одновременно
        // Если часть запросов упала по таймауту, остальные всё равно используются.
        var results = await Task.WhenAll(urls.Distinct().Select(QueryLrclibSafeAsync));
        var entries = results.SelectMany(r => r.Entries).ToList();
        if (entries.Count == 0 && results.All(r => r.Error != null))
            throw results[0].Error!;   // ни один запрос не прошёл: сообщим

        return Pick(entries, artist, title, clean, duration);
    }

    // Запрос к LRCLIB, который не бросает исключение, а возвращает ошибку вместе с результатом
    static async Task<(List<Entry> Entries, Exception? Error)> QueryLrclibSafeAsync(string url)
    {
        try { return (await QueryLrclibAsync(url), null); }
        catch (Exception ex) { return (new List<Entry>(), ex); }
    }

    static async Task<List<Entry>> QueryLrclibAsync(string url)
    {
        var list = new List<Entry>();
        using var resp = await Http.GetAsync(url);
        if ((int)resp.StatusCode is 404 or 400) return list;   // 404 — не найдено
        resp.EnsureSuccessStatusCode();   // 429 и 5xx —  ошибка сервера

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var e in root.EnumerateArray()) list.Add(ToEntry(e));
        else if (root.ValueKind == JsonValueKind.Object)
            list.Add(ToEntry(root));

        return list;
    }
    
    static Entry ToEntry(JsonElement e)
    {
        double duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble() : 0;
        return new Entry(Str(e, "artistName"), Str(e, "trackName"), duration, Str(e, "syncedLyrics"), Str(e, "plainLyrics"));
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // выбираем подходящую запись: название должно совпадать
    // ещё длительность, иначе это скорее всего другая версия песни и тайминг уедет
    static Lyrics? Pick(List<Entry> entries, string artist, string title, string clean, double duration)
    {
        string a = Norm(title), b = Norm(clean);

        bool SameTitle(Entry e)
        {
            var n = Norm(e.Title);
            var nc = Norm(CleanTitle(e.Title));   // то же название без скобок: «Песня (live)» -> «Песня»
            return TitlesMatch(n, a) || TitlesMatch(nc, a) || (b.Length > 0 && (TitlesMatch(n, b) || TitlesMatch(nc, b)));
        }

        var candidates = entries.Where(SameTitle).ToList();

        // Текст с таймкодами берём, если длина почти совпадает (до 4 с) либо исполнитель тот же
        // (или его псевдоним) и длина отличается не больше чем на 15 с
        var synced = candidates
            .Where(e => e.Synced.Length > 0 &&
                        (Math.Abs(e.Duration - duration) <= 4 ||
                         (SameArtist(e.Artist, artist) && Math.Abs(e.Duration - duration) <= 15)))
            .OrderBy(e => Math.Abs(e.Duration - duration))
            .FirstOrDefault();

        if (synced != null)
        {
            var lines = ParseLrc(synced.Synced);
            if (lines.Count > 0)
            {
                double diff = Math.Abs(synced.Duration - duration);
                string note = diff > 3 ? $", длина записи отличается на {diff:0} с — подстройте текст клавишами +/-" : "";
                return new Lyrics(lines, true, "LRCLIB (с таймкодами" + note + ")", synced.Synced);
            }
        }

        // Обычный текст берём только у того же исполнителя, чтобы не подставить одноимённую чужую
        var plain = candidates
            .Where(e => e.Plain.Length > 0 && SameArtist(e.Artist, artist))
            .OrderBy(e => Math.Abs(e.Duration - duration))
            .FirstOrDefault();

        if (plain != null)
        {
            var lines = Estimate(plain.Plain, duration);
            if (lines.Count > 0)
                return new Lyrics(lines, false, "LRCLIB (без таймкодов)", null, plain.Plain);
        }

        return null;
    }

    static async Task<Lyrics?> FromLyricsOvhAsync(string artist, string title, double duration)
    {
        var url = $"https://api.lyrics.ovh/v1/{U(artist)}/{U(CleanTitle(title))}";
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
        if (!doc.RootElement.TryGetProperty("lyrics", out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        var text = el.GetString() ?? "";
        text = Regex.Replace(text, @"^Paroles de la chanson.*?\r?\n", "", RegexOptions.IgnoreCase);

        var lines = Estimate(text, duration);
        return lines.Count == 0 ? null : new Lyrics(lines, false, "lyrics.ovh (без таймкодов)", null, text);
    }

    // разбор, тайминг

    // Формат LRC: [мм:сс.xx] Текст строки 
    static List<LyricLine> ParseLrc(string lrc)
    {
        var result = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            var tags = TimeTag.Matches(line);
            if (tags.Count == 0) continue;   // служебные строки ar:..., ti:... пропускаем

            var text = TimeTag.Replace(line, "").Trim();
            foreach (Match m in tags)
            {
                double sec = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
                if (m.Groups[3].Success)
                    sec += double.Parse("0." + m.Groups[3].Value, CultureInfo.InvariantCulture);
                result.Add(new LyricLine(sec, text));
            }
        }
        return result.OrderBy(l => l.Time).ToList();
    }

    // Если таймкодов нет -  размазываем строки
    // с учётом длины строки и пропуская начало и конец приближенние
    static List<LyricLine> Estimate(string plain, double duration)
    {
        var texts = plain.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !Regex.IsMatch(l, @"^\[[^\]]*\]$"))   // убираем пометки вроде припева
            .ToList();

        var result = new List<LyricLine>();
        if (texts.Count == 0) return result;

        double start = duration * 0.06;
        double span = duration * 0.88;
        double total = texts.Sum(l => l.Length + 10.0);

        double t = start;
        foreach (var l in texts)
        {
            result.Add(new LyricLine(t, l));
            t += span * (l.Length + 10.0) / total;
        }
        return result;
    }

    // помощники

    static string CleanTitle(string title)
    {
        var clean = Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
        return clean.Length > 0 ? clean : title;
    }

    static string Norm(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]", "").Replace('ё', 'е');

    static string U(string s) => Uri.EscapeDataString(s);

    static void TrySave(string lrcPath, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        try { File.WriteAllText(lrcPath, raw, new UTF8Encoding(false)); } catch { }
    }

    static void Warn(string text)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    static void Hint(string text)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    // ниже сравнение названий и исполнителей, ручной поиск,  источники текстов

    // Названия считаем одинаковыми, если они совпадают или одно целиком входит в другое,
    static bool TitlesMatch(string x, string y)
    {
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        return shorter.Length >= 3 && longer.Contains(shorter) && shorter.Length * 10 >= longer.Length * 6;
    }

    // Другие имена того же исполнителя из группы ArtistAliases
    static IEnumerable<string> AltArtists(string artist)
    {
        string na = Norm(artist);
        if (na.Length < 3) return Array.Empty<string>();

        foreach (var group in ArtistAliases)
            if (InGroup(na, group))
                return group.Where(g => Norm(g) != na).ToList();

        return Array.Empty<string>();
    }

    static bool InGroup(string norm, string[] group) =>
        group.Any(g =>
        {
            var ng = Norm(g);
            return ng.Length > 0 && (norm.Contains(ng) || (norm.Length >= 4 && ng.Contains(norm)));
        });

    // Тот ли это исполнитель имена похожи или оба входят в одну группу псевдонимов
    static bool SameArtist(string a, string b)
    {
        string na = Norm(a), nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return true;   // исполнитель неизвестен — не несём херню
        if (na.Contains(nb) || nb.Contains(na)) return true;
        return ArtistAliases.Any(group => InGroup(na, group) && InGroup(nb, group));
    }

    // Понятное сообщение об ошибке: таймаут по-русски
    static string Explain(Exception ex) =>
        (ex is TaskCanceledException or TimeoutException)
            ? $"превышено время ожидания ответа ({TimeoutSeconds} с)"
            : ex.Message;

    // Запускает источник текста; если он упал, пишет предупреждение и возвращает null, не мешая остальным
    static async Task<Lyrics?> SafeAsync(string name, Func<Task<Lyrics?>> source)
    {
        try { return await source(); }
        catch (Exception ex)
        {
            Warn($"{name} недоступен: {Explain(ex)}");
            return null;
        }
    }

    // Ручной поиск текста (клавиша L во время песни).
    // Можно ввести «Исполнитель - Название», ссылку на страницу с текстом или путь к файлу .txt / .lrc.
    static async Task<Lyrics?> AskManualAsync(string path, string artist, string album, double duration)
    {
        if (Console.IsInputRedirected) return null;

        Console.WriteLine();
        Warn("Ручной поиск текста. Введите одно из:");
        Hint("  Исполнитель - Название   (поиск под другим именем исполнителя)");
        Hint("  ссылку на страницу с текстом (http...)");
        Hint("  путь к файлу .txt или .lrc");
        Hint("  или просто Enter — отмена");
        Console.Write("> ");

        try { Console.CursorVisible = true; } catch { }
        string input = (Console.ReadLine() ?? "").Trim().Trim('"');
        try { Console.CursorVisible = false; } catch { }
        if (input.Length == 0) return null;

        try
        {
            Lyrics? found;
            if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                found = await FromUrlAsync(input, duration);
            }
            else if (File.Exists(input))
            {
                string raw = ReadTextFile(input);
                var synced = ParseLrc(raw);
                found = synced.Count > 0
                    ? new Lyrics(synced, true, "файл " + Path.GetFileName(input) + " (с таймкодами)", raw)
                    : FromRawText(raw, duration, "файл " + Path.GetFileName(input));
            }
            else
            {
                var (a, t) = SplitArtistTitle(input);
                found = await FindLyricsAsync(path, a.Length > 0 ? a : artist, t, album, duration, useLocal: false);
            }

            if (found == null)
            {
                Warn("По этому запросу ничего не нашлось. Можно нажать L и попробовать ещё раз.");
                return null;
            }

            // запоминаем рядом с песней, чтобы в следующий раз не искать
            if (found.Synced) TrySave(Path.ChangeExtension(path, ".lrc"), found.RawLrc);
            else TrySave(Path.ChangeExtension(path, ".txt"), found.RawPlain);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Текст найден: {found.Source}, строк: {found.Lines.Count}");
            Console.ResetColor();
            return found;
        }
        catch (Exception ex)
        {
            Warn($"Не получилось: {Explain(ex)}");
            return null;
        }
    }

    static (string Artist, string Title) SplitArtistTitle(string input)
    {
        var parts = Regex.Split(input, @"\s+[-–—]\s+");
        return parts.Length >= 2
            ? (parts[0].Trim(), string.Join(" - ", parts.Skip(1)).Trim())
            : ("", input.Trim());
    }

    // Обычный текст без таймкодов -> строки с приблизительным временем
    static Lyrics? FromRawText(string raw, double duration, string source)
    {
        var lines = Estimate(raw, duration);
        return lines.Count == 0 ? null : new Lyrics(lines, false, source + " (без таймкодов)", null, raw);
    }

    // Текст со страницы, ссылку на которую ввёл пользователь
    static async Task<Lyrics?> FromUrlAsync(string url, double duration)
    {
        string html = await GetPageAsync(url);
        string? text = ExtractLyricsFromHtml(html);
        if (text == null) return null;
        return FromRawText(text, duration, "страница " + new Uri(url).Host);
    }

    // Читает текстовый файл: сначала как UTF-8, а если не получилось, то как windows-1251
    static string ReadTextFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = new UTF8Encoding(false, false).GetString(bytes);   // неверные байты не вызывают ошибку, а заменяются спецсимволом
        if (text.Contains('\uFFFD'))
            text = Encoding.GetEncoding(1251).GetString(bytes);
        return text.TrimStart('\uFEFF');
    }

    // Скачивание страницы как обычный браузер часть сайтов не отдаёт страницы программам
    static async Task<string> GetPageAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");

        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await ReadHtmlAsync(resp);
    }

    static async Task<string?> TryGetPageAsync(string url)
    {
        try { return await GetPageAsync(url); }
        catch { return null; }
    }

    //  Читает страницу в правильной кодировке из заголовка ответа или из тега meta
    static async Task<string> ReadHtmlAsync(HttpResponseMessage resp)
    {
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
        string? charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"', '\'', ' ');

        if (string.IsNullOrEmpty(charset))
        {
            // кодировку часто указывают только в теге <meta> в начале страницы
            string head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var m = Regex.Match(head, @"charset\s*=\s*[""']?\s*([\w\-]+)", RegexOptions.IgnoreCase);
            if (m.Success) charset = m.Groups[1].Value;
        }

        Encoding enc = Encoding.UTF8;
        if (!string.IsNullOrEmpty(charset))
        {
            try { enc = Encoding.GetEncoding(charset); } catch { }
        }
        return enc.GetString(bytes);
    }

    // Genius большая база текстов
    // Используется публичный поиск сайта если Genius не пускает программу об этом будет написано
    static async Task<Lyrics?> FromGeniusAsync(string artist, string title, double duration)
    {
        string clean = CleanTitle(title);
        string a = Norm(title), b = Norm(clean);
        var queries = new List<string> { (artist + " " + clean).Trim() };
        foreach (var alt in AltArtists(artist).Take(2))
            queries.Add((alt + " " + clean).Trim());

        foreach (var query in queries.Distinct())
        {
            string json = await GetPageAsync($"https://genius.com/api/search/multi?per_page=10&q={U(query)}");
            using var doc = JsonDocument.Parse(json);

            foreach (var hit in GeniusHits(doc.RootElement))
            {
                if (hit.Url.Length == 0) continue;

                string n = Norm(hit.Title), nc = Norm(CleanTitle(hit.Title));
                bool sameTitle = TitlesMatch(n, a) || TitlesMatch(nc, a) || (b.Length > 0 && (TitlesMatch(n, b) || TitlesMatch(nc, b)));
                if (!sameTitle || !SameArtist(hit.Artist, artist)) continue;

                string html = await GetPageAsync(hit.Url);
                string? text = ExtractGeniusLyrics(html);
                if (text == null) continue;

                var lines = Estimate(text, duration);
                if (lines.Count > 0)
                    return new Lyrics(lines, false, "Genius (без таймкодов)", null, text);
            }
        }
        return null;
    }

    static IEnumerable<(string Url, string Title, string Artist)> GeniusHits(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("sections", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;

            foreach (var hit in hits.EnumerateArray())
            {
                if (Str(hit, "type") != "song") continue;
                if (!hit.TryGetProperty("result", out var result)) continue;

                string artistName = result.TryGetProperty("primary_artist", out var pa) ? Str(pa, "name") : "";
                yield return (Str(result, "url"), Str(result, "title"), artistName);
            }
        }
    }

    // На страницах Genius текст лежит в блоках data-lyrics-container > превращаем в обычный текст
    static string? ExtractGeniusLyrics(string html)
    {
        var sb = new StringBuilder();
        int pos = 0;
        while (true)
        {
            int marker = html.IndexOf("data-lyrics-container=\"true\"", pos, StringComparison.Ordinal);
            if (marker < 0) break;

            int open = html.LastIndexOf("<div", marker, StringComparison.OrdinalIgnoreCase);
            if (open < 0 || html.IndexOf('>', open) < marker) { pos = marker + 1; continue; }   // метка не внутри тега div

            int end = FindDivEnd(html, open);
            if (end < 0) break;

            sb.Append(HtmlToText(RemoveExcludedBlocks(html.Substring(open, end - open)))).Append('\n');
            pos = end;
        }

        if (sb.Length == 0) return null;

        string text = sb.ToString();
        text = Regex.Replace(text, @"^\s*(You might also like|Вам также может понравиться)\s*$", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        text = Regex.Replace(text, @"\d*Embed\s*$", "", RegexOptions.IgnoreCase);
        text = text.Trim();
        return text.Length > 0 ? text : null;
    }

    // Genius кладёт внутрь блока с текстом служебные куски > вырезаем 
    static string RemoveExcludedBlocks(string html)
    {
        while (true)
        {
            int marker = html.IndexOf("data-exclude-from-selection=\"true\"", StringComparison.Ordinal);
            if (marker < 0) return html;

            int open = html.LastIndexOf("<div", marker, StringComparison.OrdinalIgnoreCase);
            if (open < 0 || html.IndexOf('>', open) < marker) return html;

            int end = FindDivEnd(html, open);
            if (end < 0) return html;
            html = html.Remove(open, end - open);
        }
    }

    //  Находит конец блока <div>, начинающегося в позиции open
    static int FindDivEnd(string html, int open)
    {
        int depth = 0, i = open;
        while (i < html.Length)
        {
            int nextOpen = html.IndexOf("<div", i, StringComparison.OrdinalIgnoreCase);
            int nextClose = html.IndexOf("</div", i, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0) return -1;

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                i = nextOpen + 4;
            }
            else
            {
                depth--;
                int gt = html.IndexOf('>', nextClose);
                i = gt < 0 ? html.Length : gt + 1;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    // lyrics.ovh: пробуем имя исполнителя и два его псевдонима одновременно
    static async Task<Lyrics?> FromLyricsOvhAnyAsync(string artist, string title, double duration)
    {
        var names = new[] { artist }.Concat(AltArtists(artist)).Take(3).ToList();

        async Task<Lyrics?> Try(string name)
        {
            try { return await FromLyricsOvhAsync(name, title, duration); }
            catch { return null; }
        }

        var results = await Task.WhenAll(names.Select(Try));
        return results.FirstOrDefault(r => r != null);
    }

    // Поиск по сайтам спрашиваем поисковик «исполнитель название текст песни»
    // подходящие страницы и вырезаем с них блок с текстом. Страница принимается, только если на ней есть и название
    // песни, и исполнитель.
    static async Task<Lyrics?> FromWebSearchAsync(string artist, string title, double duration)
    {
        string clean = CleanTitle(title);
        string query = $"{artist} {clean} текст песни".Trim();
        string results = await GetPageAsync($"https://html.duckduckgo.com/html/?q={U(query)}");

        var urls = new List<string>();
        foreach (Match tag in Regex.Matches(results, @"<a\b[^>]*\bresult__a\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var href = Regex.Match(tag.Value, @"href=""([^""]+)""");
            if (!href.Success) continue;

            string url = System.Net.WebUtility.HtmlDecode(href.Groups[1].Value);
            var redirect = Regex.Match(url, @"[?&]uddg=([^&]+)");
            if (redirect.Success) url = Uri.UnescapeDataString(redirect.Groups[1].Value);
            else if (url.StartsWith("//")) url = "https:" + url;

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (SkipDomains.Any(d => url.Contains(d, StringComparison.OrdinalIgnoreCase))) continue;
            if (!urls.Contains(url)) urls.Add(url);
            if (urls.Count >= 5) break;
        }
        if (urls.Count == 0) return null;

        var pages = await Task.WhenAll(urls.Select(TryGetPageAsync));

        string needTitle = Norm(clean);
        var needArtists = new[] { artist }.Concat(AltArtists(artist)).Select(Norm).Where(x => x.Length > 0).ToList();

        for (int i = 0; i < urls.Count; i++)
        {
            string? html = pages[i];
            if (html == null) continue;

            string pageNorm = Norm(HtmlToText(html));
            if (!pageNorm.Contains(needTitle)) continue;
            if (needArtists.Count > 0 && !needArtists.Any(x => pageNorm.Contains(x))) continue;

            string? text = ExtractLyricsFromHtml(html);
            if (text == null) continue;

            var lines = Estimate(text, duration);
            if (lines.Count >= 8)
                return new Lyrics(lines, false, "сайт " + new Uri(urls[i]).Host + " (без таймкодов)");   // эти тексты не кэшируем
        }
        return null;
    }

    // Вырезает текст песни из произвольной страницы.
    // где строки разделены тегами <br>: ищем самую длинную цепочку таких коротких строк.
    static string? ExtractLyricsFromHtml(string html)
    {
        var genius = ExtractGeniusLyrics(html);
        if (genius != null) return genius;

        html = Regex.Replace(html, @"<(script|style|noscript)\b.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<pre\b.*?</pre>", m => m.Value.Replace("\n", "<br>"), RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"\s+", " ");

        var chunks = Regex.Split(html, @"<br\s*/?>", RegexOptions.IgnoreCase);
        var texts = chunks.Select(PlainText).ToArray();

        int bestStart = -1, bestEnd = -1, bestScore = 0, runStart = -1, score = 0;
        for (int i = 0; i <= chunks.Length; i++)
        {
            bool ok = i < chunks.Length && !BlockTag.IsMatch(chunks[i]) && texts[i].Length <= 120;
            if (ok)
            {
                if (runStart < 0) { runStart = i; score = 0; }
                if (texts[i].Length > 0) score++;
            }
            else if (runStart >= 0)
            {
                if (score > bestScore) { bestScore = score; bestStart = runStart; bestEnd = i - 1; }
                runStart = -1;
            }
        }
        if (bestScore < 8) return null;   // слишком короткая цепочка не то

        var lines = new List<string>();
        if (bestStart > 0)
        {
            var first = AfterLastBlock(chunks[bestStart - 1]);   // первая строка может слипнуться с открывающим тегом
            if (first.Length is > 0 and <= 120) lines.Add(first);
        }
        for (int i = bestStart; i <= bestEnd; i++) lines.Add(texts[i]);
        if (bestEnd + 1 < chunks.Length)
        {
            var last = BeforeFirstBlock(chunks[bestEnd + 1]);   // а последняя — с закрывающим
            if (last.Length is > 0 and <= 120) lines.Add(last);
        }

        var result = string.Join("\n", lines).Trim();
        return result.Length > 0 ? result : null;
    }

    static string AfterLastBlock(string chunk)
    {
        var ms = BlockTag.Matches(chunk);
        if (ms.Count == 0) return PlainText(chunk);
        int gt = chunk.IndexOf('>', ms[ms.Count - 1].Index);
        return gt < 0 ? "" : PlainText(chunk.Substring(gt + 1));
    }

    static string BeforeFirstBlock(string chunk)
    {
        var m = BlockTag.Match(chunk);
        return m.Success ? PlainText(chunk.Substring(0, m.Index)) : PlainText(chunk);
    }

    // HTML -> обычный текст, где <br> и концы блоков становятся переводами строк
    static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"<(script|style|noscript)\b.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</(p|div|li|tr|h[1-6]|blockquote)\s*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(html).Replace('\u00A0', ' ');
    }

    static string PlainText(string fragment) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(fragment, @"<[^>]+>", "")).Replace('\u00A0', ' ').Trim();
}

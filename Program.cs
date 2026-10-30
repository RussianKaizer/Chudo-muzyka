// Chudo-muzyka — консольный плеер.
// Берёт песни из папки с программой, ищет текст в интернете (LRCLIB, запасной вариант — lyrics.ovh)
// Проигрывает песню и выводит строки текста синхронно с музыкой, возможна авторская настройка синхронизации текста с песней.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

record LyricLine(double Time, string Text);
record Lyrics(List<LyricLine> Lines, bool Synced, string Source, string? RawLrc = null);
record Entry(string Artist, string Title, double Duration, string Synced, string Plain);

static class Program
{
    static readonly string[] Extensions = { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".aiff" };
    static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{2})(?:[.:,](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ConsoleLyricsPlayer/1.0 (personal project)");
        return client;
    }

    // вход

    static async Task<int> Main(string[] args)
    {
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

            var queue = songs;
            if (songs.Count > 1)
            {
                Console.WriteLine("Найдены песни:\n");
                for (int i = 0; i < songs.Count; i++)
                    Console.WriteLine($"  {i + 1}. {Path.GetFileName(songs[i])}");

                Console.Write("\nНомер песни (или Enter — играть все по порядку): ");
                try { Console.CursorVisible = true; } catch { }
                var answer = Console.ReadLine();
                try { Console.CursorVisible = false; } catch { }

                if (int.TryParse(answer, out int n) && n >= 1 && n <= songs.Count)
                    queue = new List<string> { songs[n - 1] };
            }

            foreach (var song in queue)
            {
                try
                {
                    if (!await PlayAsync(song)) break;   // false — бальзак нажал q
                }
                catch (Exception ex)
                {
                    Warn($"Не удалось проиграть «{Path.GetFileName(song)}»: {ex.Message}");
                }
            }
            return 0;
        }
        finally
        {
            Console.ResetColor();
            try { Console.CursorVisible = true; } catch { }
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
    static async Task<bool> PlayAsync(string path)
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
        Console.WriteLine("Пробел — пауза | N — следующая | Q — выход | +/- — сдвиг текста на 0.2 с");
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
            while (Console.KeyAvailable)
            {
                switch (Console.ReadKey(true).Key)
                {
                    case ConsoleKey.Spacebar:
                        if (output.PlaybackState == PlaybackState.Playing) output.Pause();
                        else output.Play();
                        break;

                    case ConsoleKey.N:
                    case ConsoleKey.RightArrow:
                        output.Stop();
                        break;

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
        var m = Regex.Match(name, @"^(.+?)\s+[-–—]\s+(.+)$");

        if (artist.Length == 0 && m.Success) artist = m.Groups[1].Value;
        if (title.Length == 0) title = m.Success ? m.Groups[2].Value : name;

        return (artist.Trim(), title.Trim(), album.Trim());
    }

    static string First(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // парсинг текста и поиск

    static async Task<Lyrics?> FindLyricsAsync(string path, string artist, string title, string album, double duration)
    {
        // 1 Уже скачанный ранее файл .lrc рядом с песней
        string lrcPath = Path.ChangeExtension(path, ".lrc");
        if (File.Exists(lrcPath))
        {
            var local = ParseLrc(File.ReadAllText(lrcPath, Encoding.UTF8));
            if (local.Count > 0)
                return new Lyrics(local, true, "файл " + Path.GetFileName(lrcPath) + " (с таймкодами)");
        }

        Lyrics? fallback = null;

        // 2 LRCLIB  база текстов с таймкодами
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
            Warn($"LRCLIB недоступен: {ex.Message}");
        }

        // 3 Запасной вариант - обычный текст без таймкодов
        if (fallback == null && artist.Length > 0)
        {
            try { fallback = await FromLyricsOvhAsync(artist, title, duration); }
            catch (Exception ex) { Warn($"lyrics.ovh недоступен: {ex.Message}"); }
        }

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

        Lyrics? plainFallback = null;
        foreach (var url in urls)
        {
            var entries = await QueryLrclibAsync(url);
            var result = Pick(entries, title, clean, duration);
            if (result == null) continue;
            if (result.Synced) return result;
            plainFallback ??= result;
        }
        return plainFallback;
    }

    static async Task<List<Entry>> QueryLrclibAsync(string url)
    {
        var list = new List<Entry>();
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return list;   // 404 — не найдено

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
    static Lyrics? Pick(List<Entry> entries, string title, string clean, double duration)
    {
        string a = Norm(title), b = Norm(clean);

        bool SameTitle(Entry e)
        {
            var n = Norm(e.Title);
            return n.Length > 0 && (n.Contains(a) || a.Contains(n) || (b.Length > 0 && n.Contains(b)));
        }

        var candidates = entries.Where(SameTitle).ToList();

        var synced = candidates
            .Where(e => e.Synced.Length > 0 && Math.Abs(e.Duration - duration) <= 10)
            .OrderBy(e => Math.Abs(e.Duration - duration))
            .FirstOrDefault();

        if (synced != null)
        {
            var lines = ParseLrc(synced.Synced);
            if (lines.Count > 0)
                return new Lyrics(lines, true, "LRCLIB (с таймкодами)", synced.Synced);
        }

        var plain = candidates
            .Where(e => e.Plain.Length > 0)
            .OrderBy(e => Math.Abs(e.Duration - duration))
            .FirstOrDefault();

        if (plain != null)
        {
            var lines = Estimate(plain.Plain, duration);
            if (lines.Count > 0)
                return new Lyrics(lines, false, "LRCLIB (без таймкодов)");
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
        return lines.Count == 0 ? null : new Lyrics(lines, false, "lyrics.ovh (без таймкодов)");
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
}

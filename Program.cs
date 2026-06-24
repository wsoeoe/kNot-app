using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace kNot;

class Program
{
    static string AppDir => AppContext.BaseDirectory;
    static string DataDir => Path.Combine(AppDir, "data");
    static string ConfDir => Path.Combine(DataDir, "conf");
    static string DpiDir => Path.Combine(DataDir, "dpi");
    static string CacheDir => Path.Combine(DataDir, "cache");
    static string LogDir => Path.Combine(DataDir, "logs");
    static string OriginalDir => Path.Combine(DataDir, "original");
    static string WorkConf => Path.Combine(ConfDir, "knot.conf");
    static string SettingsFile => Path.Combine(DataDir, "settings.json");
    static string LogFile => Path.Combine(LogDir, "knot.log");

    static readonly string Version = "1.0.0";
    static Process? _dpiProcess;
    static System.Threading.Timer? _dpiWatchdog;
    static readonly string WireSockService = "wiresock-client-service";

    static readonly string[] WIRESOCK_PATHS = {
        @"C:\Program Files\WireSock Secure Connect\bin\wiresock-client.exe",
        @"C:\Program Files\WireSock\bin\wiresock-client.exe"
    };

    // Service groups
    static readonly string[] Groups = {
        "discord", "messengers", "ai", "youtube", "music", "gaming",
        "rutracker", "social", "cleantax", "cuda"
    };

    static readonly Dictionary<string, string> GroupLabels = new() {
        {"discord", "Discord"}, {"messengers", "Мессенджеры"}, {"ai", "ИИ-сервисы"},
        {"youtube", "YouTube"}, {"music", "Музыка"}, {"gaming", "Игровые"},
        {"rutracker", "Torrents"}, {"social", "Соцсети"}, {"cleantax", "Другое"},
        {"cuda", "CUDA"}
    };

    static readonly string[] ServiceTargets = {
        "discord.com", "web.telegram.org", "chatgpt.com", "soundcloud.com"
    };

    // DPI strategies
    static readonly Strategy[] Strategies = {
        new("fake", "fake", "Фейковые QUIC пакеты (12 шт)", 12, "google", false),
        new("fake2", "fake2", "Фейковые QUIC пакеты (20 шт)", 20, "google", false),
        new("fake_dbank", "fake_dbank", "Фейковые QUIC dbankcloud (12 шт)", 12, "dbank", false),
        new("multisplit", "multisplit", "Дробление handshake пакета", 0, null, true),
        new("fake_multi", "fake+multi", "Фейковые QUIC + дробление", 12, "google", true),
        new("fake_tls", "fake_tls", "Фейковые TLS ClientHello (12 шт)", 12, "tls", false),
    };

    static readonly (string endpoint, string desc)[] WarpEndpoints = {
        ("162.159.192.1:2408", "WARP #1 (стандарт)"),
        ("162.159.193.1:2408", "WARP #2"),
        ("162.159.195.1:2408", "WARP #3"),
        ("162.159.196.1:2408", "WARP #4"),
        ("162.159.192.5:2408", "WARP #5"),
        ("188.114.96.1:2408", "WARP #6"),
        ("188.114.97.1:2408", "WARP #7"),
        ("188.114.99.1:2408", "WARP #8"),
        ("188.114.96.177:2408", "WARP #9"),
        ("162.159.192.1:500", "WARP #10 (порт 500)"),
        ("162.159.192.1:1701", "WARP #11 (порт 1701)"),
        ("162.159.192.1:4500", "WARP #12 (порт 4500)"),
    };

    static void Main(string[] args)
    {
        // DPI bypass mode
        if (args.Length > 0 && args[0] == "--dpi-bypass")
        {
            string strategy = args.Length > 1 ? args[1] : "fake";
            RunDpiBypassMode(strategy);
            return;
        }

        // Elevated mode
        if (args.Length > 0 && args[0] == "--elevated")
        {
            string command = args.Length > 1 ? args[1] : "";
            ExecuteElevated(command);
            return;
        }

        EnsureDirs();
        SetupLogging();
        Log($"kNot запущен (версия {Version})");

        // ASCII анимация
        PlayIntro();

        // Auto-update IP lists if older than 24h
        AutoUpdateIplist();

        // First run
        var settings = LoadSettings();
        if (settings.FirstRun)
        {
            FirstRunSetup();
            settings = LoadSettings();
        }

        // Close handler
        Console.CancelKeyPress += (sender, e) =>
        {
            var s = LoadSettings();
            if (s.CloseBehavior == "stop")
            {
                StopDpiBypass();
                try { RunElevated("uninstall"); } catch { }
            }
            e.Cancel = true;
            Environment.Exit(0);
        };

        // Main loop
        while (true)
        {
            settings = LoadSettings();
            ShowHeader(settings);
            string choice = ShowMenu();
            if (choice == "q") break;

            switch (choice)
            {
                case "1": ImportConfig(); break;
                case "2": SelectServices(); break;
                case "3": ApplyConfig(); break;
                case "4": StartTunnel(); break;
                case "5": StopTunnel(); break;
                case "6": Diagnostics(); break;
                case "7": UpdateIplist(); break;
                case "8": ToggleAutostart(); break;
                case "9": ShowSettings(); break;
            }
        }
    }

    static void AutoUpdateIplist()
    {
        var cacheFile = Path.Combine(CacheDir, "iplist-ru-groups.json");
        if (File.Exists(cacheFile))
        {
            var age = DateTime.Now - File.GetLastWriteTime(cacheFile);
            if (age.TotalHours < 24) return; // Fresh enough
        }
        Log("IP-списки устарели, автообновление...");
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var url = "https://iplist.opencck.org/api/v1.0/get?country=ru&type=groups&format=json";
            var json = client.GetStringAsync(url).Result;
            File.WriteAllText(cacheFile, json, UTF8NoBOM);
            Log("IP-списки автообновлены");
        }
        catch (Exception e) { Log($"Автообновление IP не удалось: {e.Message}"); }
    }

    static void PlayIntro()
    {
        try
        {
            // Enable ANSI + UTF-8
            Console.Clear();
            var hOut = GetStdHandle(-11);
            uint mode;
            GetConsoleMode(hOut, out mode);
            SetConsoleMode(hOut, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
            Console.OutputEncoding = Encoding.UTF8;
            Console.CursorVisible = false;

            var logo = new[] {
                " ,.                                                                 ",
                "                                                                    ",
                "\u2592\u2591\u2591\u2591\u2591\u2591\u2591              \u2593\u2593\u2593\u2593\u2593\u2593@,         \u00c6\u2593\u2593\u2593\u2593\u2593\u00a8                          \u2584@\u2572\u2572@\u2596",
                "\u2591\u2591\u2591\u2591\u2591\u2591\u2591             \u2590\u2593\u2593\u2593\u2593\u2593\u2593\u2593N        \u2593\u2593\u2593\u2593\u2593\u2593                         \u2588\u2593\u2592\u2592\u2592\u2592\u2593",
                "\u2591\u2591\u2591\u2591\u2591\u2591\u2591             \u2590\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2596      \u2593\u2593\u2593\u2593\u2593\u2593          ,,,,,          \u2588\u2593\u2592\u2592\u2592\u2592\u2592",
                "\u2591\u2591\u2591\u2591\u2591\u2591\u2591     \u2591\u2591\u2591\u2591 \\  \u2590\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593     \u2593\u2593\u2593\u2593\u2593\u2593     ,g@\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593@\u2596   \u2584\u2593\u2592\u2593\u2593\u2592\u2592\u2592\u2592\u2593\u2593\u2593\u2593\u2593\u2596",
                "\u2591\u2591\u2591\u2591\u2591\u2591\u2591   \u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591  \u2590\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593   \u2593\u2593\u2593\u2593\u2593\u2593    \u2593\u2592\u2593\u2593\u2593\u2593\u2592\u2592\u2592\u2592\u2592\u2593\u2593\u2592\u2592\u2593N \u2593\u2593\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592",
                "\u2591\u2591\u2591\u2591\u2591 \u2591\u2591\u2591\u2591\u2591\u2591\u2591g\u00ae`   \u2590\u2593\u2593\u2593\u2593\u2593\u2593\u2599\u2593\u2592\u2593\u2593\u2593\u2593\u2593, \u2593\u2593\u2593\u2593\u2593\u2593\u2592   \u2593\u2592\u2593\u2593\u2593\u2592\u2593\u259c-`\u2599\u2580\u2593\u2593\u2593\u2593\u2593\u2593\u2593 `\u2599\u2588\u2593\u2592\u2592\u2592\u2592\u259c\u2599-",
                "\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2592\u2592      \u2590\u2593\u2593\u2593\u2593\u2593\u2593  \u2580\u2593\u2593\u2593\u2593\u2593\u2593\u2596\u2593\u2593\u2593\u2593\u2593\u2593\u2592  \u2593\u2593\u2593\u2593\u2593\u2593\u2593      \u2590\u2593\u2593\u2593\u2593\u2593[  \u2588\u2593\u2592\u2593\u2592\u2593\u2592\u2593",
                "\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591\u2591 .     \u2590\u2593\u2593\u2593\u2593\u2593\u2593   \"\u2593\u2592\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593  \u2593\u2593\u2593\u2593\u2593\u2593\u2593      \u2590\u2588\u2593\u2593\u2593\u2593\u2593\u2593  \u2588\u2593\u2593\u2593\u2593\u2593",
                "\u2592\u2591\u2591\u2591\u2591\u2591@\u257c\u2591\u2591\u2591\u2591,',   \u2590\u2593\u2593\u2593\u2593\u2593\u2593     \u2580\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593  \u2598\u2593\u2593\u2593\u2593\u2593\u2593\u2596    \u2593\u2593\u2593\u2593\u2593\u2593\u2592   \u2588\u2593\u2593\u2593\u2593\u2593",
                "\u2592\u2591\u2591\u2591\u2591\u2599\u0393  \u2592\u2591\u2591\u2591\u2591 \", \u2590\u2593\u2593\u2593\u2593\u2593\u2593       \u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593   \u2599\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2592\u2592\u2593\u2593\u2593\u2593\u2593\u2593\u2592   \u2588\u2593\u2593\u2593\u2593\u2593\u2593N@\u2596",
                "[\u2591\u2591\u2591\u2591\u2592\u2596   \u2599\u2591\u2591\u2591\u2591\u2591\u2591\u2592 \u2598\u2593\u2593\u2593\u2593\u2593        \u2599\u2593\u2593\u2593\u2593\u2593\u2593     \u2580\u2593\u2592\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2592\u2593\u2580      \u2580\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593\u2593",
                " \"\u2592\u2591*\"       \"\u2592\u2591*`   \u2599\u2580\u2580\u2580\u259c           \u2599\u2580\u2580\u2580\u2580        `\"\u2580\u2580\u2580\u2580\u2580\u2580\u2599`          \u2599\u2580\u2580\u2580\u2580\u2580\u259c",
            };

            // Monochrome gradient (ANSI 256-color): dark blue -> bright cyan -> dark blue
            var colors = new[] { 238, 24, 27, 33, 39, 45, 51, 45, 39, 33, 27, 24, 238, 242 };

            for (int i = 0; i < logo.Length; i++)
            {
                int col = Math.Max(0, (Console.WindowWidth - logo[i].Length) / 2);
                int row = Math.Max(0, (Console.WindowHeight - logo.Length) / 2) + i;
                Console.SetCursorPosition(col, row);
                var color = colors[i % colors.Length];
                Console.Write($"\x1b[38;5;{color}m{logo[i]}\x1b[0m");
                Thread.Sleep(60);
            }

            Thread.Sleep(800);
            Console.CursorVisible = true;
            Console.Clear();
        }
        catch { }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    static void EnsureDirs()
    {
        foreach (var d in new[] { DataDir, ConfDir, DpiDir, CacheDir, LogDir, OriginalDir })
            Directory.CreateDirectory(d);
    }

    // ───────────────────────── Logging ─────────────────────────

    static readonly UTF8Encoding UTF8NoBOM = new(false);

    static void Log(string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            File.AppendAllText(LogFile, line + "\n", UTF8NoBOM);
        }
        catch { }
    }

    // ───────────────────────── Settings ─────────────────────────

    class AppSettings
    {
        public string? ConfigPath { get; set; }
        public string[] Groups { get; set; } = { "discord" };
        public bool UseIPv6 { get; set; } = true;
        public bool Autostart { get; set; } = false;
        public bool FirstRun { get; set; } = true;
        public string CloseBehavior { get; set; } = "keep";
        public string DpiStrategy { get; set; } = "fake";
    }

    static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile, UTF8NoBOM)) ?? new();
        }
        catch { }
        return new();
    }

    static void SaveSettings(AppSettings s)
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }), UTF8NoBOM); }
        catch { }
    }

    // ───────────────────────── Menu ─────────────────────────

    static void ShowHeader(AppSettings settings)
    {
        Console.Clear();
        Console.WriteLine($"=== kNot - обход замедлений сервисов ===");
        Console.WriteLine($"   версия {Version} · обход замедлений через WARP");

        // Config
        if (!string.IsNullOrEmpty(settings.ConfigPath) && File.Exists(settings.ConfigPath))
            Console.WriteLine($"   Конфиг: {Path.GetFileName(settings.ConfigPath)} +");
        else
            Console.WriteLine($"   Конфиг: не выбран");

        // Services
        var groups = settings.Groups ?? new[] { "discord" };
        var labels = groups.Select(g => GroupLabels.GetValueOrDefault(g, g));
        Console.WriteLine($"   Сервисы: {string.Join(", ", labels)}");

        // Tunnel status
        var svc = ServiceStatus();
        var dpi = IsDpiRunning();
        string tunnelStatus = svc.Running ? "+ работает" : (svc.Installed ? "x остановлен" : "x не установлен");
        string dpiStr = dpi ? " · защита активна" : "";
        Console.WriteLine($"   Туннель: {tunnelStatus} · IPv6: {(settings.UseIPv6 ? "вкл" : "выкл")} · автозапуск: {(settings.Autostart ? "вкл" : "выкл")}{dpiStr}");
        // IP-списки
        var cacheFile = Path.Combine(CacheDir, "iplist-ru-groups.json");
        string iplage = "нет данных";
        if (File.Exists(cacheFile))
        {
            var age = DateTime.Now - File.GetLastWriteTime(cacheFile);
            if (age.TotalMinutes < 1) iplage = "только что";
            else if (age.TotalHours < 1) iplage = $"{(int)age.TotalMinutes} мин назад";
            else if (age.TotalDays < 1) iplage = $"{(int)age.TotalHours} ч назад";
            else iplage = $"{(int)age.TotalDays} дн назад";
        }
        Console.WriteLine($"   IP-списки: {iplage}");
        Console.WriteLine("========================================");
    }

    static string ShowMenu()
    {
        Console.WriteLine();
        Console.WriteLine("  1  Импортировать / сменить конфиг (.conf)");
        Console.WriteLine("  2  Выбрать сервисы (группы)");
        Console.WriteLine("  3  Применить — собрать конфиг");
        Console.WriteLine("  4  Запустить туннель");
        Console.WriteLine("  5  Остановить туннель");
        Console.WriteLine("  6  Статус и диагностика");
        Console.WriteLine("  7  Обновить IP-списки");
        Console.WriteLine("  8  Автозапуск при старте ПК");
        Console.WriteLine("  9  Настройки");
        Console.WriteLine("  q  Выход");
        Console.Write("Выбор> ");
        return Console.ReadLine()?.Trim() ?? "";
    }

    // ───────────────────────── First Run ─────────────────────────

    static void FirstRunSetup()
    {
        Console.WriteLine($"\nДобро пожаловать в kNot!\n");
        Console.WriteLine("kNot — обход замедлений сервисов (Discord, Telegram, YouTube и др.)");
        Console.WriteLine("через Cloudflare WARP с собственным DPI-обходом.\n");

        Console.WriteLine("Шаг 1. Что делать при закрытии окна kNot?");
        Console.WriteLine("  1  Оставить туннель работать (рекомендуется)");
        Console.WriteLine("  2  Остановить туннель при закрытии");
        Console.Write("Выбор> ");
        var raw = Console.ReadLine()?.Trim() ?? "1";
        var behavior = raw == "2" ? "stop" : "keep";

        var s = LoadSettings();
        s.FirstRun = false;
        s.CloseBehavior = behavior;
        SaveSettings(s);
        Console.WriteLine("+ Сохранено.\n");

        Console.WriteLine("Шаг 2. WARP конфиг\n");
        Console.WriteLine("Для работы kNot нужен WARP конфиг (.conf).");
        Console.WriteLine("  1. Откройте в браузере: https://wsoeoe.github.io/kNot-site/");
        Console.WriteLine("  2. В блоке WireSock нажмите AWG 2.0 и скачайте .conf файл");
        Console.WriteLine("  3. Положите .conf файл в папку (рядом с knot.exe -> data -> conf)");
        Console.WriteLine("\nПосле этого выберите пункт [1] в меню.\n");
        Console.WriteLine("WireSock (VPN клиент) установится автоматически при первом [4].");
        Console.WriteLine("\nНажмите Enter для продолжения...");
        Console.ReadLine();
    }

    // ───────────────────────── [1] Import Config ─────────────────────────

    static void ImportConfig()
    {
        Console.WriteLine("\nИмпорт конфига (.conf)\n");

        // Auto-find .conf in conf/
        var found = Directory.GetFiles(ConfDir, "*.conf")
            .Where(f => Path.GetFileName(f) != "knot.conf")
            .ToList();

        // Also check Downloads
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            found.AddRange(Directory.GetFiles(downloads, "*.conf")
                .Where(f => Path.GetFileName(f) != "knot.conf"));
        }

        string? selected = null;
        if (found.Count > 0)
        {
            Console.WriteLine("Найдены конфиги:");
            for (int i = 0; i < found.Count; i++)
            {
                var loc = found[i].Contains("Downloads") ? "(Downloads)" : "(conf/)";
                Console.WriteLine($"  {i + 1}  {Path.GetFileName(found[i])} {loc}");
            }
            Console.WriteLine("  m  Указать вручную");
            Console.Write("Выбор> ");
            var raw = Console.ReadLine()?.Trim() ?? "";
            if (raw == "m") selected = null;
            else if (int.TryParse(raw, out int idx) && idx >= 1 && idx <= found.Count)
                selected = found[idx - 1];
        }

        if (selected == null)
        {
            Console.WriteLine("Путь к .conf (или Enter для отмены): https://wsoeoe.github.io/kNot-site/");
            Console.Write("> ");
            var path = Console.ReadLine()?.Trim().Trim('"').Trim('\'') ?? "";
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) { Console.WriteLine($"x Файл не найден: {path}"); Pause(); return; }

            // Copy to conf/
            var dest = Path.Combine(ConfDir, Path.GetFileName(path));
            File.Copy(path, dest, true);
            selected = dest;
        }

        // Copy to conf/ if not already there
        if (!selected.StartsWith(ConfDir))
        {
            var dest = Path.Combine(ConfDir, Path.GetFileName(selected));
            File.Copy(selected, dest, true);
            selected = dest;
        }

        // Backup original
        var backup = Path.Combine(OriginalDir, Path.GetFileName(selected) + ".orig");
        File.Copy(selected, backup, true);

        // Read config without BOM
        var s = LoadSettings();
        s.ConfigPath = Path.GetFullPath(selected);
        SaveSettings(s);
        Log($"Импортирован конфиг: {selected}");

        Console.WriteLine($"+ Конфиг импортирован: {Path.GetFileName(selected)}");

        // Check WireSock
        var ws = FindWireSock();
        if (ws == null)
            Console.WriteLine("! WireSock не найден. Установится автоматически при [4].");
        else
            Console.WriteLine($"+ Клиент: wiresock ({Path.GetFileName(ws)})");

        Pause();
    }

    // ───────────────────────── [2] Select Services ─────────────────────────

    static void SelectServices()
    {
        var s = LoadSettings();
        var selected = s.Groups?.ToHashSet() ?? new HashSet<string> { "discord" };

        Console.WriteLine("\nПресеты:");
        Console.WriteLine("  1  Только Discord");
        Console.WriteLine("  2  Всё кроме RU");
        Console.WriteLine("  3  Всё");
        Console.WriteLine("  0  Выбрать вручную");
        Console.Write("> ");
        var preset = Console.ReadLine()?.Trim() ?? "0";

        if (preset == "1")
        {
            selected.Clear();
            selected.Add("discord");
        }
        else if (preset == "2")
        {
            selected.Clear();
            foreach (var g in Groups) selected.Add(g);
        }
        else if (preset == "3")
        {
            selected.Clear();
            foreach (var g in Groups) selected.Add(g);
        }
        else
        {
            Console.WriteLine("\nВыбор сервисов (через запятую)\n");
            for (int i = 0; i < Groups.Length; i++)
            {
                var mark = selected.Contains(Groups[i]) ? "+" : " ";
                Console.WriteLine($"  {mark} {i + 1}. {GroupLabels.GetValueOrDefault(Groups[i], Groups[i])}");
            }
            Console.Write("\nВыбор> ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (!string.IsNullOrEmpty(input))
            {
                selected.Clear();
                foreach (var tok in input.Split(','))
                    if (int.TryParse(tok.Trim(), out int idx) && idx >= 1 && idx <= Groups.Length)
                        selected.Add(Groups[idx - 1]);
            }
        }

        s.Groups = selected.ToArray();
        SaveSettings(s);
        Console.WriteLine($"+ Выбрано: {string.Join(", ", s.Groups.Select(g => GroupLabels.GetValueOrDefault(g, g)))}");
        Pause();
    }

    // ───────────────────────── [3] Apply Config ─────────────────────────

    static void ApplyConfig()
    {
        var s = LoadSettings();
        if (string.IsNullOrEmpty(s.ConfigPath) || !File.Exists(s.ConfigPath))
        {
            Console.WriteLine("x Сначала импортируйте конфиг (пункт 1).");
            Pause();
            return;
        }

        // Load original config
        var lines = File.ReadAllLines(s.ConfigPath, Encoding.UTF8).ToList();

        // Collect CIDRs from iplist cache
        var cidrs = CollectCidrs(s.Groups ?? new[] { "discord" }, s.UseIPv6);
        if (cidrs.Count == 0)
        {
            Console.WriteLine("x IP-списки пусты. Нажмите [7] для обновления.");
            Pause();
            return;
        }

        // Build new config: keep [Interface] + [Peer] but replace AllowedIPs
        var result = new List<string>();
        bool inPeer = false;
        bool allowedSet = false;
        bool mtuSet = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("[Peer]"))
            {
                inPeer = true;
                result.Add(line);
                continue;
            }

            // Remove WireSock masking fields (Id/Ip/Ib)
            if (trimmed.StartsWith("Id =") || trimmed.StartsWith("Ip =") || trimmed.StartsWith("Ib ="))
                continue;

            // Set MTU to 1380 if MTU exists, otherwise add it
            if (trimmed.StartsWith("MTU ="))
            {
                result.Add("MTU = 1380");
                mtuSet = true;
                continue;
            }

            // Replace AllowedIPs
            if (inPeer && trimmed.StartsWith("AllowedIPs ="))
            {
                result.Add($"AllowedIPs = {string.Join(", ", cidrs)}");
                allowedSet = true;
                continue;
            }

            result.Add(line);
        }

        // Add MTU if not set
        if (!mtuSet)
        {
            // Insert after [Interface] line
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i].Trim().StartsWith("[Interface]"))
                {
                    result.Insert(i + 1, "MTU = 1380");
                    break;
                }
            }
        }

        // Add PersistentKeepalive if not present
        if (inPeer && !result.Any(l => l.Trim().StartsWith("PersistentKeepalive")))
            result.Add("PersistentKeepalive = 25");

        // Add AllowedIPs if [Peer] had none
        if (inPeer && !allowedSet)
            result.Add($"AllowedIPs = {string.Join(", ", cidrs)}");

        // Atomic write (UTF-8 without BOM)
        var tmp = WorkConf + ".tmp";
        File.WriteAllLines(tmp, result, new UTF8Encoding(false));
        File.Move(tmp, WorkConf, true);

        Console.WriteLine($"+ Конфиг собран: {cidrs.Count} CIDR");
        Log($"Конфиг собран: {cidrs.Count} CIDR");
        Pause();
    }

    // ───────────────────────── [4] Start Tunnel ─────────────────────────

    static void StartTunnel()
    {
        if (!File.Exists(WorkConf))
        {
            Console.WriteLine("x Сначала примените конфиг (пункт 3).");
            Pause();
            return;
        }

        // Already running?
        var svc = ServiceStatus();
        if (svc.Running)
        {
            Console.WriteLine("\nТуннель уже работает.");
            if (!IsDpiRunning())
                StartDpiBypass();
            Pause();
            return;
        }

        // Find or install WireSock
        var ws = FindWireSock();
        if (ws == null)
        {
            Console.WriteLine("\nWireSock не найден. Устанавливаю автоматически...");
            if (!InstallWireSock())
            {
                Console.WriteLine("x Не удалось установить WireSock.");
                Console.WriteLine("   Скачайте: https://www.wiresock.net/wiresock-secure-connect/download");
                Pause();
                return;
            }
            ws = FindWireSock();
        }

        Console.WriteLine("\nЗапуск туннеля...");
        Console.WriteLine("Запрашиваю права администратора...");

        // Start DPI bypass BEFORE WireSock
        StartDpiBypass();

        // Install WireSock
        var result = RunElevated("install");
        if (result.Ok)
        {
            Console.Write("Ожидаю подключение...");
            // Wait for service + route stabilization
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(1000);
                svc = ServiceStatus();
                if (svc.Running)
                {
                    Console.Write("..");
                    Thread.Sleep(5000); // Route stabilization
                    break;
                }
                Console.Write(".");
            }
            Console.WriteLine();

            svc = ServiceStatus();
            if (svc.Running)
            {
                var dpi = IsDpiRunning();
                Console.WriteLine($"+ Туннель работает{(dpi ? " · защита активна" : "")}");
            }
            else
            {
                Console.WriteLine($"! Сервис установлен, но не поднялся ({svc.State}).");
                Console.WriteLine("   См. пункт 6 (диагностика).");
            }
        }
        else
        {
            Console.WriteLine($"x Ошибка: {result.Output}");
            StopDpiBypass();
        }
        Pause();
    }

    // ───────────────────────── [5] Stop Tunnel ─────────────────────────

    static void StopTunnel()
    {
        Console.WriteLine("\nОстановка туннеля...");
        StopDpiBypass();

        var svc = ServiceStatus();
        if (!svc.Installed)
        {
            Console.WriteLine("+ Туннель не установлен.");
            Pause();
            return;
        }

        var result = RunElevated("uninstall");
        if (result.Ok)
            Console.WriteLine("+ Туннель остановлен.");
        else
            Console.WriteLine($"x Ошибка: {result.Output}");
        Pause();
    }

    // ───────────────────────── [6] Diagnostics ─────────────────────────

    static void Diagnostics()
    {
        Console.WriteLine("\nДиагностика");

        var svc = ServiceStatus();
        Console.WriteLine($"  Туннель (сервис)     {(svc.Running ? "OK" : "x " + svc.State)}");

        var ws = FindWireSock();
        Console.WriteLine($"  Клиент               {(ws != null ? "OK " + Path.GetFileName(ws) : "x не найден")}");

        var dpi = IsDpiRunning();
        Console.WriteLine($"  Защита туннеля       {(dpi ? "OK работает" : "x не запущена")}");

        if (!svc.Running)
        {
            Console.WriteLine("  Доступность          пропущено — туннель не активен");
            Pause();
            return;
        }

        // Ping test
        Log("Проверяю связность туннеля (ping 1.1.1.1) ...");
        var pingOk = PingHost("1.1.1.1");
        Console.WriteLine($"  Связность (ping)     {(pingOk ? "OK 1.1.1.1 отвечает" : "x handshake не завершён")}");

        // Service tests
        Log("Тестирую доступность сервисов ...");
        var services = new[] {
            ("Discord", "discord.com"),
            ("Telegram", "web.telegram.org"),
            ("ChatGPT", "chatgpt.com"),
            ("SoundCloud", "soundcloud.com"),
        };

        foreach (var (name, host) in services)
        {
            var (ok, ms, ip) = ProbeService(host);
            Console.WriteLine($"  {name,-20} {(ok ? "OK" : "x")} {(ok ? $"{ms}ms" : "FAIL")} {(ip != null ? "-> " + ip : "")}");
        }

        Console.WriteLine($"\n  Лог: {LogFile}");
        Pause();
    }

    // ───────────────────────── [7] Update IP Lists ─────────────────────────

    static void UpdateIplist()
    {
        Console.WriteLine("\nОбновление IP-списков...");
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var url = "https://iplist.opencck.org/api/v1.0/get?country=ru&type=groups&format=json";
            var json = client.GetStringAsync(url).Result;
            var cacheFile = Path.Combine(CacheDir, "iplist-ru-groups.json");
            File.WriteAllText(cacheFile, json, UTF8NoBOM);

            var s = LoadSettings();
            // Parse to count groups
            using var doc = JsonDocument.Parse(json);
            int count = doc.RootElement.EnumerateObject().Count();
            Console.WriteLine($"+ IP-списки обновлены: {count} групп");

            // Update timestamp
            Log("IP-списки обновлены");
        }
        catch (Exception e)
        {
            Console.WriteLine($"x Ошибка: {e.Message}");
        }
        Pause();
    }

    // ───────────────────────── [8] Autostart ─────────────────────────

    static void ToggleAutostart()
    {
        var s = LoadSettings();
        s.Autostart = !s.Autostart;
        SaveSettings(s);
        Console.WriteLine($"\n+ Автозапуск: {(s.Autostart ? "ВКЛ" : "ВЫКЛ")}");
        Pause();
    }

    // ───────────────────────── [9] Settings ─────────────────────────

    static void ShowSettings()
    {
        while (true)
        {
            var s = LoadSettings();
            Console.WriteLine("\nНастройки\n");
            Console.WriteLine($"  1  IPv6: {(s.UseIPv6 ? "вкл" : "выкл")}");
            Console.WriteLine($"  2  Поведение при закрытии: {s.CloseBehavior}");
            Console.WriteLine($"  3  Сменить endpoint");
            Console.WriteLine($"  4  Стратегия DPI-обхода");
            Console.WriteLine("  Enter — назад");
            Console.Write("> ");
            var raw = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(raw)) return;

            switch (raw)
            {
                case "1":
                    s.UseIPv6 = !s.UseIPv6;
                    SaveSettings(s);
                    Console.WriteLine($"+ IPv6: {(s.UseIPv6 ? "вкл" : "выкл")}");
                    Pause();
                    break;
                case "2":
                    Console.WriteLine("  1  Оставлять  2  Останавливать  3  Спрашивать");
                    Console.Write("> ");
                    var cb = Console.ReadLine()?.Trim() ?? "1";
                    s.CloseBehavior = cb switch { "2" => "stop", "3" => "ask", _ => "keep" };
                    SaveSettings(s);
                    Console.WriteLine($"+ Сохранено: {s.CloseBehavior}");
                    Pause();
                    break;
                case "3":
                    ChangeEndpoint();
                    break;
                case "4":
                    SelectDpiStrategy();
                    break;
            }
        }
    }

    static void ChangeEndpoint()
    {
        if (!File.Exists(WorkConf))
        {
            Console.WriteLine("x Сначала соберите конфиг (пункт 3).");
            Pause();
            return;
        }

        var current = GetEndpoint();
        Console.WriteLine($"\nСмена endpoint WARP");
        Console.WriteLine($"  Текущий: {current}\n");

        for (int i = 0; i < WarpEndpoints.Length; i++)
        {
            var mark = WarpEndpoints[i].endpoint == current ? "<" : " ";
            Console.WriteLine($"  {i + 1} {mark} {WarpEndpoints[i].endpoint,-24} {WarpEndpoints[i].desc}");
        }
        Console.WriteLine("  c  Ввести свой");
        Console.Write("Endpoint> ");
        var raw = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw)) return;

        string newEp;
        if (raw == "c")
        {
            Console.Write("IP:порт > ");
            newEp = Console.ReadLine()?.Trim() ?? "";
            if (!newEp.Contains(':')) { Console.WriteLine("x Неверный формат"); Pause(); return; }
        }
        else if (int.TryParse(raw, out int idx) && idx >= 1 && idx <= WarpEndpoints.Length)
            newEp = WarpEndpoints[idx - 1].endpoint;
        else { Console.WriteLine("x Неверный ввод"); Pause(); return; }

        // Text-based replace (preserves comments)
        var text = File.ReadAllText(WorkConf, Encoding.UTF8);
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().StartsWith("Endpoint"))
            {
                text = text.Replace(line.Trim(), $"Endpoint = {newEp}");
                break;
            }
        }
        File.WriteAllText(WorkConf, text, UTF8NoBOM);
        Log($"Endpoint изменён: {current} -> {newEp}");
        Console.WriteLine($"+ Endpoint: {current} -> {newEp}");
        Console.WriteLine("Перезапустите туннель: [5] -> [4]");
        Pause();
    }

    static void SelectDpiStrategy()
    {
        var s = LoadSettings();
        Console.WriteLine("\nСтратегия DPI-обхода");
        Console.WriteLine("Разные провайдеры режут по-разному.\n");
        for (int i = 0; i < Strategies.Length; i++)
        {
            var mark = Strategies[i].Id == s.DpiStrategy ? "<" : " ";
            Console.WriteLine($"  {i + 1} {mark} {Strategies[i].Name,-14} {Strategies[i].Desc}");
        }
        Console.WriteLine("  a  AutoStart — авто-подбор");
        Console.Write("> ");
        var raw = Console.ReadLine()?.Trim() ?? "";
        if (raw == "a") { AutoStart(); return; }
        if (int.TryParse(raw, out int idx) && idx >= 1 && idx <= Strategies.Length)
        {
            s.DpiStrategy = Strategies[idx - 1].Id;
            SaveSettings(s);
            Console.WriteLine($"+ Стратегия: {Strategies[idx - 1].Name}");
            Console.WriteLine("Перезапустите туннель: [5] -> [4]");
        }
        Pause();
    }

    static void AutoStart()
    {
        if (!File.Exists(WorkConf))
        {
            Console.WriteLine("x Сначала [1] -> [2] -> [3]");
            Pause();
            return;
        }

        Console.WriteLine("\nAutoStart — авто-подбор стратегии\n");
        var originalStrategy = LoadSettings().DpiStrategy;

        // Stop everything
        StopDpiBypass();
        RunElevated("uninstall");
        Thread.Sleep(2000);

        foreach (var strat in Strategies)
        {
            Console.WriteLine($"[{Array.IndexOf(Strategies, strat) + 1}/{Strategies.Length}] {strat.Name}: {strat.Desc}");
            var s = LoadSettings();
            s.DpiStrategy = strat.Id;
            SaveSettings(s);

            StopDpiBypass();
            Thread.Sleep(1000);
            StartDpiBypass();
            Thread.Sleep(1000);

            try { RunElevated("install"); }
            catch (Exception) { Console.WriteLine("  -> WireSock не запустился"); StopDpiBypass(); continue; }

            Console.Write("  Ожидаю handshake...");
            bool ok = false;
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(2000);
                Console.Write(".");
                try
                {
                    using var sock = new TcpClient();
                    sock.Connect("1.1.1.1", 443);
                    ok = true;
                    break;
                }
                catch { }
            }
            Console.WriteLine();

            if (ok)
            {
                // Test Discord
                bool discordOk = false;
                try
                {
                    using var sock = new TcpClient();
                    sock.Connect("162.159.135.232", 443);
                    discordOk = true;
                }
                catch { }

                Console.WriteLine($"  -> Handshake OK! Discord: {(discordOk ? "OK" : "FAIL")}");
                if (discordOk)
                {
                    Console.WriteLine($"+ Рабочая стратегия: {strat.Name}");
                    Console.WriteLine("Сохранено. В следующий раз used автоматически.");
                    Pause();
                    return;
                }
            }
            else
                Console.WriteLine("  -> Handshake не прошёл");

            StopDpiBypass();
            RunElevated("uninstall");
            Thread.Sleep(2000);
        }

        // Restore original
        var sRestore = LoadSettings();
        sRestore.DpiStrategy = originalStrategy;
        SaveSettings(sRestore);
        Console.WriteLine("x Ни одна стратегия не сработала.");
        Pause();
    }

    // ───────────────────────── WireSock Management ─────────────────────────

    class ServiceInfo
    {
        public bool Installed;
        public bool Running;
        public string State = "не установлен";
    }

    static string? FindWireSock()
    {
        foreach (var p in WIRESOCK_PATHS)
            if (File.Exists(p)) return p;
        // Check PATH
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path != null)
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                var full = Path.Combine(dir, "wiresock-client.exe");
                if (File.Exists(full) && !full.Contains("WindowsApps")) return full;
            }
        return null;
    }

    static bool InstallWireSock()
    {
        // Try winget
        try
        {
            var p = Process.Start(new ProcessStartInfo("winget",
                "install NTKERNEL.WireSockVPNClient --accept-package-agreements --accept-source-agreements --silent")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(120000);
            Thread.Sleep(2000);
            var ws = FindWireSock();
            if (ws != null) { Log("WireSock установлен через winget"); return true; }
        }
        catch { }

        // Try direct download
        try
        {
            var url = "https://wiresock.net/_api/download-release.php?product=wiresock-secure-connect&platform=windows_x64&version=3.4.8.1";
            var msiPath = Path.Combine(Path.GetTempPath(), "wiresock-setup.msi");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            File.WriteAllBytes(msiPath, client.GetByteArrayAsync(url).Result);

            var p = Process.Start(new ProcessStartInfo("msiexec", $"/i \"{msiPath}\" /quiet /norestart")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(120000);
            Thread.Sleep(3000);
            try { File.Delete(msiPath); } catch { }
            var wsInstalled = FindWireSock();
            if (wsInstalled != null) { Log("WireSock установлен через .msi"); return true; }
        }
        catch (Exception e) { Log($"WireSock install failed: {e.Message}"); }
        return false;
    }

    static ServiceInfo ServiceStatus()
    {
        var result = new ServiceInfo();
        try
        {
            var p = Process.Start(new ProcessStartInfo("sc", $"query {WireSockService}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            });
            p?.WaitForExit(10000);
            var text = p?.StandardOutput.ReadToEnd() ?? "";
            if (text.Contains("RUNNING"))
            { result.Installed = true; result.Running = true; result.State = "работает"; }
            else if (text.Contains("STOPPED"))
            { result.Installed = true; result.State = "остановлен"; }
            else if (text.Contains("START_PENDING"))
            { result.Installed = true; result.State = "запускается"; }
        }
        catch { }
        return result;
    }

    // ───────────────────────── Elevated Execution ─────────────────────────

    class ElevResult { public bool Ok; public string Output = ""; }

    static bool IsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    static ElevResult RunElevated(string command)
    {
        if (IsAdmin())
            return ExecuteElevatedDirect(command);

        // ShellExecute runas
        var marker = Path.Combine(DataDir, $"elevated_{Process.GetCurrentProcess().Id}_{Guid.NewGuid():N}.json");
        try { File.Delete(marker); } catch { }

        var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        var args = $"--elevated {command} --marker \"{marker}\"";

        try
        {
            var psi = new ProcessStartInfo(exePath, args)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppDir
            };
            Process.Start(psi);
        }
        catch (Exception e)
        {
            Log($"Elevation failed: {e.Message}");
            return new ElevResult { Ok = false, Output = e.Message };
        }

        // Wait for marker file
        var deadline = DateTime.Now.AddSeconds(120);
        while (DateTime.Now < deadline)
        {
            if (File.Exists(marker))
            {
                try
                {
                    var json = File.ReadAllText(marker, Encoding.UTF8);
                    var result = JsonSerializer.Deserialize<JsonElement>(json);
                    var ok = result.GetProperty("ok").GetBoolean();
                    var output = result.GetProperty("output").GetString() ?? "";
                    try { File.Delete(marker); } catch { }
                    return new ElevResult { Ok = ok, Output = output };
                }
                catch { Thread.Sleep(500); }
            }
            Thread.Sleep(500);
        }
        return new ElevResult { Ok = false, Output = "timeout" };
    }

    static ElevResult ExecuteElevatedDirect(string command)
    {
        try
        {
            string output;
            var ws = FindWireSock();
            var s = LoadSettings();
            var startType = s.Autostart ? "2" : "3";

            // Stop + uninstall first
            RunHidden("sc", $"stop {WireSockService}");
            Thread.Sleep(3000);
            if (ws != null)
                RunHidden(ws, "uninstall");
            Thread.Sleep(2000);
            // Kill ghost processes
            RunHidden("taskkill", "/f /im wiresock-client.exe");
            Thread.Sleep(2000);

            if (command == "install")
            {
                if (ws == null) return new ElevResult { Ok = false, Output = "WireSock not found" };
                var r1 = RunHidden(ws, $"install -start-type {startType} -config \"{WorkConf}\" -log-level info -lac");
                Thread.Sleep(1000);
                var r2 = RunHidden("sc", $"start {WireSockService}");
                output = r1 + "\n" + r2;
            }
            else // uninstall
            {
                if (ws != null)
                    output = RunHidden(ws, "uninstall");
                else
                    output = RunHidden("sc", $"delete {WireSockService}");
            }

            return new ElevResult { Ok = true, Output = output };
        }
        catch (Exception e)
        {
            return new ElevResult { Ok = false, Output = e.Message };
        }
    }

    static void ExecuteElevated(string command)
    {
        // Parse --marker
        string marker = "";
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--marker") marker = args[i + 1];
        }

        var result = ExecuteElevatedDirect(command);

        if (!string.IsNullOrEmpty(marker))
        {
            try
            {
                var json = JsonSerializer.Serialize(new { ok = result.Ok, output = result.Output });
                File.WriteAllText(marker, json, Encoding.UTF8);
            }
            catch { }
        }
    }

    static string RunHidden(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = Process.Start(psi);
            p?.WaitForExit(30000);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            var err = p?.StandardError.ReadToEnd() ?? "";
            return (output + " " + err).Trim();
        }
        catch (Exception e) { return e.Message; }
    }

    // ───────────────────────── DPI Bypass ─────────────────────────

    static bool IsDpiRunning()
    {
        if (_dpiProcess != null && !_dpiProcess.HasExited)
            return true;
        // Check for knot.exe with --dpi-bypass (exclude ourselves)
        try
        {
            var p = Process.Start(new ProcessStartInfo("tasklist", "/fi \"imagename eq knot.exe\" /fo csv /nh")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            });
            p?.WaitForExit(3000);
            var text = p?.StandardOutput.ReadToEnd() ?? "";
            // tasklist shows PID in column 2. Count knot.exe instances minus ourselves.
            // If there are 2+ knot.exe processes, one is us and one is DPI.
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int count = 0;
            foreach (var line in lines)
            {
                if (line.Contains("knot.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract PID (CSV format: "knot.exe","1234",...)
                    var parts = line.Split(',');
                    if (parts.Length >= 2)
                    {
                        var pidStr = parts[1].Trim('"').Trim();
                        if (int.TryParse(pidStr, out int pid) && pid != Environment.ProcessId)
                            count++;
                    }
                }
            }
            return count > 0;
        }
        catch { return false; }
    }

    static void StartDpiBypass()
    {
        if (IsDpiRunning()) { Log("DPI-обход уже работает"); return; }

        DoStartDpi();

        // Watchdog — проверяет каждые 30 сек, перезапускает если упал
        _dpiWatchdog?.Dispose();
        _dpiWatchdog = new System.Threading.Timer(_ =>
        {
            if (!IsDpiRunning() && ServiceStatus().Running)
            {
                Log("DPI-обход: упал, перезапускаю...");
                DoStartDpi();
            }
        }, null, 30000, 30000);
    }

    static void DoStartDpi()
    {
        var s = LoadSettings();
        var strategy = s.DpiStrategy;
        Log($"DPI-обход: запускаю (стратегия: {strategy})");

        var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        try
        {
            if (IsAdmin())
            {
                _dpiProcess = Process.Start(new ProcessStartInfo(exePath, $"--dpi-bypass {strategy}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
            }
            else
            {
                var psi = new ProcessStartInfo(exePath, $"--dpi-bypass {strategy}")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppDir
                };
                _dpiProcess = Process.Start(psi);
            }
            Thread.Sleep(3000);
            if (_dpiProcess != null && !_dpiProcess.HasExited)
                Log($"DPI-обход: запущен (PID {_dpiProcess.Id})");
            else if (IsDpiRunning())
                Log("DPI-обход: запущен (elevated)");
            else
                Log("DPI-обход: процесс упал");
        }
        catch (Exception e) { Log($"DPI-обход: ошибка запуска: {e.Message}"); }
    }

    static void StopDpiBypass()
    {
        _dpiWatchdog?.Dispose();
        _dpiWatchdog = null;

        if (_dpiProcess != null && !_dpiProcess.HasExited)
        {
            try { _dpiProcess.Kill(); _dpiProcess.WaitForExit(5000); }
            catch { }
        }
        _dpiProcess = null;

        // Kill any knot.exe --dpi-bypass processes (NOT ourselves)
        try
        {
            foreach (var proc in Process.GetProcessesByName("knot"))
            {
                try
                {
                    if (proc.Id != Environment.ProcessId) // Don't kill ourselves
                        proc.Kill();
                }
                catch { }
            }
        }
        catch { }
        Log("DPI-обход: остановлен");
    }

    static void RunDpiBypassMode(string strategyId)
    {
        try
        {
            RunDpiBypassInner(strategyId);
        }
        catch (Exception e)
        {
            // Log to file before dying
            try
            {
                File.AppendAllText(LogFile,
                    $"[DPI FATAL] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}\n", UTF8NoBOM);
            }
            catch { }
            Console.WriteLine($"[DPI] FATAL: {e}");
            Console.WriteLine("[DPI] Press any key to exit...");
            try { Console.ReadKey(true); } catch { }
        }
    }

    static void RunDpiBypassInner(string strategyId)
    {
        // Extract WinDivert files if needed
        ExtractDpiFiles();

        var strategy = Strategies.FirstOrDefault(s => s.Id == strategyId) ?? Strategies[0];

        // Read endpoint from config
        string endpoint = "162.159.192.1:4500";
        if (File.Exists(WorkConf))
        {
            foreach (var line in File.ReadAllLines(WorkConf, Encoding.UTF8))
            {
                if (line.Trim().StartsWith("Endpoint"))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var ep = parts[1].Trim();
                        if (ep.Contains(':'))
                        {
                            var colon = ep.LastIndexOf(':');
                            var host = ep[..colon];
                            var port = ep[(colon + 1)..];
                            try { endpoint = $"{Dns.GetHostAddresses(host)[0].ToString()}:{port}"; }
                            catch { endpoint = ep; }
                        }
                    }
                    break;
                }
            }
        }

        var colonIdx = endpoint.LastIndexOf(':');
        var endpointIp = endpoint[..colonIdx];
        var endpointPort = int.Parse(endpoint[(colonIdx + 1)..]);

        // Load fake payload
        byte[]? fakePayload = strategy.PayloadType switch
        {
            "google" => LoadDpiFile("quic_initial_www_google_com.bin"),
            "dbank" => LoadDpiFile("quic_initial_dbankcloud_ru.bin"),
            "tls" => LoadDpiFile("tls_clienthello_www_google_com.bin"),
            _ => null
        };

        Console.WriteLine($"[DPI] kNot DPI Bypass — {endpoint} — strategy: {strategy.Name}");
        if (fakePayload != null)
            Console.WriteLine($"[DPI] Fake payload: {fakePayload.Length} bytes");

        // Start WinDivert
        var windivert = new WinDivertInterop(DpiDir);
        if (!windivert.Open($"outbound and udp and udp.DstPort == {endpointPort}"))
        {
            Console.WriteLine($"[DPI] WinDivert open failed: {windivert.LastError}");
            return;
        }

        Console.WriteLine($"[DPI] WinDivert open, target {endpointIp}:{endpointPort}");

        int packetCount = 0;
        int ipId = 0;

        while (true)
        {
            var packet = windivert.Receive();
            if (packet == null) { Thread.Sleep(10); continue; }

            // Parse IP header
            if (packet.Length < 28) { windivert.Send(packet); continue; }
            if ((packet[0] >> 4) != 4) { windivert.Send(packet); continue; }

            int ihl = (packet[0] & 0xF) * 4;
            if (packet[9] != 17) { windivert.Send(packet); continue; } // UDP only

            int srcPort = (packet[ihl] << 8) | packet[ihl + 1];
            int dstPort = (packet[ihl + 2] << 8) | packet[ihl + 3];
            var srcIp = $"{packet[12]}.{packet[13]}.{packet[14]}.{packet[15]}";

            // 1. Send fake packets
            if (fakePayload != null && strategy.Repeats > 0)
            {
                for (int i = 0; i < strategy.Repeats; i++)
                {
                    ipId = (ipId + 1) & 0xFFFF;
                    var fake = BuildUdpPacket(srcIp, srcPort, endpointIp, endpointPort, fakePayload, ipId);
                    windivert.Send(fake);
                }
            }

            // 2. Send real packet (maybe split)
            if (strategy.Split && packet.Length > 100)
            {
                var frags = SplitPacket(packet, 64);
                foreach (var frag in frags)
                    windivert.Send(frag);
            }
            else
            {
                windivert.Send(packet);
            }

            packetCount++;
            if (packetCount <= 5 || packetCount % 100 == 0)
            {
                string tag = (strategy.Repeats > 0 ? $" +{strategy.Repeats} fake" : "") + (strategy.Split ? " +split" : "");
                var msg = $"[DPI] #{packetCount}: {srcIp}:{srcPort} -> {endpointIp}:{endpointPort} ({packet.Length}b){tag}";
                Console.WriteLine(msg);
                if (packetCount % 500 == 0)
                    File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\n", UTF8NoBOM);
            }
        }
    }

    static byte[] BuildUdpPacket(string srcIp, int srcPort, string dstIp, int dstPort, byte[] payload, int ipId)
    {
        int udpLen = 8 + payload.Length;
        int totalLen = 20 + udpLen;
        var packet = new byte[totalLen];

        // IP header
        packet[0] = 0x45; // version=4, ihl=5
        packet[1] = 0x00;
        packet[2] = (byte)(totalLen >> 8);
        packet[3] = (byte)(totalLen & 0xFF);
        packet[4] = (byte)(ipId >> 8);
        packet[5] = (byte)(ipId & 0xFF);
        packet[6] = 0x40; packet[7] = 0x00; // DF
        packet[8] = 64; // TTL
        packet[9] = 17; // UDP
        // checksum = 0 (WinDivert fixes it)

        var srcParts = srcIp.Split('.');
        var dstParts = dstIp.Split('.');
        for (int i = 0; i < 4; i++) { packet[12 + i] = byte.Parse(srcParts[i]); packet[16 + i] = byte.Parse(dstParts[i]); }

        // IP checksum
        int cksum = IpChecksum(packet, 20);
        packet[10] = (byte)(cksum >> 8);
        packet[11] = (byte)(cksum & 0xFF);

        // UDP header
        packet[20] = (byte)(srcPort >> 8);
        packet[21] = (byte)(srcPort & 0xFF);
        packet[22] = (byte)(dstPort >> 8);
        packet[23] = (byte)(dstPort & 0xFF);
        packet[24] = (byte)(udpLen >> 8);
        packet[25] = (byte)(udpLen & 0xFF);
        packet[26] = 0; packet[27] = 0; // checksum

        // Payload
        Buffer.BlockCopy(payload, 0, packet, 28, payload.Length);
        return packet;
    }

    static int IpChecksum(byte[] header, int len)
    {
        int sum = 0;
        for (int i = 0; i < len; i += 2)
            sum += (header[i] << 8) | (i + 1 < len ? header[i + 1] : 0);
        while (sum >> 16 != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (~sum) & 0xFFFF;
    }

    static byte[][] SplitPacket(byte[] packet, int pos)
    {
        pos = (pos / 8) * 8;
        if (pos < 8 || packet.Length < 28 + pos) return new[] { packet };

        int ihl = (packet[0] & 0xF) * 4;
        var ipHeader = new byte[20];
        Buffer.BlockCopy(packet, 0, ipHeader, 0, 20);
        var udpHeader = new byte[8];
        Buffer.BlockCopy(packet, 20, udpHeader, 0, 8);
        var payload = new byte[packet.Length - 28];
        Buffer.BlockCopy(packet, 28, payload, 0, payload.Length);

        var part1Payload = payload[..pos];
        var part2Payload = payload[pos..];
        if (part2Payload.Length == 0) return new[] { packet };

        // Fragment 1: MF=1
        var frag1 = new byte[20 + 8 + part1Payload.Length];
        Buffer.BlockCopy(ipHeader, 0, frag1, 0, 20);
        int total1 = frag1.Length;
        frag1[2] = (byte)(total1 >> 8); frag1[3] = (byte)(total1 & 0xFF);
        frag1[4] = ipHeader[4]; frag1[5] = ipHeader[5]; // same IP ID
        frag1[6] = 0x20; frag1[7] = 0x00; // MF=1, offset=0
        frag1[10] = 0; frag1[11] = 0;
        int ck1 = IpChecksum(frag1, 20);
        frag1[10] = (byte)(ck1 >> 8); frag1[11] = (byte)(ck1 & 0xFF);
        Buffer.BlockCopy(udpHeader, 0, frag1, 20, 8);
        Buffer.BlockCopy(part1Payload, 0, frag1, 28, part1Payload.Length);

        // Fragment 2: MF=0
        var frag2 = new byte[20 + part2Payload.Length];
        Buffer.BlockCopy(ipHeader, 0, frag2, 0, 20);
        int total2 = frag2.Length;
        frag2[2] = (byte)(total2 >> 8); frag2[3] = (byte)(total2 & 0xFF);
        frag2[4] = ipHeader[4]; frag2[5] = ipHeader[5];
        int fragOffset = (8 + pos) / 8;
        frag2[6] = (byte)(fragOffset >> 8); frag2[7] = (byte)(fragOffset & 0xFF);
        frag2[10] = 0; frag2[11] = 0;
        int ck2 = IpChecksum(frag2, 20);
        frag2[10] = (byte)(ck2 >> 8); frag2[11] = (byte)(ck2 & 0xFF);
        Buffer.BlockCopy(part2Payload, 0, frag2, 20, part2Payload.Length);

        return new[] { frag1, frag2 };
    }

    // ───────────────────────── WinDivert ─────────────────────────

    static void ExtractDpiFiles()
    {
        // Extract embedded WinDivert files
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        foreach (var name in names)
        {
            var fileName = name.Replace("kNot.data.dpi.", "").Replace("kNot.", "");
            var dest = Path.Combine(DpiDir, fileName);
            if (!File.Exists(dest))
            {
                try
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        Directory.CreateDirectory(DpiDir);
                        using var fs = File.Create(dest);
                        stream.CopyTo(fs);
                    }
                }
                catch { }
            }
        }
    }

    static byte[]? LoadDpiFile(string name)
    {
        var path = Path.Combine(DpiDir, name);
        if (File.Exists(path)) return File.ReadAllBytes(path);
        return null;
    }

    // ───────────────────────── Helpers ─────────────────────────

    static string? GetEndpoint()
    {
        if (!File.Exists(WorkConf)) return null;
        foreach (var line in File.ReadAllLines(WorkConf, Encoding.UTF8))
        {
            if (line.Trim().StartsWith("Endpoint"))
            {
                var parts = line.Split('=', 2);
                return parts.Length == 2 ? parts[1].Trim() : null;
            }
        }
        return null;
    }

    static List<string> CollectCidrs(string[] groups, bool useIPv6)
    {
        var result = new List<string>();
        var cacheFile = Path.Combine(CacheDir, "iplist-ru-groups.json");
        if (!File.Exists(cacheFile)) return result;

        var wanted = new HashSet<string>(groups);
        var cidrs = new HashSet<string>();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(cacheFile, UTF8NoBOM));
            // API format: { "domain": { "cidr4": [...], "cidr6": [...], "group": "discord" } }
            foreach (var domain in doc.RootElement.EnumerateObject())
            {
                var info = domain.Value;
                if (info.ValueKind != JsonValueKind.Object) continue;
                
                // Check group field
                if (!info.TryGetProperty("group", out var groupEl)) continue;
                var group = groupEl.GetString();
                if (group == null || !wanted.Contains(group)) continue;

                // Collect cidr4
                if (info.TryGetProperty("cidr4", out var cidr4) && cidr4.ValueKind == JsonValueKind.Array)
                    foreach (var c in cidr4.EnumerateArray())
                    {
                        var cidr = c.GetString();
                        if (cidr != null) cidrs.Add(cidr);
                    }

                // Collect cidr6
                if (useIPv6 && info.TryGetProperty("cidr6", out var cidr6) && cidr6.ValueKind == JsonValueKind.Array)
                    foreach (var c in cidr6.EnumerateArray())
                    {
                        var cidr = c.GetString();
                        if (cidr != null) cidrs.Add(cidr);
                    }
            }
            
            // Add DNS servers
            cidrs.Add("1.1.1.1/32");
            cidrs.Add("1.0.0.1/32");
            if (useIPv6)
            {
                cidrs.Add("2606:4700:4700::1111/128");
                cidrs.Add("2606:4700:4700::1001/128");
            }
        }
        catch { }
        return cidrs.ToList();
    }

    static bool PingHost(string host)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("ping", $"-n 1 -w 3000 {host}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            });
            p?.WaitForExit(10000);
            var text = p?.StandardOutput.ReadToEnd() ?? "";
            return text.Contains("TTL=") || text.Contains("TTL =");
        }
        catch { return false; }
    }

    static (bool ok, int ms, string? ip) ProbeService(string host)
    {
        string? ip = null;
        try
        {
            var addrs = Dns.GetHostAddresses(host);
            if (addrs.Length > 0) ip = addrs[0].ToString();
        }
        catch { return (false, 0, null); }

        if (ip != null)
        {
            // Warm-up ping
            PingHost(ip);
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var sock = new TcpClient();
                sock.Connect(ip, 443);
                using var ssl = new SslStream(sock.GetStream(), false, (_, _, _, _) => true, null);
                ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                sw.Stop();
                return (true, (int)sw.ElapsedMilliseconds, ip);
            }
            catch
            {
                if (attempt < 2) Thread.Sleep(2000);
            }
        }
        return (false, 0, ip);
    }

    static void Pause()
    {
        Console.WriteLine("\nНажмите Enter для продолжения...");
        Console.ReadLine();
    }

    static void SetupLogging()
    {
        // Nothing special - Log() writes to file
    }
}

// ───────────────────────── Models ─────────────────────────

record Strategy(string Id, string Name, string Desc, int Repeats, string? PayloadType, bool Split);

// ───────────────────────── WinDivert Interop ─────────────────────────

class WinDivertInterop
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr hModule);

    delegate IntPtr WinDivertOpenFunc(byte[] filter, int layer, sbyte priority, ulong flags);
    delegate bool WinDivertRecvFunc(IntPtr handle, byte[] packet, uint packetLen, out uint readLen, byte[] addr);
    delegate bool WinDivertSendFunc(IntPtr handle, byte[] packet, uint packetLen, ref uint sendLen, byte[] addr);
    delegate void WinDivertCloseFunc(IntPtr handle);

    WinDivertOpenFunc? _open;
    WinDivertRecvFunc? _recv;
    WinDivertSendFunc? _send;
    WinDivertCloseFunc? _close;
    IntPtr _handle;
    byte[] _addr = new byte[64];
    byte[] _buf = new byte[65535];

    public int LastError { get; private set; }

    public WinDivertInterop(string dpiDir)
    {
        var dllPath = Path.Combine(dpiDir, "WinDivert.dll");
        var hMod = LoadLibrary(dllPath);
        if (hMod == IntPtr.Zero) throw new DllNotFoundException($"WinDivert.dll not found: {dllPath}");

        _open = Marshal.GetDelegateForFunctionPointer<WinDivertOpenFunc>(GetProcAddress(hMod, "WinDivertOpen"));
        _recv = Marshal.GetDelegateForFunctionPointer<WinDivertRecvFunc>(GetProcAddress(hMod, "WinDivertRecv"));
        _send = Marshal.GetDelegateForFunctionPointer<WinDivertSendFunc>(GetProcAddress(hMod, "WinDivertSend"));
        _close = Marshal.GetDelegateForFunctionPointer<WinDivertCloseFunc>(GetProcAddress(hMod, "WinDivertClose"));
    }

    public bool Open(string filter)
    {
        var filterBytes = Encoding.ASCII.GetBytes(filter + "\0");
        _handle = _open!(filterBytes, 0, 0, 0);
        if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
        {
            LastError = Marshal.GetLastPInvokeError();
            return false;
        }
        return true;
    }

    public byte[]? Receive()
    {
        if (_handle == IntPtr.Zero) return null;
        if (_recv!(_handle, _buf, (uint)_buf.Length, out uint readLen, _addr))
            return _buf[..(int)readLen];
        return null;
    }

    public void Send(byte[] packet)
    {
        if (_handle == IntPtr.Zero) return;
        uint sendLen = (uint)packet.Length;
        _send!(_handle, packet, sendLen, ref sendLen, _addr);
    }

    public void Close()
    {
        if (_handle != IntPtr.Zero && _close != null)
        {
            _close(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

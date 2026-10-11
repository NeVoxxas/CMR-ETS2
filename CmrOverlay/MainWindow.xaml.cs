using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace CmrOverlay;

public partial class MainWindow : Window
{
    // =====================================================================
    //  Settings
    // =====================================================================
    const double FormW = 1654;
    const double FormH = 2339;
    const bool ShowBorders = false;
    const string TelemetryUrl = "http://localhost:25555/api/ets2/telemetry";

    InkCanvas signatureCanvas = null!; // InkCanvas for signature input

    // ---------- F8 ----------
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const int HotkeyId = 9001;
    const int ExitHotkeyId = 9002;                // Ctrl + Shift + F8 = išjungti programą
    const bool ExitWithGame = true;               // programa uždaroma, kai uždaromas ETS2

    void RegisterToggleKey()
    {
        var h = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(h).AddHook(WndProc);
        if (!RegisterHotKey(h, HotkeyId, 0, 0x77))               // 0x77 = F8
            Debug.WriteLine("F8 alr using");
        if (!RegisterHotKey(h, ExitHotkeyId, 0x0002 | 0x0004, 0x77))   // Ctrl(0x2) + Shift(0x4) + F8
            Debug.WriteLine("Ctrl+Shift+F8 alr using");
    }

    // Automatinis išjungimas: kai ETS2 procesas, kurį jau buvome pamatę, dingsta - uždarom programą
    bool seenGame;
    readonly DispatcherTimer gameWatch = new() { Interval = TimeSpan.FromSeconds(3) };

    void StartGameWatcher()
    {
        if (!ExitWithGame) return;
        gameWatch.Tick += (_, _) =>
        {
            bool running = Process.GetProcessesByName("eurotrucks2").Length > 0;
            if (running) seenGame = true;
            else if (seenGame) Application.Current.Shutdown();
        };
        gameWatch.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(h, HotkeyId);
            UnregisterHotKey(h, ExitHotkeyId);
        }
        catch { }
        base.OnClosed(e);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == ExitHotkeyId)
        {
            Application.Current.Shutdown();
            handled = true;
        }
        else if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            if (IsVisible) Hide();
            else { Show(); Activate(); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- Laukai ----------
    readonly Dictionary<string, TextBox> F = new();

    // City -> Country
    static readonly Dictionary<string, string> Country = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Vilnius"] = "LT",
        ["Kaunas"] = "LT",
        ["Klaipėda"] = "LT",
        ["Klaipeda"] = "LT",
        ["Šiauliai"] = "LT",
        ["Panevėžys"] = "LT",
        ["Riga"] = "LV",
        ["Tallinn"] = "EE",
        ["Warszawa"] = "PL",
        ["Gdańsk"] = "PL",
        ["Poznań"] = "PL",
        ["Łódź"] = "PL",
        ["Berlin"] = "DE",
        ["Hamburg"] = "DE",
        ["Köln"] = "DE",
        ["München"] = "DE",
        ["Hannover"] = "DE",
        ["Frankfurt"] = "DE",
        ["Dresden"] = "DE",
        ["Waldlaubersheim"] = "DE",
        ["Paris"] = "FR",
        ["Lyon"] = "FR",
        ["Amsterdam"] = "NL",
        ["Rotterdam"] = "NL",
        ["Bruxelles"] = "BE",
        ["Praha"] = "CZ",
        ["Wien"] = "AT"
    };

    // Mod folder
    static readonly string[] ModFolders =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     "Euro Truck Simulator 2", "mod"),
    };

    static readonly Dictionary<string, string> CountryIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["germany"] = "DE",
        ["france"] = "FR",
        ["uk"] = "GB",
        ["netherlands"] = "NL",
        ["belgium"] = "BE",
        ["luxembourg"] = "LU",
        ["switzerland"] = "CH",
        ["austria"] = "AT",
        ["italy"] = "IT",
        ["spain"] = "ES",
        ["portugal"] = "PT",
        ["poland"] = "PL",
        ["czech"] = "CZ",
        ["slovakia"] = "SK",
        ["hungary"] = "HU",
        ["denmark"] = "DK",
        ["sweden"] = "SE",
        ["norway"] = "NO",
        ["finland"] = "FI",
        ["estonia"] = "EE",
        ["latvia"] = "LV",
        ["lithuania"] = "LT",
        ["russia"] = "RU",
        ["turkey"] = "TR",
        ["bulgaria"] = "BG",
        ["romania"] = "RO",
        ["greece"] = "GR",
        ["croatia"] = "HR",
        ["slovenia"] = "SI",
        ["serbia"] = "RS",
        ["macedonia"] = "MK",
        ["albania"] = "AL",
        ["montenegro"] = "ME",
        ["kosovo"] = "XK",
        ["ukraine"] = "UA",
        ["belarus"] = "BY",
        ["moldova"] = "MD",
        ["ireland"] = "IE",
        ["bosnia"] = "BA"
    };

    // Miestų užkrovimas: 1) cities.json (šalia exe) 2) modai (.scs/.zip) iš Documents\...\mod
    static void LoadCities()
    {
        // 1) cities.json
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "cities.json");
            if (File.Exists(p))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p, Encoding.UTF8));
                if (d != null)
                    foreach (var kv in d) Country[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex) { Debug.WriteLine("cities.json: " + ex.Message); }

        try
        {
            var pi = Path.Combine(AppContext.BaseDirectory, "city_ids.json");
            if (File.Exists(pi))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(pi, Encoding.UTF8));
                if (d != null)
                    foreach (var kv in d) CityNameById[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex) { Debug.WriteLine("city_ids.json: " + ex.Message); }

        // 2) modai (papildo/perrašo)
        LoadModCities();

        // 3) paieška be diakritikų (Köln = Koln)
        foreach (var kv in Country.ToList())
            NormCountry.TryAdd(Norm(kv.Key), kv.Value);
    }

    static readonly Dictionary<string, string> NormCountry = new(StringComparer.OrdinalIgnoreCase);

    // miesto id (pvz. tm_waldlheim) -> rodomas pavadinimas (Waldlaubersheim)
    static readonly Dictionary<string, string> CityNameById = new(StringComparer.OrdinalIgnoreCase);

    static string Norm(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Replace('ł', 'l').Replace('Ł', 'L').Trim();
    }

    static void ParseCities(string text, string fallbackName)
    {
        var re = new Regex(@"city_data\s*:\s*city\.(\w+)\s*\{(.*?)\n\s*\}", RegexOptions.Singleline);
        foreach (Match m in re.Matches(text))
        {
            var id = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            var c = Regex.Match(body, @"\bcountry\s*:\s*""?(\w+)""?");
            if (!c.Success) continue;
            var cid = c.Groups[1].Value;
            var code = CountryIds.TryGetValue(cid, out var iso) ? iso : cid.ToUpperInvariant();
            var n = Regex.Match(body, @"\bcity_name\s*:\s*""([^""]+)""");
            if (n.Success && !n.Groups[1].Value.StartsWith("@@"))
            {
                Country[n.Groups[1].Value] = code;
                CityNameById[id] = n.Groups[1].Value;
            }
            Country[id] = code;
            Country[id.Replace('_', ' ')] = code;
        }
    }

    static void LoadModCities()
    {
        foreach (var dir in ModFolders)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".scs", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var zip = ZipFile.OpenRead(file);
                    foreach (var entry in zip.Entries)
                    {
                        var path = entry.FullName.Replace('\\', '/');
                        if (!path.StartsWith("def/city/", StringComparison.OrdinalIgnoreCase) ||
                            !path.EndsWith(".sii", StringComparison.OrdinalIgnoreCase)) continue;

                        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                        ParseCities(reader.ReadToEnd(), Path.GetFileNameWithoutExtension(path));
                    }
                }
                catch { /* ne ZIP arba sugadintas failas, praleidžiame */ }
            }
        }
    }

    static string C(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) return "";
        if (Country.TryGetValue(city, out var c)) return c;
        if (NormCountry.TryGetValue(Norm(city), out c)) return c;
        LogIfUnknown(city);
        return "";
    }

    static readonly HashSet<string> loggedUnknown = new(StringComparer.OrdinalIgnoreCase);
    static void LogIfUnknown(string city)
    {
        if (!loggedUnknown.Add(city)) return;
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "unknown_cities.txt"), city + Environment.NewLine, Encoding.UTF8); }
        catch { }
    }

    // Laukai, kuriuos pildai pats (telemetrija jų nelies) – išsaugomi settings.json
    static readonly string[] Saved =
    {
        "carrier", "truckPlate", "instructions", "payment", "special",
        "annexed", "cash", "marks", "packages", "packing", "statNo", "volume", "remarks", "driverName"
    };

    static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    const string CurrencyCode = "EUR";       // žaidimo valiuta (telemetrija jos nepateikia)
    DateTime? lastGameTime;

    static double N(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    static bool TryGameTime(JsonElement root, out DateTime t)
    {
        t = default;
        return root.TryGetProperty("game", out var g) &&
               DateTime.TryParse(S(g, "time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t);
    }

    void SetTime(string key, DateTime t)      // key: loadArr, loadDep, unloadArr, unloadDep
    {
        F[key + "H"].Text = t.Hour.ToString("00");
        F[key + "M"].Text = t.Minute.ToString("00");
    }

    // ---------- Telemetrija ----------
    static readonly HttpClient http = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool wasAttached;
    int attachedTicks;
    int detachedTicks;

    bool sentThisJob;

    // ---------- Tempimas (Alt + pelė) ----------
    TextBox? drag;
    Point dragOff;

    // ---------- Štampas (24 grafa) ----------
    Border stamp = null!;
    TextBlock stampText = null!;
    bool stampDrag;
    Point stampOff;

    // ---------- Vežėjo antspaudas (23 grafa) ----------
    static readonly bool AutoCarrierStamp = false;   // true = antspaudas uždedamas automatiškai kiekvienam kroviniui
    Border carrierStamp = null!;
    TextBlock carrierStampText = null!;

    public MainWindow()
    {
        InitializeComponent();

        var area = SystemParameters.WorkArea;
        Height = area.Height - 40;
        Width = Height * FormW / FormH + 20;   // išlaiko nuotraukos proporcijas
        Top = area.Top + 10;
        Left = area.Left + 10;

        BuildForm();
        BuildStamp();       // turi būti prieš StartTelemetry()
        BuildSignatureCanvas(); // turi būti prieš StartTelemetry()

        LoadSettings();
        foreach (var k in Saved)
            if (F.TryGetValue(k, out var tb)) tb.TextChanged += (_, _) => SaveSettings();

        // vairuotojo vardas: jei dar neįvestas, imame ETS2 profilio vardą
        if (string.IsNullOrWhiteSpace(F["driverName"].Text))
            F["driverName"].Text = DetectProfileName();

        // Ctrl + paspaudimas: parodo koordinates Output lange
        Form.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            var p = e.GetPosition(Form);
            Debug.WriteLine($"X={p.X:0}, Y={p.Y:0}");

            // Ctrl + paspaudimas 23 grafoje: vežėjo antspaudas
            if (p.X >= 540 && p.X <= 1075 && p.Y >= 1670 && p.Y <= 2000)
            {
                ShowCarrierStamp(p);
                e.Handled = true;
            }
        };

        // F9: bandomasis užpildymas be žaidimo
        KeyDown += (s, e) =>
        {
            if (e.Key != Key.F9) return;
            F["sender"].Text = "UAB Test\nVilnius, LT";
            F["consignee"].Text = "GmbH Test\nBerlin, DE";
            F["deliveryPlace"].Text = "Berlin";
            F["deliveryCountry"].Text = "DE";
            F["loadPlace"].Text = "Vilnius";
            F["loadCountry"].Text = "LT";
            F["loadDate"].Text = DateTime.Now.ToString("yyyy-MM-dd");
            F["goods"].Text = "Pallets";
            F["weight"].Text = "18000";
            F["trailerPlate"].Text = "XYZ789";
        };

        // Klavišai vietoje mygtukų (veikia, kai CMR langas turi fokusą)
        PreviewKeyDown += (s, e) =>
        {
            bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (ctrl && e.Key == Key.P) { Print_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { Save_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (ctrl && e.Key == Key.D) { _ = SendCmrToDiscordAsync(); e.Handled = true; } // <--- Štai čia pridėjome testą!
            else if (ctrl && e.Key == Key.Q) { Application.Current.Shutdown(); }
            else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }

        };


        LoadCities();
        StartTelemetry();
        RegisterToggleKey();
        StartGameWatcher();
    }



    // =====================================================================
    //  LAUKAI
    // =====================================================================

    void AddField(string key, double x, double y, double w, double h, bool multi = false)
    {
        var tb = new TextBox
        {
            Width = w,
            Height = h,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Red,
            BorderThickness = new Thickness(ShowBorders ? 1 : 0),
            FontSize = 22,
            FontFamily = new FontFamily("Arial"),
            Foreground = Brushes.DarkBlue,
            AcceptsReturn = multi,
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);

        tb.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt) return;
            drag = tb;
            var p = e.GetPosition(Form);
            dragOff = new Point(p.X - Canvas.GetLeft(tb), p.Y - Canvas.GetTop(tb));
            tb.CaptureMouse();
            e.Handled = true;
        };
        tb.PreviewMouseMove += (s, e) =>
        {
            if (drag != tb) return;
            var p = e.GetPosition(Form);
            Canvas.SetLeft(tb, p.X - dragOff.X);
            Canvas.SetTop(tb, p.Y - dragOff.Y);
        };
        tb.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (drag != tb) return;
            drag = null;
            tb.ReleaseMouseCapture();
            Debug.WriteLine($"AddField(\"{key}\", {Canvas.GetLeft(tb):0}, {Canvas.GetTop(tb):0}, {tb.Width:0}, {tb.Height:0}" + (multi ? ", true);" : ");"));
        };

        Form.Children.Add(tb);
        F[key] = tb;
    }

    void BuildForm()
    {
        AddField("sender", 115, 205, 680, 150, true);   // 1
        AddField("consignee", 113, 397, 680, 150, true);   // 2
        AddField("deliveryPlace", 209, 585, 600, 30);         // 3 Vieta
        AddField("deliveryCountry", 221, 618, 600, 30);         // 3 Šalis
        AddField("loadPlace", 204, 712, 600, 30);         // 4 Vieta
        AddField("loadCountry", 216, 742, 600, 30);         // 4 Šalis
        AddField("loadDate", 204, 773, 600, 30);         // 4 Data
        AddField("carrier", 824, 390, 700, 150, true);   // 16
        AddField("remarks", 824, 772, 700, 140, true);   // 18
        AddField("marks", 115, 971, 220, 280, true);   // 6
        AddField("packages", 345, 971, 190, 280, true);   // 7
        AddField("packing", 547, 972, 170, 280, true);   // 8
        AddField("goods", 732, 971, 235, 280, true);   // 9
        AddField("statNo", 994, 972, 170, 280, true);   // 10
        AddField("weight", 1174, 972, 170, 280, true);   // 11
        AddField("volume", 1357, 971, 190, 280, true);   // 12
        AddField("truckPlate", 115, 2069, 320, 40);         // 25 vilkikas
        AddField("trailerPlate", 464, 2069, 320, 40);         // 25 priekaba
        AddField("annexed", 115, 842, 680, 90, true);   // 5 pridedami dokumentai
        AddField("instructions", 120, 1324, 660, 95, true);   // 13 siuntėjo nurodymai
        AddField("cash", 153, 1544, 1400, 25);         // 14 grąžinimas
        AddField("payment", 113, 1614, 670, 90, true);    // 15 apmokėjimo sąlygos
        AddField("special", 815, 1619, 710, 90, true);    // 20 ypatingos sąlygos
        AddField("loadArrH", 275, 1757, 70, 26);   // 22 Atvykimas pakrovimui, val.
        AddField("loadArrM", 398, 1757, 85, 26);   // 22 min.
        AddField("loadDepH", 275, 1787, 70, 26);   // 22 Išvykimas, val.
        AddField("loadDepM", 398, 1787, 85, 26);   // 22 min.
        AddField("driverName", 1082, 1630, 450, 35); // Vairuotojo vardas (nerodomas ant CMR)
        F["driverName"].Visibility = Visibility.Collapsed;
        AddField("unloadArrH", 1252, 1758, 72, 26);   // 24 Atvykimas iškrovimui, val.
        AddField("unloadArrM", 1364, 1758, 90, 26);   // 24 min.
        AddField("unloadDepH", 1273, 1788, 72, 26);   // 24 Išvykimas, val.
        AddField("unloadDepM", 1389, 1788, 90, 26);  // 24 min.
        AddField("freight", 1018, 1322, 170, 28);   // 19 Pervežimo kaina (siuntėjas)
        AddField("currency", 1182, 1322, 175, 28);   // 19 Valiuta
        AddField("freightTotal", 1013, 1512, 170, 28);  // 19 Viso apmokėjimui
        AddField("currencyTotal", 1180, 1510, 175, 28);
        AddField("tariffKm", 219, 2195, 125, 35);   // 28 Tarifinis atstumas, km
    }

    // =====================================================================
    //  ŠTAMPAS
    // =====================================================================

    void BuildStamp()
    {
        stampText = new TextBlock
        {
            FontFamily = new FontFamily("Arial"),
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.RoyalBlue,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        stamp = new Border
        {
            Width = 360,
            Height = 140,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.RoyalBlue,
            BorderThickness = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Opacity = 0.85,
            Child = stampText,
            Visibility = Visibility.Hidden,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-6)
        };
        Canvas.SetLeft(stamp, 1130);
        Canvas.SetTop(stamp, 1830);

        // Alt + tempimas, kad sukalibruotum vietą
        stamp.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt) return;
            stampDrag = true;
            var p = e.GetPosition(Form);
            stampOff = new Point(p.X - Canvas.GetLeft(stamp), p.Y - Canvas.GetTop(stamp));
            stamp.CaptureMouse();
            e.Handled = true;
        };
        stamp.PreviewMouseMove += (s, e) =>
        {
            if (!stampDrag) return;
            var p = e.GetPosition(Form);
            Canvas.SetLeft(stamp, p.X - stampOff.X);
            Canvas.SetTop(stamp, p.Y - stampOff.Y);
        };
        stamp.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (!stampDrag) return;
            stampDrag = false;
            stamp.ReleaseMouseCapture();
            Debug.WriteLine($"Stamp: {Canvas.GetLeft(stamp):0}, {Canvas.GetTop(stamp):0}");
        };

        // Dešinys paspaudimas ant štampo jį nuima
        stamp.MouseRightButtonDown += (s, e) => stamp.Visibility = Visibility.Hidden;

        // Nematomas spaudžiamas plotas ant 24 grafos: kairys paspaudimas deda štampą
        var area24 = new System.Windows.Shapes.Rectangle
        {
            Width = 450,
            Height = 300,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(area24, 1082);
        Canvas.SetTop(area24, 1682);
        area24.MouseLeftButtonDown += (s, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Alt) return;
            var p = e.GetPosition(Form);
            Canvas.SetLeft(stamp, p.X - stamp.Width / 2);   // štampas centruojamas ties paspaudimu
            Canvas.SetTop(stamp, p.Y - stamp.Height / 2);
            ShowStamp();
            if (lastGameTime is DateTime t) SetTime("unloadDep", t);   // išvykimas iš iškrovimo
            if (!sentThisJob) { sentThisJob = true; _ = SendCmrToDiscordAsync(); }
        };

        Form.Children.Add(area24);   // pirmiau, kad štampas liktų viršuje
        Form.Children.Add(stamp);
    }

    void BuildSignatureCanvas()
    {
        // Sukuriame piešimo lauką (InkCanvas) 23 lauko zonoje
        signatureCanvas = new InkCanvas
        {
            Width = 450,       // Pritaikyta pagal 23 lauko plotį
            Height = 160,      // Pritaikyta pagal 23 lauko aukštį
            Background = Brushes.Transparent,
            EditingMode = InkCanvasEditingMode.Ink
        };

        var drawingAttributes = new System.Windows.Ink.DrawingAttributes
        {
            Color = Colors.DarkBlue,
            Width = 3,
            Height = 3
        };
        signatureCanvas.DefaultDrawingAttributes = drawingAttributes;

        // 23 lauko pozicija ant kanco (X ir Y koordinates patikslink pagal savo formos dydį)
        // Orientacinė 23 lauko vieta ant 1654x2339 lapo:
        Canvas.SetLeft(signatureCanvas, 580);
        Canvas.SetTop(signatureCanvas, 1780);

        // Dešinysis paspaudimas išvalo parašą (jei norisi perrašyti iš naujo)
        // Ctrl + dešinysis paspaudimas nuima vežėjo antspaudą
        signatureCanvas.PreviewMouseRightButtonDown += (s, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control) carrierStamp.Visibility = Visibility.Hidden;
            else signatureCanvas.Strokes.Clear();
            e.Handled = true;
        };

        // Ctrl + kairys paspaudimas 23 grafoje deda vežėjo antspaudą (paprastas tempimas - parašas)
        signatureCanvas.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            var p = e.GetPosition(Form);
            ShowCarrierStamp(p);
            e.Handled = true;
        };

        // Vežėjo antspaudas: po parašu, kad parašas būtų ant antspaudo
        carrierStampText = new TextBlock
        {
            FontFamily = new FontFamily("Arial"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.RoyalBlue,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        carrierStamp = new Border
        {
            Width = 420,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.RoyalBlue,
            BorderThickness = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Opacity = 0.85,
            Child = carrierStampText,
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(4)
        };
        Canvas.SetLeft(carrierStamp, 595);
        Canvas.SetTop(carrierStamp, 1760);

        Form.Children.Add(carrierStamp);
        Form.Children.Add(signatureCanvas);
    }

    // Vežėjo antspaudas: įmonės pavadinimas ir HQ miestas iš žaidimo išsaugojimo.
    // Jei nepavyksta nuskaityti - imamas 16 laukas (vežėjas). center = kur centruoti; null = numatyta vieta 23 grafoje
    void ShowCarrierStamp(Point? center = null)
    {
        string text = "";
        var (company, hq) = ReadCompanyFromSave();
        if (!string.IsNullOrWhiteSpace(company))
        {
            text = company;
            if (!string.IsNullOrWhiteSpace(hq))
            {
                var cc = C(hq);
                text += "\n" + hq + (cc != "" ? ", " + cc : "");
            }
        }
        if (text == "")
        {
            var lines = F["carrier"].Text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                         .Select(l => l.Trim()).Where(l => l.Length > 0);
            text = string.Join("\n", lines);
        }
        carrierStampText.Text = text != "" ? text : "ĮMONĖS NERASTA\n(įrašyk 16 lauke)";
        carrierStamp.Visibility = Visibility.Visible;
        carrierStamp.Measure(new Size(carrierStamp.Width, double.PositiveInfinity));
        double h = carrierStamp.DesiredSize.Height;
        double cx = center?.X ?? 805;
        double cy = center?.Y ?? 1850;
        Canvas.SetLeft(carrierStamp, cx - carrierStamp.Width / 2);
        Canvas.SetTop(carrierStamp, cy - h / 2);
    }

    void ShowStamp()
    {
        var lines = F["consignee"].Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string name = lines.Length > 0 ? lines[0].Trim() : "";
        string time = (lastGameTime ?? DateTime.Now).ToString("HH:mm");

        stampText.Text = $"{name}\n{F["deliveryPlace"].Text}\n{DateTime.Now:yyyy-MM-dd} {time}";
        stamp.Visibility = Visibility.Visible;
    }

    // =====================================================================
    //  SPAUSDINIMAS / IŠSAUGOJIMAS (Ctrl+P / Ctrl+S)
    // =====================================================================

    void Print_Click(object s, RoutedEventArgs e)
    {
        Form.UpdateLayout();
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintVisual(Form, "CMR");
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        Form.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)FormW, (int)FormH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Form);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var path = Path.Combine(AppContext.BaseDirectory, $"CMR_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        using var fs = File.Create(path);
        enc.Save(fs);
        Debug.WriteLine("Išsaugota: " + path);
    }

    // =====================================================================
    //  TELEMETRIJA
    // =====================================================================

    void StartTelemetry()
    {
        timer.Tick += async (_, _) => await Poll();
        timer.Start();
    }

    static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    async Task Poll()
    {
        try
        {
            var json = await http.GetStringAsync(TelemetryUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var trailer = root.GetProperty("trailer");
            var job = root.GetProperty("job");
            bool attached = trailer.GetProperty("attached").GetBoolean();

            bool hasTime = TryGameTime(root, out var gameTime);
            if (hasTime) lastGameTime = gameTime;

            if (attached) { attachedTicks++; detachedTicks = 0; }
            else { detachedTicks++; attachedTicks = 0; }

            if (attachedTicks == 2 && !wasAttached)
            {
                wasAttached = true;

                string srcCity = S(job, "sourceCity");
                string dstCity = S(job, "destinationCity");

                F["sender"].Text = $"{S(job, "sourceCompany")}\n{srcCity}";
                F["consignee"].Text = $"{S(job, "destinationCompany")}\n{dstCity}";
                F["deliveryPlace"].Text = dstCity;
                F["deliveryCountry"].Text = C(dstCity);
                F["loadPlace"].Text = srcCity;
                F["loadCountry"].Text = C(srcCity);
                F["loadDate"].Text = DateTime.Now.ToString("yyyy-MM-dd");
                F["goods"].Text = S(trailer, "name");
                F["weight"].Text = Math.Round(N(trailer, "mass")).ToString();
                F["trailerPlate"].Text = "";

                // frachtas ir valiuta
                long income = (long)N(job, "income");
                string money = income > 0 ? income.ToString() : "";
                F["freight"].Text = money;
                F["freightTotal"].Text = money;
                F["currency"].Text = income > 0 ? CurrencyCode : "";
                F["currencyTotal"].Text = income > 0 ? CurrencyCode : "";

                // atstumas (GPS maršruto ilgis metrais -> km)
                double meters = root.TryGetProperty("navigation", out var nav) ? N(nav, "estimatedDistance") : 0;
                F["tariffKm"].Text = meters > 0 ? Math.Round(meters / 1000).ToString() : "";

                // laikai: naujas krovinys
                foreach (var k in new[] { "loadArrH", "loadArrM", "unloadArrH", "unloadArrM", "unloadDepH", "unloadDepM" })
                    F[k].Text = "";
                if (hasTime) SetTime("loadDep", gameTime);   // išvykimas iš pakrovimo

                sentThisJob = false;                   // naujas krovinys: leidžiame siųsti į Discord iš naujo
                stamp.Visibility = Visibility.Hidden;
                carrierStamp.Visibility = Visibility.Hidden;
                if (AutoCarrierStamp) ShowCarrierStamp();
                Show();
                Activate();
            }
            else if (detachedTicks == 2 && wasAttached)
            {
                wasAttached = false;
                if (hasTime) SetTime("unloadArr", gameTime);   // atvykimas iškrovimui
                Show();
                Activate();
            }
        }
        catch (HttpRequestException)
        {
            // serveris neveikia
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Telemetrija: " + ex.Message);
        }
    }

    // =====================================================================
    //  DISCORD WEBHOOK
    // =====================================================================

    static string V(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

    async Task SendCmrToDiscordAsync()
    {
        string webhookUrl = "";
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("discordWebhookUrl", out var val))
                    webhookUrl = val.GetString() ?? "";
            }
        }
        catch { }

        if (string.IsNullOrEmpty(webhookUrl)) return;

        try
        {
            Form.UpdateLayout();

            // 1. CMR nuotrauka
            var renderBitmap = new RenderTargetBitmap((int)Form.Width, (int)Form.Height, 96d, 96d, PixelFormats.Pbgra32);
            renderBitmap.Render(Form);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

            byte[] photoBytes;
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                photoBytes = ms.ToArray();
            }

            // 2. Embed laukai (tušti laukai Discord'e neleidžiami, todėl V() įrašo „—")
            string OneLine(string key) => V(F[key].Text.Replace("\r", "").Replace("\n", ", "));
            string route = $"{V(F["loadPlace"].Text)} → {V(F["deliveryPlace"].Text)}";
            string driver = V(F.TryGetValue("driverName", out var dBox) ? dBox.Text : "");
            string cargo = string.IsNullOrWhiteSpace(F["goods"].Text)
                ? "—"
                : $"{F["goods"].Text.Trim()} ({V(F["weight"].Text)} kg)";
            string km = string.IsNullOrWhiteSpace(F["tariffKm"].Text) ? "—" : $"{F["tariffKm"].Text} km";
            string freight = string.IsNullOrWhiteSpace(F["freight"].Text)
                ? "—"
                : $"{F["freight"].Text} {F["currency"].Text}".Trim();

            var payload = new
            {
                embeds = new[]
                {
                    new
                    {
                        title = "🚛 Naujas reisas pristatytas!",
                        description = $"**{route}**",
                        color = 0x2F80ED,
                        fields = new[]
                        {
                            new { name = "👤 Vairuotojas", value = driver, inline = true },
                            new { name = "📦 Krovinys", value = cargo, inline = true },
                            new { name = "📏 Atstumas", value = km, inline = true },
                            new { name = "💶 Frachtas", value = freight, inline = true },
                            new { name = "📤 Siuntėjas", value = OneLine("sender"), inline = true },
                            new { name = "📥 Gavėjas", value = OneLine("consignee"), inline = true },
                        },
                        image = new { url = "attachment://cmr_waybill.png" },
                        footer = new { text = "CMR Overlay" },
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
                    }
                }
            };

            // 3. Siunčiame: embed kaip payload_json + nuotrauka kaip priedas
            using var formData = new MultipartFormDataContent();
            formData.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");

            var imageContent = new ByteArrayContent(photoBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            formData.Add(imageContent, "files[0]", "cmr_waybill.png");

            HttpResponseMessage response = await http.PostAsync(webhookUrl, formData);
            if (response.IsSuccessStatusCode)
                Debug.WriteLine("CMR sėkmingai išsiųstas į Discord!");
            else
                Debug.WriteLine($"Discord klaida {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Klaida siunčiant į Discord: " + ex.Message);
        }
    }

    // =====================================================================
    //  VAIRUOTOJO VARDAS (iš ETS2 profilio aplanko pavadinimo)
    // =====================================================================

    // =====================================================================
    //  ĮMONĖ IŠ ŽAIDIMO IŠSAUGOJIMO (profile.sii + game.sii)
    // =====================================================================

    // Žinomas ETS2/ATS SII šifravimo raktas (AES-256-CBC, po to zlib)
    static readonly byte[] SiiKey =
    {
        0x2a, 0x5f, 0xcb, 0x17, 0x91, 0xd2, 0x2f, 0xb6, 0x02, 0x45, 0xb3, 0xd8, 0x36, 0x9e, 0xd0, 0xb2,
        0xc2, 0x73, 0x71, 0x56, 0x3f, 0xbf, 0x1f, 0x3c, 0x9e, 0xdf, 0x6b, 0x11, 0x82, 0x5a, 0x5d, 0x0a
    };

    static DirectoryInfo? FindProfileDir()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Euro Truck Simulator 2");
        DirectoryInfo? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var sub in new[] { "profiles", "steam_profiles" })
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var d in new DirectoryInfo(dir).GetDirectories())
            {
                var sii = Path.Combine(d.FullName, "profile.sii");
                var time = File.Exists(sii) ? File.GetLastWriteTimeUtc(sii) : d.LastWriteTimeUtc;
                if (time > bestTime) { bestTime = time; best = d; }
            }
        }
        return best;
    }

    // Nuskaito .sii: tekstinį arba užšifruotą (ScsC). Dvejetainio (BSII) formato neskaito.
    static string ReadSii(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 8) return "";

            if (b[0] == 'S' && b[1] == 'i' && b[2] == 'i' && b[3] == 'N')
                return Encoding.UTF8.GetString(b);

            if (b[0] == 'S' && b[1] == 'c' && b[2] == 's' && b[3] == 'C')
            {
                // antraštė: 4 parašas, 32 HMAC, 16 IV, 4 dydis, toliau duomenys
                var iv = b.AsSpan(36, 16).ToArray();
                const int dataStart = 56;
                int len = (b.Length - dataStart) / 16 * 16;

                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.Key = SiiKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using var dec = aes.CreateDecryptor();
                var plain = dec.TransformFinalBlock(b, dataStart, len);

                using var zs = new ZLibStream(new MemoryStream(plain), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                zs.CopyTo(ms);
                var o = ms.ToArray();
                if (o.Length > 4 && o[0] == 'S' && o[1] == 'i' && o[2] == 'i' && o[3] == 'N')
                    return Encoding.UTF8.GetString(o);
                if (IsBsii(o))
                    return BsiiToText(o);

                Debug.WriteLine("SII: nežinomas turinys po iššifravimo (pirmi baitai: " + BitConverter.ToString(o, 0, Math.Min(8, o.Length)) + "): " + path);
                return "";
            }

            if (IsBsii(b))
                return BsiiToText(b);

            Debug.WriteLine("SII: nežinomas formatas: " + path);
        }
        catch (Exception ex) { Debug.WriteLine("SII: " + ex.Message); }
        return "";
    }

    // ---------- BSII (dvejetainis SII) skaitytuvas ----------
    // Išsitraukia tik reikiamus laukus ir grąžina juos kaip tekstą: laukas: "reikšmė"
    static string BsiiToText(byte[] data)
    {
        var wanted = new[] { "company_name", "hq_city" };
        var found = new Dictionary<string, string>();
        try
        {
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            r.ReadUInt32();                       // "SIIB"
            uint ver = r.ReadUInt32();            // 1, 2, 3
            var structs = new Dictionary<uint, List<(uint type, string name)>>();

            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                uint bt = r.ReadUInt32();
                if (bt == 0)
                {
                    if (r.ReadByte() == 0) break;             // struktūrų pabaiga
                    uint id = r.ReadUInt32();
                    BsiiString(r);                            // struktūros pavadinimas
                    var segs = new List<(uint, string)>();
                    while (true)
                    {
                        uint t = r.ReadUInt32();
                        if (t == 0) break;
                        string fn = BsiiString(r);
                        if (t == 0x37)                        // enum lentelė
                        {
                            uint n = r.ReadUInt32();
                            for (uint i = 0; i < n; i++) { r.ReadUInt32(); BsiiString(r); }
                        }
                        segs.Add((t, fn));
                    }
                    structs[id] = segs;
                }
                else
                {
                    if (!structs.TryGetValue(bt, out var segs)) break;
                    BsiiId(r);                                // vieneto id
                    foreach (var (t, fn) in segs)
                    {
                        string? v = BsiiValue(r, t, ver);
                        if (v != null && (fn.Contains("company") || fn == "profile_name"))
                            Debug.WriteLine($"BSII laukas {fn} = '{v}'");
                        if (v != null && Array.IndexOf(wanted, fn) >= 0 && !found.ContainsKey(fn))
                            found[fn] = v;
                    }
                    if (found.Count == wanted.Length) break;
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine("BSII: " + ex.Message); }

        var sb = new StringBuilder();
        if (found.TryGetValue("company_name", out var cn)) sb.AppendLine($"company_name: \"{cn}\"");
        if (found.TryGetValue("hq_city", out var hc)) sb.AppendLine($"hq_city: city.{hc}");
        return sb.ToString();
    }

    // dvejetainio SII žymuo: "BSII" (kai kur užrašomas ir kaip "SIIB")
    static bool IsBsii(byte[] d) =>
        d.Length > 8 &&
        ((d[0] == 'B' && d[1] == 'S' && d[2] == 'I' && d[3] == 'I') ||
         (d[0] == 'S' && d[1] == 'I' && d[2] == 'I' && d[3] == 'B'));

    static string BsiiString(BinaryReader r)
    {
        uint len = r.ReadUInt32();
        return Encoding.UTF8.GetString(r.ReadBytes((int)len));
    }

    // Užkoduotas žetonas (base-38): 0-9, a-z, _
    static string BsiiToken(ulong v)
    {
        const string table = "0123456789abcdefghijklmnopqrstuvwxyz_";
        var sb = new StringBuilder();
        while (v != 0)
        {
            int i = (int)(v % 38);
            v /= 38;
            if (i == 0) break;
            sb.Append(table[i - 1]);
        }
        return sb.ToString();
    }

    static void BsiiId(BinaryReader r)
    {
        byte parts = r.ReadByte();
        if (parts == 0xFF) r.ReadUInt64();                    // be pavadinimo: 64 bitų adresas
        else for (int i = 0; i < parts; i++) r.ReadUInt64();  // pavadinimo dalys
    }

    // Nuskaito vieną reikšmę; grąžina tekstą tik string/token tipams, kitus praleidžia
    static string? BsiiValue(BinaryReader r, uint t, uint ver)
    {
        if ((t & 1) == 0)                                     // visi masyvų tipai yra lyginiai
        {
            uint n = r.ReadUInt32();
            for (uint i = 0; i < n; i++) BsiiValue(r, t - 1, ver);
            return null;
        }
        switch (t)
        {
            case 0x01: return BsiiString(r);
            case 0x03: return BsiiToken(r.ReadUInt64());
            case 0x05: case 0x25: case 0x27: case 0x2F: case 0x37: r.ReadUInt32(); return null;
            case 0x07: r.ReadBytes(8); return null;
            case 0x09: case 0x11: r.ReadBytes(12); return null;
            case 0x17: r.ReadBytes(16); return null;
            case 0x19:
                if (ver < 2) throw new InvalidDataException("BSII v1 vector8");
                r.ReadBytes(32); return null;
            case 0x29: case 0x2B: r.ReadUInt16(); return null;
            case 0x31: case 0x33: r.ReadUInt64(); return null;
            case 0x35: r.ReadByte(); return null;
            case 0x39: case 0x3B: case 0x3D: BsiiId(r); return null;
            default: throw new InvalidDataException($"BSII: nežinomas tipas 0x{t:X2}");
        }
    }

    // Lauko reikšmė iš SII teksto: su kabutėmis arba be (iki eilutės pabaigos)
    static string SiiField(string text, string name)
    {
        var m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(name) + @"\s*:[ \t]*(.*?)[ \t]*\r?$");
        if (!m.Success) return "";
        var v = m.Groups[1].Value.Trim();
        if (v.Length >= 2 && v.StartsWith("\"") && v.EndsWith("\"")) v = v.Substring(1, v.Length - 2);
        return v.Trim();
    }

    // (įmonės pavadinimas, HQ miestas) iš aktyvaus profilio
    static (string company, string hq) ReadCompanyFromSave()
    {
        try
        {
            var dir = FindProfileDir();
            if (dir == null) return ("", "");

            string company = "";
            var prof = ReadSii(Path.Combine(dir.FullName, "profile.sii"));
            company = SiiField(prof, "company_name");

            string hq = "";
            var saveRoot = Path.Combine(dir.FullName, "save");
            if (Directory.Exists(saveRoot))
            {
                var newest = new DirectoryInfo(saveRoot).GetDirectories()
                    .Where(d => File.Exists(Path.Combine(d.FullName, "game.sii")))
                    .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "game.sii")))
                    .FirstOrDefault();
                if (newest != null)
                {
                    var game = ReadSii(Path.Combine(newest.FullName, "game.sii"));
                    var hm = Regex.Match(game, @"hq_city\s*:\s*(?:city\.)?(\w+)");
                    if (hm.Success)
                    {
                        var hqId = hm.Groups[1].Value;
                        hq = CityNameById.TryGetValue(hqId, out var nm)
                            ? nm
                            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(hqId.Replace('_', ' '));
                    }
                    if (company == "") company = SiiField(game, "company_name");
                }
            }
            Debug.WriteLine($"Įmonė iš išsaugojimo: '{company}', HQ: '{hq}'");
            if (company == "")
            {
                var i = prof.IndexOf("company_name", StringComparison.Ordinal);
                Debug.WriteLine(i >= 0
                    ? "profile.sii: " + prof.Substring(i, Math.Min(80, prof.Length - i)).Replace("\r", "").Replace("\n", " | ")
                    : "profile.sii: laukas company_name nerastas (ilgis " + prof.Length + ")");
            }
            return (company, hq);
        }
        catch (Exception ex) { Debug.WriteLine("ReadCompanyFromSave: " + ex.Message); return ("", ""); }
    }

    static string DetectProfileName()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Euro Truck Simulator 2");

            DirectoryInfo? best = null;
            DateTime bestTime = DateTime.MinValue;

            // "profiles" (vietiniai) ir "steam_profiles" (Steam Cloud)
            foreach (var sub in new[] { "profiles", "steam_profiles" })
            {
                var dir = Path.Combine(root, sub);
                if (!Directory.Exists(dir)) continue;

                foreach (var d in new DirectoryInfo(dir).GetDirectories())
                {
                    var sii = Path.Combine(d.FullName, "profile.sii");
                    var time = File.Exists(sii) ? File.GetLastWriteTimeUtc(sii) : d.LastWriteTimeUtc;
                    if (time > bestTime) { bestTime = time; best = d; }
                }
            }

            if (best == null) return "";

            // aplanko pavadinimas = profilio vardas UTF-8 baitais, užrašytais hex
            var hex = best.Name;
            if (hex.Length % 2 != 0 || !Regex.IsMatch(hex, "^[0-9A-Fa-f]+$")) return "";

            return Encoding.UTF8.GetString(Convert.FromHexString(hex));
        }
        catch { return ""; }
    }

    // =====================================================================
    //  SAVE / LOAD
    // =====================================================================

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath));
            if (d == null) return;
            foreach (var (k, v) in d)
                if (F.TryGetValue(k, out var tb)) tb.Text = v;
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            var d = new Dictionary<string, string>();
            if (File.Exists(SettingsPath))
                d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath)) ?? d;

            foreach (var k in Saved)
                if (F.TryGetValue(k, out var tb)) d[k] = tb.Text;

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(d));
        }
        catch { }
    }
}
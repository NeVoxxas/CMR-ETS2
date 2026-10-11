using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
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
    //  NUSTATYMAI
    // =====================================================================
    const double FormW = 1654;          // cmr.png plotis (px)
    const double FormH = 2339;          // cmr.png aukštis (px)
    const bool ShowBorders = false;     // kalibravimui true, galutinei versijai false
    const string TelemetryUrl = "http://localhost:25555/api/ets2/telemetry";

    // ---------- Globalus klavišas (F8) ----------
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
    const int HotkeyId = 9001;

    void RegisterToggleKey()
    {
        var h = new WindowInteropHelper(this).EnsureHandle();   // veikia ir kai langas paslėptas
        HwndSource.FromHwnd(h).AddHook(WndProc);
        if (!RegisterHotKey(h, HotkeyId, 0, 0x77))               // 0x77 = F8
            Debug.WriteLine("F8 klavišas užimtas, pakeisk 0x77");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            if (IsVisible) Hide();
            else { Show(); Activate(); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- Laukai ----------
    readonly Dictionary<string, TextBox> F = new();

    // Miestas -> šalis (pildyk pagal poreikį)
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

    // modų aplankai, kuriuose ieškoma miestų (pakeisk pagal save)
    static readonly string[] ModFolders =
    {
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                 "Euro Truck Simulator 2", "mod"),
    // jei modą turi per Steam Workshop, pridėk ir šitą:
    // @"C:\Program Files (x86)\Steam\steamapps\workshop\content\227300",
};

    // šalies id iš modo -> kodas (nežinomas id tampa didžiosiomis raidėmis)
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
        ["russia"] = "RU"
    };

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

                        using var reader = new StreamReader(entry.Open());
                        var text = reader.ReadToEnd();

                        var ctry = Regex.Match(text, "country:\\s*\"?([\\w.]+)\"?");
                        if (!ctry.Success) continue;
                        var id = ctry.Groups[1].Value;
                        var code = CountryIds.TryGetValue(id, out var c) ? c : id.ToUpperInvariant();

                        var nameMatch = Regex.Match(text, "city_name:\\s*\"([^\"]+)\"");
                        var name = nameMatch.Success && !nameMatch.Groups[1].Value.StartsWith("@@")
                            ? nameMatch.Groups[1].Value
                            : Path.GetFileNameWithoutExtension(path).Replace('_', ' ');

                        Country[name] = code;
                    }
                }
                catch { /* ne ZIP arba sugadintas failas, praleidžiame */ }
            }
        }
    }

    static string C(string city) => Country.TryGetValue(city, out var c) ? c : "";

    // Laukai, kuriuos pildai pats (telemetrija jų nelies) – išsaugomi settings.json
    static readonly string[] Saved =
    {
        "carrier", "truckPlate", "instructions", "payment", "special",
        "annexed", "cash", "marks", "packages", "packing", "statNo", "volume", "remarks"
    };

    static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    // ---------- Telemetrija ----------
    static readonly HttpClient http = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool wasAttached;
    int attachedTicks;
    int detachedTicks;

    // ---------- Tempimas (Alt + pelė) ----------
    TextBox? drag;
    Point dragOff;

    // ---------- Štampas (24 grafa) ----------
    Border stamp = null!;
    TextBlock stampText = null!;
    bool stampDrag;
    Point stampOff;

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

        LoadSettings();
        foreach (var k in Saved)
            if (F.TryGetValue(k, out var tb)) tb.TextChanged += (_, _) => SaveSettings();

        // Ctrl + paspaudimas: parodo koordinates Output lange
        Form.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            var p = e.GetPosition(Form);
            Debug.WriteLine($"X={p.X:0}, Y={p.Y:0}");
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
            else if (ctrl && e.Key == Key.Q) { Application.Current.Shutdown(); }
            else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };

        StartTelemetry();
        RegisterToggleKey();
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
            Height = 100,
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
        };

        Form.Children.Add(area24);   // pirmiau, kad štampas liktų viršuje
        Form.Children.Add(stamp);
    }

    void ShowStamp()
    {
        var lines = F["consignee"].Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string name = lines.Length > 0 ? lines[0].Trim() : "";
        stampText.Text = $"{name}\n{F["deliveryPlace"].Text}\n{DateTime.Now:yyyy-MM-dd HH:mm}";
        stamp.Visibility = Visibility.Visible;
    }

    // =====================================================================
    //  SPAUSDINIMAS / IŠSAUGOJIMAS (Ctrl+P / Ctrl+S)
    // =====================================================================

    void Print_Click(object s, RoutedEventArgs e)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintVisual(Form, "CMR");
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
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

            // skaičiuojame, kiek tikrinimų iš eilės būsena išliko (~1 s sulaikymas)
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
                F["weight"].Text = Math.Round(trailer.GetProperty("mass").GetDouble()).ToString();
                F["trailerPlate"].Text = "";           // naujai priekabai numerį įvesi ranka

                stamp.Visibility = Visibility.Hidden;  // naujas krovinys: be senojo štampo
                Show();
                Activate();
            }
            else if (detachedTicks == 2 && wasAttached)
            {
                wasAttached = false;

                // laukų nekeičiame: atkabinus užsakymo duomenų žaidimas gali nebeduoti
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
            foreach (var k in Saved)
                if (F.TryGetValue(k, out var tb)) d[k] = tb.Text;
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(d));
        }
        catch { }
    }
}
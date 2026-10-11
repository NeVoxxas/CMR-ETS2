using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace CmrOverlay;

public partial class MainWindow : Window
{
    readonly Dictionary<string, TextBox> F = new();

    public MainWindow()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Height = area.Height - 40;
        Width = Height * 1654.0 / 2339.0 + 20;   // išlaiko nuotraukos proporcijas
        Top = area.Top + 10;
        Left = area.Left + 10;
        BuildForm();
        Form.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            var p = e.GetPosition(Form);
            System.Diagnostics.Debug.WriteLine($"X={p.X:0}, Y={p.Y:0}");
        };
        StartTelemetry();
    }

    void AddField(string key, double x, double y, double w, double h, bool multi = false)
    {
        var tb = new TextBox
        {
            Width = w,
            Height = h,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Red,
            BorderThickness = new Thickness(1),
            FontSize = 22,
            FontFamily = new FontFamily("Arial"),
            Foreground = Brushes.DarkBlue,
            AcceptsReturn = multi,
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        Form.Children.Add(tb);
        F[key] = tb;
    }

    void BuildForm()
    {
        // Koordinatės apytikrės, reikės sukalibruoti pagal tavo nuotrauką
        AddField("sender", 115, 195, 680, 150, true);   // 1
        AddField("consignee", 115, 385, 680, 150, true);   // 2
        AddField("deliveryPlace", 190, 585, 600, 30);         // 3 Vieta
        AddField("deliveryCountry", 190, 615, 600, 30);         // 3 Šalis
        AddField("loadPlace", 190, 710, 600, 30);         // 4 Vieta
        AddField("loadCountry", 190, 740, 600, 30);         // 4 Šalis
        AddField("loadDate", 190, 770, 600, 30);         // 4 Data
        AddField("carrier", 810, 385, 700, 150, true);   // 16
        AddField("remarks", 810, 765, 700, 140, true);   // 18
        AddField("marks", 115, 955, 220, 280, true);   // 6
        AddField("packages", 345, 955, 190, 280, true);   // 7
        AddField("packing", 545, 955, 170, 280, true);   // 8
        AddField("goods", 730, 955, 235, 280, true);   // 9
        AddField("statNo", 975, 955, 170, 280, true);   // 10
        AddField("weight", 1155, 955, 170, 280, true);   // 11
        AddField("volume", 1335, 955, 190, 280, true);   // 12
        AddField("truckPlate", 115, 2075, 320, 40);         // 25 vilkikas
        AddField("trailerPlate", 450, 2075, 320, 40);         // 25 priekaba
    }

    void Close_Click(object s, RoutedEventArgs e) => Hide();

    void Print_Click(object s, RoutedEventArgs e)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintVisual(Form, "CMR");
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        var rtb = new RenderTargetBitmap(1654, 2339, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Form);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create($"CMR_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        enc.Save(fs);
    }

    static readonly HttpClient http = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool wasAttached;
    int attachedTicks;

    void StartTelemetry()
    {
        timer.Tick += async (_, _) => await Poll();
        timer.Start();
    }

    async System.Threading.Tasks.Task Poll()
    {
        try
        {
            var json = await http.GetStringAsync("http://localhost:25555/api/ets2/telemetry");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var trailer = root.GetProperty("trailer");
            var job = root.GetProperty("job");
            bool attached = trailer.GetProperty("attached").GetBoolean();

            attachedTicks = attached ? attachedTicks + 1 : 0;

            // rodome, kai priekaba išlieka prikabinta ~1 s
            if (attachedTicks == 2 && !wasAttached)
            {
                string S(JsonElement e, string n) => e.GetProperty(n).GetString() ?? "";
                F["sender"].Text = $"{S(job, "sourceCompany")}\n{S(job, "sourceCity")}";
                F["consignee"].Text = $"{S(job, "destinationCompany")}\n{S(job, "destinationCity")}";
                F["deliveryPlace"].Text = S(job, "destinationCity");
                F["loadPlace"].Text = S(job, "sourceCity");
                F["loadDate"].Text = DateTime.Now.ToString("yyyy-MM-dd");
                F["goods"].Text = S(trailer, "name");
                F["weight"].Text = Math.Round(trailer.GetProperty("mass").GetDouble()).ToString();
                F["trailerPlate"].Text = S(trailer, "licensePlate");
                F["truckPlate"].Text = S(root.GetProperty("truck"), "licensePlate");
                Show();
                Activate();
            }
            if (!attached) wasAttached = false; else if (attachedTicks >= 2) wasAttached = true;
        }
        catch { /* serveris neveikia */ }
    }
}
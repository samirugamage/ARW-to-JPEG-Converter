// Program.cs
// ARW to JPEG GUI using WinForms (.NET 8). No Python required.
// Two conversion engines:
//   RawTherapee (rawtherapee-cli.exe): full RAW development with lens correction,
//     highlight recovery and camera-like tones. Best look, slower.
//   ImageMagick (magick.exe): plain RAW decode. Fastest, flatter look.
// exiftool.exe then copies all metadata from each ARW into its JPEG in one batch run.
// Build with .NET 8 on Windows or via GitHub Actions.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

// Order matters: saved in settings by name.
public enum ConvertEngine { RawTherapee = 0, ImageMagick = 1 }

public class AppSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; }
    public string? Engine { get; set; }
    public string? RawTherapeePath { get; set; }
    public string? MagickPath { get; set; }
    public string? ExifPath { get; set; }
    public string? LastInputDir { get; set; }
    public bool OutputToFolder { get; set; }
    public string? OutputDir { get; set; }
    public bool SkipExisting { get; set; } = true;
    public int Quality { get; set; } = 90;
    public bool Recurse { get; set; } = true;
    public int Jobs { get; set; } = DefaultJobs();

    public static int DefaultJobs() => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public static string FilePath() => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            var p = FilePath();
            if (File.Exists(p))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p)) ?? new AppSettings();
        }
        catch { }

        // The old version saved quality 100, which is what made some JPEGs bigger
        // than their ARW. Move old settings to the new default once.
        if (s.Version < CurrentVersion)
        {
            if (s.Quality > 95) s.Quality = 90;
            s.Version = CurrentVersion;
        }
        return s;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath(), json);
        }
        catch { }
    }
}

// ======================================================================
//  Look and feel
// ======================================================================

static class Theme
{
    public static readonly Color Page = Color.FromArgb(236, 241, 247);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(214, 222, 233);
    public static readonly Color Text = Color.FromArgb(17, 24, 39);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color Good = Color.FromArgb(22, 163, 74);
    public static readonly Color Bad = Color.FromArgb(220, 38, 38);
    public static readonly Color Slate = Color.FromArgb(100, 116, 139);
    public static readonly Color Violet = Color.FromArgb(124, 58, 237);
    public static readonly Color LogBack = Color.FromArgb(17, 24, 39);
    public static readonly Color LogText = Color.FromArgb(209, 213, 219);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Strong = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Status = new("Segoe UI Semibold", 10.5f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 11.25f);
    public static readonly Font HeaderTitle = new("Segoe UI Semibold", 17f);
    public static readonly Font Big = new("Segoe UI Semibold", 15f);
    public static readonly Font Mono = new("Consolas", 9.5f);
    public static readonly Font Icons = new("Segoe MDL2 Assets", 10f);
    public static readonly Font IconsSmall = new("Segoe MDL2 Assets", 8f);
    public static readonly Font IconsLarge = new("Segoe MDL2 Assets", 20f);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    public static Color Lighter(Color c, float t) => Mix(c, Color.White, t);
    public static Color Darker(Color c, float t) => Mix(c, Color.Black, t);

    // Paints a white "glass" shine over the top half of a shape.
    public static void Gloss(Graphics g, GraphicsPath shape, RectangleF r, int topAlpha, int bottomAlpha)
    {
        var state = g.Save();
        g.SetClip(shape);
        var top = new RectangleF(r.X, r.Y, r.Width, Math.Max(1, r.Height / 2));
        using (var brush = new LinearGradientBrush(new RectangleF(top.X, top.Y - 1, top.Width, top.Height + 2),
                   Color.FromArgb(topAlpha, Color.White), Color.FromArgb(bottomAlpha, Color.White), 90f))
            g.FillRectangle(brush, top);
        g.Restore(state);
    }

    public const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
}

// Gradient title band with a glass shine.
sealed class HeaderBand : Panel
{
    public HeaderBand()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Top;
        Height = 84;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        if (r.Width < 2 || r.Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;

        using (var bg = new LinearGradientBrush(r, Color.FromArgb(30, 64, 175), Color.FromArgb(91, 33, 182), 0f))
            g.FillRectangle(bg, r);
        using (var shape = new GraphicsPath())
        {
            shape.AddRectangle(r);
            Theme.Gloss(g, shape, r, 70, 10);
        }

        var tile = new RectangleF(20 * s, (r.Height - 50 * s) / 2, 50 * s, 50 * s);
        using (var tilePath = Theme.Round(tile, 13 * s))
        {
            using (var tileBrush = new SolidBrush(Color.FromArgb(50, Color.White))) g.FillPath(tileBrush, tilePath);
            Theme.Gloss(g, tilePath, tile, 70, 0);
            using (var tilePen = new Pen(Color.FromArgb(90, Color.White))) g.DrawPath(tilePen, tilePath);
        }
        TextRenderer.DrawText(g, "", Theme.IconsLarge, Rectangle.Round(tile), Color.White,
            Theme.Line | TextFormatFlags.HorizontalCenter);

        int x = (int)(tile.Right + 16 * s);
        TextRenderer.DrawText(g, "ARW → JPEG Converter", Theme.HeaderTitle,
            new Rectangle(x, (int)(r.Height / 2 - 34 * s), r.Width - x, (int)(34 * s)), Color.White,
            TextFormatFlags.SingleLine | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Sony RAW photos to small, high-quality JPEGs, with all the photo details kept", Theme.Body,
            new Rectangle(x, (int)(r.Height / 2 + 4 * s), r.Width - x, (int)(22 * s)), Color.FromArgb(224, 231, 255),
            TextFormatFlags.SingleLine | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

// White rounded card with an optional step number and a title.
sealed class Card : TableLayoutPanel
{
    readonly string? step;
    readonly string title;

    public Card(string? step, string title)
    {
        this.step = step;
        this.title = title;
        DoubleBuffered = true;
        BackColor = Theme.Card;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 14);
        Padding = new Padding(18, 48, 18, 16);
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void AddRow(Control c, bool fill = false)
    {
        RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        RowCount = RowStyles.Count;
        Controls.Add(c, 0, RowCount - 1);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Page);
        if (Width < 8 || Height < 8) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f - 3 * s);
        using (var shadowPath = Theme.Round(new RectangleF(r.X, r.Y + 3 * s, r.Width, r.Height), 11 * s))
        using (var shadow = new SolidBrush(Color.FromArgb(22, 15, 23, 42)))
            g.FillPath(shadow, shadowPath);
        using (var path = Theme.Round(r, 11 * s))
        {
            using (var fill = new SolidBrush(Theme.Card)) g.FillPath(fill, path);
            using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
        }

        float x = 18 * s;
        if (step != null)
        {
            var dot = new RectangleF(x, 13 * s, 24 * s, 24 * s);
            using (var dotPath = new GraphicsPath())
            {
                dotPath.AddEllipse(dot);
                using (var dotBrush = new LinearGradientBrush(dot, Theme.Lighter(Theme.Accent, 0.2f), Theme.Darker(Theme.Accent, 0.15f), 90f))
                    g.FillPath(dotBrush, dotPath);
                Theme.Gloss(g, dotPath, dot, 110, 20);
            }
            TextRenderer.DrawText(g, step, Theme.Strong, Rectangle.Round(dot), Color.White, Theme.Line | TextFormatFlags.HorizontalCenter);
            x = dot.Right + 10 * s;
        }
        TextRenderer.DrawText(g, title, Theme.CardTitle, new Rectangle((int)x, (int)(12 * s), Width - (int)x, (int)(26 * s)), Theme.Text, Theme.Line);
    }
}

// Rounded gradient button with a glossy top half and an optional icon.
sealed class GlossyButton : Button
{
    bool hover, pressed;
    public Color BaseColor { get; set; } = Theme.Accent;
    public bool Light { get; set; }
    public string? Glyph { get; set; }

    public static GlossyButton Browse() => new()
    {
        Text = "Browse",
        Glyph = "",
        Light = true,
        Size = new Size(104, 30),
        Margin = new Padding(8, 2, 0, 2),
    };

    public GlossyButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Strong;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Page);
        if (Width < 6 || Height < 6) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

        Color face = Light ? Color.FromArgb(248, 250, 252) : BaseColor;
        if (!Enabled) face = Light ? Color.FromArgb(241, 245, 249) : Color.FromArgb(203, 213, 225);
        else if (pressed) face = Theme.Darker(face, 0.12f);
        else if (hover) face = Light ? Color.FromArgb(239, 246, 255) : Theme.Lighter(face, 0.12f);

        using var path = Theme.Round(rect, 8 * s);
        using (var fill = new LinearGradientBrush(rect, Theme.Lighter(face, 0.15f), Theme.Darker(face, Light ? 0.04f : 0.14f), 90f))
            g.FillPath(fill, path);
        Theme.Gloss(g, path, rect, Light ? 140 : 85, Light ? 30 : 12);
        var border = Light ? (hover && Enabled ? Theme.Accent : Theme.Border) : Theme.Darker(face, 0.22f);
        using (var pen = new Pen(border)) g.DrawPath(pen, path);

        var color = !Enabled ? (Light ? Color.FromArgb(148, 163, 184) : Color.White) : Light ? Theme.Text : Color.White;
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size, Theme.Line);
        var glyphSize = string.IsNullOrEmpty(Glyph) ? Size.Empty : TextRenderer.MeasureText(g, Glyph, Theme.Icons, Size, Theme.Line);
        int gap = glyphSize.IsEmpty ? 0 : (int)(8 * s);
        int x = (Width - (glyphSize.Width + gap + textSize.Width)) / 2;
        if (!glyphSize.IsEmpty)
            TextRenderer.DrawText(g, Glyph, Theme.Icons, new Rectangle(x, 0, glyphSize.Width, Height), color, Theme.Line);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphSize.Width + gap, 0, textSize.Width + 2, Height), color, Theme.Line);
    }
}

// Rounded progress bar with a glossy fill.
sealed class GlossyProgress : Control
{
    int current, max = 100;

    public int Value { get => current; set { current = Math.Clamp(value, 0, max); Invalidate(); } }
    public int Maximum { get => max; set { max = Math.Max(1, value); current = Math.Min(current, max); Invalidate(); } }

    public GlossyProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 14;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        if (Width < 4 || Height < 4) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var track = Theme.Round(r, r.Height / 2))
        using (var trackBrush = new LinearGradientBrush(r, Color.FromArgb(220, 226, 235), Color.FromArgb(236, 240, 245), 90f))
            g.FillPath(trackBrush, track);
        if (current <= 0) return;
        var fillRect = new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * current / max), r.Height);
        using var fillPath = Theme.Round(fillRect, r.Height / 2);
        using (var fill = new LinearGradientBrush(fillRect, Theme.Lighter(Theme.Accent, 0.25f), Theme.Darker(Theme.Accent, 0.1f), 90f))
            g.FillPath(fill, fillPath);
        Theme.Gloss(g, fillPath, fillRect, 120, 20);
    }
}

// Small coloured tile showing one number (converted, skipped, saved, time).
sealed class StatTile : Control
{
    readonly Color tint;
    string caption, shown = "-";

    public StatTile(string caption, Color tint)
    {
        this.caption = caption;
        this.tint = tint;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 76;
        Margin = new Padding(0, 0, 8, 0);
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
    }

    public string Shown { get => shown; set { if (shown != value) { shown = value; Invalidate(); } } }
    public string Caption { get => caption; set { if (caption != value) { caption = value; Invalidate(); } } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        if (Width < 8 || Height < 8) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, 9 * s);
        using (var fill = new LinearGradientBrush(r, Theme.Lighter(tint, 0.94f), Theme.Lighter(tint, 0.84f), 90f))
            g.FillPath(fill, path);
        Theme.Gloss(g, path, r, 120, 15);
        using (var pen = new Pen(Theme.Lighter(tint, 0.65f))) g.DrawPath(pen, path);
        int pad = (int)(12 * s);
        TextRenderer.DrawText(g, shown, Theme.Big, new Rectangle(pad, (int)(8 * s), Width - 2 * pad, (int)(32 * s)),
            Theme.Darker(tint, 0.25f), Theme.Line | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, caption, Theme.Small, new Rectangle(pad, (int)(44 * s), Width - 2 * pad, (int)(20 * s)),
            Theme.Muted, Theme.Line | TextFormatFlags.EndEllipsis);
    }
}

// Pill showing whether a tool was found.
sealed class ToolChip : Control
{
    public enum ChipState { Found, Missing, NotNeeded }
    ChipState state = ChipState.Missing;

    public ToolChip(string name)
    {
        Text = name;
        Font = Theme.Strong;
        AutoSize = true;
        Margin = new Padding(0, 0, 8, 0);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public ChipState State { get => state; set { if (state != value) { state = value; Invalidate(); } } }

    public override Size GetPreferredSize(Size proposedSize)
    {
        float s = DeviceDpi / 96f;
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + (int)(36 * s), (int)(28 * s));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        if (Width < 8 || Height < 8) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var color = state switch { ChipState.Found => Theme.Good, ChipState.Missing => Theme.Bad, _ => Theme.Muted };
        var glyph = state switch { ChipState.Found => "", ChipState.Missing => "", _ => "" };
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, r.Height / 2);
        using (var fill = new SolidBrush(Theme.Lighter(color, 0.9f))) g.FillPath(fill, path);
        using (var pen = new Pen(Theme.Lighter(color, 0.55f))) g.DrawPath(pen, path);
        int x = (int)(11 * s);
        TextRenderer.DrawText(g, glyph, Theme.IconsSmall, new Rectangle(x, 0, (int)(14 * s), Height), color, Theme.Line);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + (int)(18 * s), 0, Width - x - (int)(18 * s), Height),
            state == ChipState.NotNeeded ? Theme.Muted : Theme.Darker(color, 0.3f), Theme.Line);
    }
}

// ======================================================================
//  Main window
// ======================================================================

public class MainForm : Form
{
    // ---- ImageMagick engine ----
    // Read the raw with the camera's white balance. A -define only affects decoding
    // when it comes BEFORE the input file on the command line.
    const string DecodeArgs = "-define dng:use-camera-wb=true";

    // -strip: drop ImageMagick's own profiles (exiftool adds the real metadata afterwards).
    // -sampling-factor 4:2:0: ImageMagick turns chroma subsampling off above quality 90,
    //   which makes files much bigger for no visible gain on photos.
    // -interlace JPEG: progressive, usually a little smaller.
    // jpeg:optimize-coding: optimized Huffman tables, smaller at zero quality cost.
    const string JpegArgs = "-strip -sampling-factor 4:2:0 -interlace JPEG -define jpeg:optimize-coding=true";

    // ---- RawTherapee engine ----
    // Uses RawTherapee's bundled "Auto-Matched Curve" profiles, which match the tone curve
    // of the camera's own JPEG and turn on automatic lens correction. The profile is picked
    // per photo from its ISO: Low = capture sharpening, Medium = colour noise reduction,
    // High = stronger noise reduction.
    static readonly string[] IsoLevels = { "Low", "Medium", "High" };
    static string IsoLevel(int iso) => iso <= 800 ? "Low" : iso <= 3200 ? "Medium" : "High";
    static string ProfilePath(string profilesDir, string level) =>
        Path.Combine(profilesDir, $"Auto-Matched Curve - ISO {level}.pp3");

    // RawTherapee already rotates the pixels and writes the right image size, so when copying
    // the ARW's metadata don't copy its rotation flag, pixel size or landscape thumbnail on top.
    static readonly string[] RawTherapeeMetaExclusions = { "--Orientation", "--ExifImageWidth", "--ExifImageHeight", "--IFD1:all" };

    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    // ---- step 1: photos ----
    readonly TextBox txtInput = NewTextBox("Choose the folder with your ARW photos");
    readonly GlossyButton btnInput = GlossyButton.Browse();
    readonly CheckBox chkRecurse = NewCheck("Include subfolders", true);

    // ---- step 2: output ----
    readonly RadioButton optNextTo = NewRadio("Next to each ARW file", Theme.Body, true);
    readonly RadioButton optFolder = NewRadio("In this folder:", Theme.Body, false);
    readonly TextBox txtOutput = NewTextBox("Choose where to save the JPEGs");
    readonly GlossyButton btnOutput = GlossyButton.Browse();
    readonly CheckBox chkSkip = NewCheck("Skip photos that already have a JPEG", true);

    // ---- step 3: conversion ----
    readonly RadioButton optRt = NewRadio("RawTherapee: best look", Theme.Strong, true);
    readonly RadioButton optIm = NewRadio("ImageMagick: fastest", Theme.Strong, false);
    readonly TrackBar trkQuality = new()
    {
        Minimum = 50, Maximum = 100, Value = 90, TickStyle = TickStyle.None, AutoSize = false,
        Size = new Size(165, 30), BackColor = Theme.Card, SmallChange = 1, LargeChange = 5, Margin = new Padding(0),
    };
    readonly Label lblQuality = new() { Text = "90", Font = Theme.Strong, ForeColor = Theme.Text, AutoSize = false, Size = new Size(34, 30), TextAlign = ContentAlignment.MiddleLeft };
    readonly NumericUpDown numJobs = new() { Minimum = 1, Maximum = Math.Max(1, Environment.ProcessorCount), Value = AppSettings.DefaultJobs(), Width = 56, Margin = new Padding(0, 3, 10, 3) };

    // ---- tools ----
    readonly TextBox txtRt = NewTextBox("Path to rawtherapee-cli.exe");
    readonly TextBox txtMagick = NewTextBox("Path to magick.exe");
    readonly TextBox txtExif = NewTextBox("Path to exiftool.exe");
    readonly GlossyButton btnRt = GlossyButton.Browse(), btnMagick = GlossyButton.Browse(), btnExif = GlossyButton.Browse();
    readonly ToolChip chipRt = new("RawTherapee"), chipMagick = new("ImageMagick"), chipExif = new("ExifTool");
    readonly LinkLabel lnkTools = new() { Text = "Edit paths", AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Darker(Theme.Accent, 0.2f), Margin = new Padding(4, 0, 0, 0) };
    readonly TableLayoutPanel toolPaths = new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Visible = false, Margin = new Padding(0, 10, 0, 0) };

    // ---- step 4: run ----
    readonly GlossyButton btnStart = new() { Text = "Start converting", Glyph = "", Size = new Size(210, 46), Margin = new Padding(0, 0, 10, 0) };
    readonly GlossyButton btnCancel = new() { Text = "Cancel", Glyph = "", BaseColor = Theme.Slate, Size = new Size(120, 46), Enabled = false, Margin = new Padding(0) };
    readonly Label lblStatus = new() { Text = "Ready", Font = Theme.Status, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
    readonly GlossyProgress progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 8, 0, 14) };
    readonly StatTile tileDone = new("Converted", Theme.Accent);
    readonly StatTile tileSkipped = new("Skipped", Theme.Slate);
    readonly StatTile tileSaved = new("Space saved", Theme.Good);
    readonly StatTile tileTime = new("Time", Theme.Violet) { Margin = new Padding(0) };
    readonly TextBox txtLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.None,
        BackColor = Theme.LogBack, ForeColor = Theme.LogText, Font = Theme.Mono, Dock = DockStyle.Fill,
    };

    readonly TableLayoutPanel leftStack = new() { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0) };
    readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 1000 };

    CancellationTokenSource? cts;
    RunStats? current;
    Stopwatch? runClock;
    int runTotal;
    int countVersion;

    sealed class RunStats
    {
        public long ArwBytes, JpgBytes;
        public int Done, Converted, Failed, Larger, Skipped;
        public readonly ConcurrentQueue<(string Arw, string Jpg)> Pairs = new();
    }

    sealed record ToolCommand(string Exe, string Args, string Note);

    sealed record ToolResult(int Exit, string StdOut, string StdErr)
    {
        public string All => (StdOut + StdErr).TrimEnd();
    }

    public MainForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ARW to JPEG Converter";
        Font = Theme.Body;
        ForeColor = Theme.Text;
        BackColor = Theme.Page;
        ClientSize = new Size(1120, 820);
        MinimumSize = new Size(940, 660);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        // ---- 1: photos ----
        var cardPhotos = new Card("1", "Choose photos");
        cardPhotos.AddRow(PathRow(txtInput, btnInput));
        chkRecurse.Margin = new Padding(0, 8, 0, 0);
        cardPhotos.AddRow(chkRecurse);

        // ---- 2: output ----
        var cardOutput = new Card("2", "Where to save the JPEGs");
        cardOutput.AddRow(optNextTo);
        cardOutput.AddRow(optFolder);
        cardOutput.AddRow(PathRow(txtOutput, btnOutput, indent: 20));
        cardOutput.AddRow(Hint("Subfolders are recreated inside this folder.", 20));
        chkSkip.Margin = new Padding(0, 12, 0, 0);
        cardOutput.AddRow(chkSkip);
        cardOutput.AddRow(Hint("Turn off to convert again and replace existing JPEGs.", 20));

        // ---- 3: conversion ----
        var cardConvert = new Card("3", "Conversion");
        cardConvert.AddRow(optRt);
        cardConvert.AddRow(Hint("Lens correction, camera-like colors, ISO-based noise reduction.", 20));
        optIm.Margin = new Padding(0, 8, 0, 0);
        cardConvert.AddRow(optIm);
        cardConvert.AddRow(Hint("Plain, flatter look. About 3x faster.", 20));
        var qualityRow = HRow(FieldLabel("JPEG quality"), trkQuality, lblQuality, Hint("88-92 recommended"));
        qualityRow.Margin = new Padding(0, 14, 0, 0);
        cardConvert.AddRow(qualityRow);
        cardConvert.AddRow(HRow(FieldLabel("Parallel jobs"), numJobs, Hint("photos converted at the same time")));

        // ---- tools ----
        var cardTools = new Card(null, "Tools");
        cardTools.AddRow(HRow(chipRt, chipMagick, chipExif, lnkTools));
        toolPaths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolPaths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddToolRow("RawTherapee", txtRt, btnRt);
        AddToolRow("ImageMagick", txtMagick, btnMagick);
        AddToolRow("ExifTool", txtExif, btnExif);
        cardTools.AddRow(toolPaths);

        leftStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var card in new[] { cardPhotos, cardOutput, cardConvert })
        {
            leftStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftStack.Controls.Add(card, 0, leftStack.RowStyles.Count - 1);
        }
        leftStack.RowCount = leftStack.RowStyles.Count;
        var leftScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 0, 8, 0), BackColor = Theme.Page };
        leftScroll.Controls.Add(leftStack);

        // ---- 4: run ----
        var cardRun = new Card("4", "Convert");
        cardRun.AddRow(HRow(btnStart, btnCancel));
        cardRun.AddRow(lblStatus);
        cardRun.AddRow(progress);
        var tiles = new TableLayoutPanel { ColumnCount = 4, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        tiles.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tileList = new[] { tileDone, tileSkipped, tileSaved, tileTime };
        for (int i = 0; i < tileList.Length; i++)
        {
            tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            tiles.Controls.Add(tileList[i], i, 0);
        }
        cardRun.AddRow(tiles);

        var cardLog = new Card(null, "Activity log") { AutoSize = false, Margin = new Padding(0) };
        var logHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.LogBack, Padding = new Padding(12, 10, 4, 10), Margin = new Padding(0) };
        logHost.Controls.Add(txtLog);
        cardLog.AddRow(logHost, fill: true);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(8, 0, 0, 0), BackColor = Theme.Page };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(cardRun, 0, 0);
        right.Controls.Add(cardTools, 0, 1);
        right.Controls.Add(cardLog, 0, 2);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(16, 16, 16, 16), BackColor = Theme.Page };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(leftScroll, 0, 0);
        body.Controls.Add(right, 1, 0);

        Controls.Add(body);              // fills what the header leaves
        Controls.Add(new HeaderBand());  // docked to the top

        // ---- default tool paths: "tools" folder next to the app, else an installed copy ----
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        txtRt.Text = FindRawTherapee(toolsDir);
        txtMagick.Text = FindTool(toolsDir, "magick.exe");
        txtExif.Text = FindTool(toolsDir, "exiftool.exe");

        // ---- saved settings ----
        var s = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(s.RawTherapeePath) && File.Exists(s.RawTherapeePath)) txtRt.Text = s.RawTherapeePath;
        if (!string.IsNullOrWhiteSpace(s.MagickPath) && File.Exists(s.MagickPath)) txtMagick.Text = s.MagickPath;
        if (!string.IsNullOrWhiteSpace(s.ExifPath) && File.Exists(s.ExifPath)) txtExif.Text = s.ExifPath;
        if (!string.IsNullOrWhiteSpace(s.LastInputDir) && Directory.Exists(s.LastInputDir)) txtInput.Text = s.LastInputDir;
        txtOutput.Text = s.OutputDir ?? "";
        optFolder.Checked = s.OutputToFolder;
        optNextTo.Checked = !s.OutputToFolder;
        chkSkip.Checked = s.SkipExisting;
        if (s.Quality >= trkQuality.Minimum && s.Quality <= trkQuality.Maximum) trkQuality.Value = s.Quality;
        lblQuality.Text = trkQuality.Value.ToString();
        if (s.Jobs >= numJobs.Minimum && s.Jobs <= numJobs.Maximum) numJobs.Value = s.Jobs;
        chkRecurse.Checked = s.Recurse;
        // No saved engine yet: use RawTherapee when it is installed.
        var engine = Enum.TryParse<ConvertEngine>(s.Engine, out var saved) && Enum.IsDefined(saved)
            ? saved
            : File.Exists(txtRt.Text) ? ConvertEngine.RawTherapee : ConvertEngine.ImageMagick;
        optRt.Checked = engine == ConvertEngine.RawTherapee;
        optIm.Checked = engine == ConvertEngine.ImageMagick;

        // ---- events ----
        btnInput.Click += (_, __) =>
        {
            var dir = PickFolder("Choose the folder with your ARW photos", txtInput.Text);
            if (dir != null) txtInput.Text = dir;
        };
        btnOutput.Click += (_, __) =>
        {
            var dir = PickFolder("Choose where to save the JPEGs", txtOutput.Text.Length > 0 ? txtOutput.Text : txtInput.Text);
            if (dir != null) { txtOutput.Text = dir; optFolder.Checked = true; }
        };
        btnRt.Click += (_, __) => { var p = PickExe("rawtherapee-cli.exe"); if (p != null) txtRt.Text = p; };
        btnMagick.Click += (_, __) => { var p = PickExe("magick.exe"); if (p != null) txtMagick.Text = p; };
        btnExif.Click += (_, __) => { var p = PickExe("exiftool.exe"); if (p != null) txtExif.Text = p; };
        lnkTools.LinkClicked += (_, __) => ShowToolPaths(!toolPaths.Visible);
        trkQuality.ValueChanged += (_, __) => lblQuality.Text = trkQuality.Value.ToString();
        txtInput.TextChanged += (_, __) => RefreshPhotoCount();
        chkRecurse.CheckedChanged += (_, __) => RefreshPhotoCount();
        optFolder.CheckedChanged += (_, __) => UpdateOutputEnabled();
        optRt.CheckedChanged += (_, __) => UpdateToolStatus();
        txtRt.TextChanged += (_, __) => UpdateToolStatus();
        txtMagick.TextChanged += (_, __) => UpdateToolStatus();
        txtExif.TextChanged += (_, __) => UpdateToolStatus();
        btnStart.Click += async (_, __) => await StartAsync();
        btnCancel.Click += (_, __) => { cts?.Cancel(); Log("Canceling..."); SetStatus("Canceling..."); };
        clockTimer.Tick += (_, __) => { if (runClock != null) tileTime.Shown = FormatTime(runClock.Elapsed); };
        FormClosing += (_, __) => { cts?.Cancel(); SaveCurrentSettings(); };
        Load += (_, __) =>
        {
            // Never open taller or wider than the screen.
            var area = Screen.FromControl(this).WorkingArea;
            if (Height > area.Height || Width > area.Width)
            {
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                CenterToScreen();
            }
        };

        UpdateOutputEnabled();
        UpdateToolStatus();
        ResetTiles();
        RefreshPhotoCount();
    }

    // ------------------------------------------------------------------
    //  UI helpers
    // ------------------------------------------------------------------

    static TextBox NewTextBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        BorderStyle = BorderStyle.FixedSingle,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 2, 0, 2),
    };

    static CheckBox NewCheck(string text, bool isChecked) => new()
    {
        Text = text, Checked = isChecked, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 4, 0, 0),
    };

    static RadioButton NewRadio(string text, Font font, bool isChecked) => new()
    {
        Text = text, Font = font, Checked = isChecked, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 4, 0, 0),
    };

    static Label Hint(string text, int indent = 0) => new()
    {
        Text = text, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(indent, 1, 0, 2),
    };

    static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = false, Size = new Size(104, 30), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Margin = new Padding(0),
    };

    // A row of controls, each vertically centred.
    static TableLayoutPanel HRow(params Control[] items)
    {
        var row = new TableLayoutPanel { ColumnCount = items.Length, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 2, 0, 2) };
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < items.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            items[i].Anchor = AnchorStyles.Left;
            row.Controls.Add(items[i], i, 0);
        }
        return row;
    }

    // A text box that stretches, with a Browse button on the right.
    static TableLayoutPanel PathRow(TextBox box, Button browse, int indent = 0)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(indent, 2, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Controls.Add(box, 0, 0);
        row.Controls.Add(browse, 1, 0);
        return row;
    }

    void AddToolRow(string name, TextBox box, Button browse)
    {
        toolPaths.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        toolPaths.RowCount = toolPaths.RowStyles.Count;
        toolPaths.Controls.Add(FieldLabel(name), 0, toolPaths.RowCount - 1);
        toolPaths.Controls.Add(PathRow(box, browse), 1, toolPaths.RowCount - 1);
    }

    void ShowToolPaths(bool show)
    {
        toolPaths.Visible = show;
        lnkTools.Text = show ? "Hide paths" : "Edit paths";
    }

    void UpdateOutputEnabled()
    {
        txtOutput.Enabled = optFolder.Checked;
        btnOutput.Enabled = optFolder.Checked;
    }

    void UpdateToolStatus()
    {
        bool rtNeeded = optRt.Checked;
        chipRt.State = File.Exists(txtRt.Text.Trim()) ? ToolChip.ChipState.Found : rtNeeded ? ToolChip.ChipState.Missing : ToolChip.ChipState.NotNeeded;
        chipMagick.State = File.Exists(txtMagick.Text.Trim()) ? ToolChip.ChipState.Found : !rtNeeded ? ToolChip.ChipState.Missing : ToolChip.ChipState.NotNeeded;
        chipExif.State = File.Exists(txtExif.Text.Trim()) ? ToolChip.ChipState.Found : ToolChip.ChipState.Missing;
        if (new[] { chipRt, chipMagick, chipExif }.Any(c => c.State == ToolChip.ChipState.Missing))
            ShowToolPaths(true);
    }

    // Counts the ARW files in the chosen folder in the background and shows it as the status.
    async void RefreshPhotoCount()
    {
        if (cts != null) return;
        var version = ++countVersion;
        var dir = txtInput.Text.Trim();
        var option = chkRecurse.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        if (dir.Length == 0 || !Directory.Exists(dir)) { SetStatus("Choose the folder with your ARW photos"); return; }
        SetStatus("Looking for photos...");
        int count;
        try { count = await Task.Run(() => Directory.EnumerateFiles(dir, "*.ARW", option).Count()); }
        catch { count = -1; }
        if (version != countVersion || cts != null) return;
        SetStatus(count < 0 ? "Can't read this folder"
                : count == 0 ? "No ARW photos in this folder"
                : $"Ready: {count} ARW photo{(count == 1 ? "" : "s")} found");
    }

    void SetStatus(string text) => lblStatus.Text = text;

    void ResetTiles()
    {
        tileDone.Shown = "0";
        tileSkipped.Shown = "0";
        tileSaved.Shown = "0 MB";
        tileSaved.Caption = "Space saved";
        tileTime.Shown = "0:00";
    }

    void UpdateTiles(RunStats s)
    {
        tileDone.Shown = runTotal > 0 ? $"{s.Converted} / {runTotal}" : s.Converted.ToString();
        tileSkipped.Shown = s.Skipped.ToString();
        var saved = Math.Max(0, s.ArwBytes - s.JpgBytes);
        tileSaved.Shown = FormatSize(saved);
        tileSaved.Caption = s.ArwBytes > 0 ? $"Saved ({100.0 * saved / s.ArwBytes:0}%)" : "Space saved";
        if (runClock != null) tileTime.Shown = FormatTime(runClock.Elapsed);
    }

    void SetRunning(bool running)
    {
        btnStart.Enabled = !running;
        btnCancel.Enabled = running;
        leftStack.Enabled = !running;
        if (running) clockTimer.Start(); else clockTimer.Stop();
    }

    string? PickFolder(string title, string start)
    {
        using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(start)) dialog.InitialDirectory = start;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    string? PickExe(string name)
    {
        using var dialog = new OpenFileDialog { Filter = name + "|" + name + "|Programs|*.exe", Title = "Find " + name };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    void Warn(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    static string FindTool(string toolsDir, string exe)
    {
        var local = Path.Combine(toolsDir, exe);
        if (File.Exists(local)) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return "";
    }

    // RawTherapee's installer does not add itself to PATH, so also look in
    // Program Files\RawTherapee\<version>\ and take the newest version.
    static string FindRawTherapee(string toolsDir)
    {
        var found = FindTool(toolsDir, "rawtherapee-cli.exe");
        if (found != "") return found;
        foreach (var programFiles in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(programFiles), "RawTherapee");
                if (!Directory.Exists(root)) continue;
                var newest = Directory.EnumerateFiles(root, "rawtherapee-cli.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(p)), out var v) ? v : new Version(0, 0))
                    .FirstOrDefault();
                if (newest != null) return newest;
            }
            catch { }
        }
        return "";
    }

    // ------------------------------------------------------------------
    //  Conversion
    // ------------------------------------------------------------------

    async Task StartAsync()
    {
        SaveCurrentSettings();

        var input = txtInput.Text.Trim();
        var engine = optRt.Checked ? ConvertEngine.RawTherapee : ConvertEngine.ImageMagick;
        var rt = txtRt.Text.Trim();
        var magick = txtMagick.Text.Trim();
        var exif = txtExif.Text.Trim();
        if (input.Length == 0 || !Directory.Exists(input)) { Warn("Choose the folder with your ARW photos first."); return; }
        if (engine == ConvertEngine.RawTherapee && !File.Exists(rt)) { Warn("RawTherapee was not found. Install it, set its location under Tools, or choose ImageMagick."); return; }
        if (engine == ConvertEngine.ImageMagick && !File.Exists(magick)) { Warn("ImageMagick was not found. Install it or set its location under Tools."); return; }
        if (!File.Exists(exif)) { Warn("ExifTool was not found. Install it or set its location under Tools."); return; }

        var profilesDir = "";
        if (engine == ConvertEngine.RawTherapee)
        {
            profilesDir = Path.Combine(Path.GetDirectoryName(rt)!, "profiles");
            var missing = IsoLevels.Select(l => ProfilePath(profilesDir, l)).FirstOrDefault(p => !File.Exists(p));
            if (missing != null) { Warn("RawTherapee profile not found:\n" + missing); return; }
        }

        var inputRoot = Path.GetFullPath(input);
        string? outputRoot = null;
        if (optFolder.Checked)
        {
            if (txtOutput.Text.Trim().Length == 0) { Warn("Choose the folder to save the JPEGs in, or pick \"Next to each ARW file\"."); return; }
            try
            {
                outputRoot = Path.GetFullPath(txtOutput.Text.Trim());
                Directory.CreateDirectory(outputRoot);
            }
            catch (Exception ex) { Warn("Can't use that output folder:\n" + ex.Message); return; }
        }

        bool skipExisting = chkSkip.Checked;
        int quality = trkQuality.Value;
        int jobs = (int)numJobs.Value;
        var searchOption = chkRecurse.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        cts = new CancellationTokenSource();
        var token = cts.Token;
        var stats = current = new RunStats();
        runTotal = 0;
        runClock = Stopwatch.StartNew();
        txtLog.Clear();
        progress.Value = 0;
        ResetTiles();
        SetRunning(true);
        SetStatus("Looking for photos...");
        try
        {
            var files = await Task.Run(() => Directory.EnumerateFiles(inputRoot, "*.ARW", searchOption)
                                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                                      .ToList());
            if (files.Count == 0) { Log("No ARW files found."); SetStatus("No ARW photos in this folder"); return; }

            // Work out where each JPEG goes, and leave out the ones that already exist.
            var work = new List<(string Arw, string Jpg)>();
            foreach (var arw in files)
            {
                var jpg = TargetPath(arw, inputRoot, outputRoot);
                if (skipExisting && File.Exists(jpg)) stats.Skipped++;
                else work.Add((arw, jpg));
            }
            runTotal = work.Count;
            progress.Maximum = Math.Max(1, work.Count);
            UpdateTiles(stats);

            Log($"Found {files.Count} ARW files. Engine: {engine}, quality {quality}, {jobs} parallel job(s).");
            Log(outputRoot == null ? "Saving each JPEG next to its ARW file." : $"Saving JPEGs in {outputRoot}");
            if (stats.Skipped > 0) Log($"Skipping {stats.Skipped} photo(s) that already have a JPEG.");
            if (work.Count == 0)
            {
                Log("Nothing new to convert.");
                SetStatus("Nothing new to convert: every photo already has a JPEG");
                return;
            }
            SetStatus($"Converting... 0 of {work.Count}");

            Func<string, string, ToolCommand> command;
            string[] metaExclusions;
            if (engine == ConvertEngine.RawTherapee)
            {
                Log("Reading ISO values to pick RawTherapee's noise profile for each photo...");
                var isoByFile = await ReadIsoAsync(exif, work.Select(w => w.Arw).ToList(), token);
                command = (arw, outFile) => RawTherapeeCommand(rt, profilesDir, isoByFile, arw, outFile, quality);
                metaExclusions = RawTherapeeMetaExclusions;
            }
            else
            {
                command = (arw, outFile) => new ToolCommand(magick, $"{DecodeArgs} \"{arw}\" {JpegArgs} -quality {quality} \"{outFile}\"", "");
                metaExclusions = Array.Empty<string>();
            }

            bool canceled = false;
            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = token };
                await Parallel.ForEachAsync(work, options, (item, ct) => ConvertOneAsync(item.Arw, item.Jpg, command, stats, ct));
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                Log("Canceled. Copying metadata into the files already converted...");
            }

            // One exiftool process for all JPEGs instead of one per file: exiftool's start-up
            // time on Windows is often longer than the metadata copy itself.
            if (!stats.Pairs.IsEmpty)
            {
                SetStatus("Copying photo details (metadata)...");
                await CopyMetadataAsync(exif, stats.Pairs.ToList(), metaExclusions);
            }

            LogSummary(stats, runClock.Elapsed);
            UpdateTiles(stats);
            SetStatus(canceled
                ? $"Canceled: {stats.Converted} converted before stopping"
                : stats.Failed > 0
                    ? $"Finished with problems: {stats.Converted} converted, {stats.Failed} failed (see log)"
                    : $"Done: {stats.Converted} converted{(stats.Skipped > 0 ? $", {stats.Skipped} skipped" : "")}");
        }
        catch (OperationCanceledException)
        {
            Log("Canceled.");
            SetStatus("Canceled");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            SetStatus("Stopped because of an error (see log)");
        }
        finally
        {
            runClock?.Stop();
            UpdateTiles(stats);
            SetRunning(false);
            cts?.Dispose();
            cts = null;
        }
    }

    // Next to the ARW, or in the output folder with the same subfolder layout as the input.
    static string TargetPath(string arw, string inputRoot, string? outputRoot)
    {
        var name = Path.GetFileNameWithoutExtension(arw) + ".jpg";
        var arwDir = Path.GetDirectoryName(arw)!;
        if (outputRoot == null) return Path.Combine(arwDir, name);
        var relative = Path.GetRelativePath(inputRoot, arwDir);
        return Path.GetFullPath(Path.Combine(outputRoot, relative, name));
    }

    static ToolCommand RawTherapeeCommand(string rt, string profilesDir, Dictionary<string, int> isoByFile,
                                          string arw, string outFile, int quality)
    {
        isoByFile.TryGetValue(NormalizePath(arw), out var iso); // 0 when unknown -> Low
        var level = IsoLevel(iso);
        // -q quick start, -p profile, -j quality, -js1 = 4:2:0 subsampling, -Y overwrite, -c input (must be last)
        var args = $"-o \"{outFile}\" -q -p \"{ProfilePath(profilesDir, level)}\" -j{quality} -js1 -Y -c \"{arw}\"";
        var note = iso > 0 ? $"ISO {iso}, {level} profile" : $"{level} profile";
        return new ToolCommand(rt, args, note);
    }

    async ValueTask ConvertOneAsync(string arw, string jpg, Func<string, string, ToolCommand> command, RunStats stats, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(jpg)!;
        // Write to a temporary name and rename when complete, so a cancel or crash
        // never leaves a half-written .jpg that looks finished.
        var partial = Path.Combine(dir, Path.GetFileNameWithoutExtension(jpg) + ".partial.jpg");
        try
        {
            Directory.CreateDirectory(dir);
            var cmd = command(arw, partial);
            var result = await RunToolAsync(cmd.Exe, cmd.Args, ct);
            if (result.Exit != 0 || !File.Exists(partial))
            {
                Interlocked.Increment(ref stats.Failed);
                Log($"FAILED {arw}{Environment.NewLine}{result.All}");
                return;
            }
            File.Move(partial, jpg, overwrite: true);

            long arwSize = new FileInfo(arw).Length;
            long jpgSize = new FileInfo(jpg).Length;
            Interlocked.Add(ref stats.ArwBytes, arwSize);
            Interlocked.Add(ref stats.JpgBytes, jpgSize);
            Interlocked.Increment(ref stats.Converted);
            stats.Pairs.Enqueue((arw, jpg));

            var note = cmd.Note.Length > 0 ? $" ({cmd.Note})" : "";
            var warning = "";
            if (jpgSize >= arwSize)
            {
                Interlocked.Increment(ref stats.Larger);
                warning = "  <- larger than the ARW, lower the quality";
            }
            Log($"{Path.GetFileName(arw)}: {FormatSize(arwSize)} -> {FormatSize(jpgSize)}{note}{warning}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref stats.Failed);
            Log($"FAILED {arw}: {ex.Message}");
        }
        finally
        {
            TryDelete(partial);
            var done = Interlocked.Increment(ref stats.Done);
            Ui(() =>
            {
                progress.Value = done;
                UpdateTiles(stats);
                if (cts is { IsCancellationRequested: false }) SetStatus($"Converting... {done} of {runTotal}");
            });
        }
    }

    // One exiftool run reads the ISO of every file (used to pick RawTherapee's noise profile).
    async Task<Dictionary<string, int>> ReadIsoAsync(string exif, List<string> files, CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var argFile = Path.Combine(Path.GetTempPath(), $"arw2jpg_{Guid.NewGuid():N}.args");
        try
        {
            var lines = new List<string> { "-charset", "filename=utf8", "-json", "-n", "-fast", "-ISO" };
            lines.AddRange(files);
            await File.WriteAllLinesAsync(argFile, lines, Utf8NoBom, ct);

            var result = await RunToolAsync(exif, $"-@ \"{argFile}\"", ct);
            using var doc = JsonDocument.Parse(result.StdOut);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("SourceFile", out var src) && src.GetString() is string path &&
                    item.TryGetProperty("ISO", out var iso) && iso.ValueKind == JsonValueKind.Number && iso.TryGetInt32(out var value))
                    map[NormalizePath(path)] = value;
            }
        }
        catch (JsonException)
        {
            Log("Could not read ISO values; using the low-ISO profile for every photo.");
        }
        finally
        {
            TryDelete(argFile);
        }
        return map;
    }

    // One exiftool process copies the metadata for every pair (one -execute block per photo),
    // so it works whether the JPEGs are next to the ARWs or in another folder.
    async Task CopyMetadataAsync(string exif, List<(string Arw, string Jpg)> pairs, string[] exclusions)
    {
        Log($"Copying metadata into {pairs.Count} JPEG(s) with one exiftool run...");
        var argFile = Path.Combine(Path.GetTempPath(), $"arw2jpg_{Guid.NewGuid():N}.args");
        try
        {
            var lines = new List<string>();
            for (int i = 0; i < pairs.Count; i++)
            {
                if (i > 0) lines.Add("-execute");
                lines.AddRange(new[] { "-charset", "filename=utf8", "-q", "-tagsFromFile", pairs[i].Arw, "-all:all" });
                lines.AddRange(exclusions);
                lines.Add("-overwrite_original");
                lines.Add(pairs[i].Jpg);
            }
            await File.WriteAllLinesAsync(argFile, lines, Utf8NoBom);

            var result = await RunToolAsync(exif, $"-@ \"{argFile}\"", CancellationToken.None);
            var messages = result.All.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            foreach (var line in messages.Take(40)) Log("exiftool: " + line);
            if (messages.Count > 40) Log($"exiftool: ...and {messages.Count - 40} more lines");
            Log(result.Exit == 0 && messages.Count == 0 ? "Metadata copied." : "exiftool reported the messages above.");
        }
        finally
        {
            TryDelete(argFile);
        }
    }

    void LogSummary(RunStats s, TimeSpan elapsed)
    {
        Log($"Finished in {FormatTime(elapsed)}: {s.Converted} converted, {s.Skipped} skipped, {s.Failed} failed.");
        if (s.ArwBytes > 0)
        {
            var change = 100.0 * (s.ArwBytes - s.JpgBytes) / s.ArwBytes;
            var how = change >= 0 ? $"{change:0}% smaller" : $"{-change:0}% LARGER";
            Log($"ARW total {FormatSize(s.ArwBytes)} -> JPEG total {FormatSize(s.JpgBytes)} ({how}).");
        }
        if (s.Larger > 0)
            Log($"{s.Larger} JPEG(s) came out larger than their ARW. Try a lower quality, e.g. 85.");
    }

    static async Task<ToolResult> RunToolAsync(string exe, string args, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
            throw;
        }
        lock (stdout) lock (stderr) return new ToolResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path.Replace('/', '\\')); } catch { return path; }
    }

    static string FormatSize(long bytes)
    {
        double mb = bytes / (double)(1L << 20);
        return mb >= 1024 ? $"{mb / 1024:0.00} GB" : mb >= 100 ? $"{mb:0} MB" : $"{mb:0.0} MB";
    }

    static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    void SaveCurrentSettings()
    {
        var set = new AppSettings
        {
            Version = AppSettings.CurrentVersion,
            Engine = (optRt.Checked ? ConvertEngine.RawTherapee : ConvertEngine.ImageMagick).ToString(),
            RawTherapeePath = txtRt.Text.Trim(),
            MagickPath = txtMagick.Text.Trim(),
            ExifPath = txtExif.Text.Trim(),
            LastInputDir = txtInput.Text.Trim(),
            OutputToFolder = optFolder.Checked,
            OutputDir = txtOutput.Text.Trim(),
            SkipExisting = chkSkip.Checked,
            Quality = trkQuality.Value,
            Recurse = chkRecurse.Checked,
            Jobs = (int)numJobs.Value,
        };
        set.Save();
    }

    // Run on the UI thread; ignored if the window is already gone.
    void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { } // also covers ObjectDisposedException
    }

    void Log(string msg)
    {
        if (InvokeRequired) { Ui(() => Log(msg)); return; }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }
}

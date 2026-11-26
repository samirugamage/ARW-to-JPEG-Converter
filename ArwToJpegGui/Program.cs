// Program.cs
// ARW to JPEG GUI using WinForms. No Python required.
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
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

// Order matches the items in the Engine drop-down.
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

    // UI
    TextBox txtInputDir = new TextBox { Dock = DockStyle.Fill };
    Button btnBrowseInput = new Button { Text = "Browse...", AutoSize = true };

    ComboBox cboEngine = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };

    CheckBox chkRecurse = new CheckBox { Text = "Include subfolders", Checked = true, AutoSize = true };
    NumericUpDown numQuality = new NumericUpDown { Minimum = 50, Maximum = 100, Value = 90, Width = 60 };
    NumericUpDown numJobs = new NumericUpDown { Minimum = 1, Maximum = Math.Max(1, Environment.ProcessorCount), Value = AppSettings.DefaultJobs(), Width = 60 };

    TextBox txtRt = new TextBox { Dock = DockStyle.Fill };
    Button btnRt = new Button { Text = "Find rawtherapee-cli.exe", AutoSize = true };

    TextBox txtMagick = new TextBox { Dock = DockStyle.Fill };
    Button btnMagick = new Button { Text = "Find magick.exe", AutoSize = true };

    TextBox txtExif = new TextBox { Dock = DockStyle.Fill };
    Button btnExif = new Button { Text = "Find exiftool.exe", AutoSize = true };

    Button btnStart = new Button { Text = "Start", AutoSize = true };
    Button btnCancel = new Button { Text = "Cancel", Enabled = false, AutoSize = true };
    Button btnSave = new Button { Text = "Save paths", AutoSize = true };

    ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    TextBox txtLog = new TextBox { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, ReadOnly = true, Dock = DockStyle.Fill };

    CancellationTokenSource? cts;

    sealed class RunStats
    {
        public long ArwBytes, JpgBytes;
        public int Done, Converted, Failed, Larger;
        public readonly ConcurrentQueue<string> Jpegs = new();
    }

    sealed record ToolCommand(string Exe, string Args, string Note);

    sealed record ToolResult(int Exit, string StdOut, string StdErr)
    {
        public string All => (StdOut + StdErr).TrimEnd();
    }

    public MainForm()
    {
        Text = "ARW to JPEG Converter";
        Width = 920; Height = 680; MinimizeBox = true; MaximizeBox = true;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // labels
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // inputs
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // buttons

        int row = 0;
        void AddRow(string? label, Control main, Control? right = null, bool fill = false)
        {
            panel.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            if (label != null) panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            panel.Controls.Add(main, 1, row);
            if (right != null) panel.Controls.Add(right, 2, row);
            else panel.SetColumnSpan(main, 2);
            row++;
        }

        static Label InlineLabel(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(16, 6, 3, 3) };

        cboEngine.Items.AddRange(new object[]
        {
            "RawTherapee - best look: lens correction, camera-like tones (slower)",
            "ImageMagick - fastest, plain look",
        });

        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        options.Controls.Add(chkRecurse);
        options.Controls.Add(InlineLabel("JPEG quality"));
        options.Controls.Add(numQuality);
        options.Controls.Add(InlineLabel("Parallel jobs"));
        options.Controls.Add(numJobs);

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(740, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Quality 88-92 looks the same as 100 on photos at a fraction of the size. " +
                   "RawTherapee is about 3x slower than ImageMagick. Each parallel job can use 1-1.5 GB of RAM."
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(btnStart);
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnSave);

        AddRow("Input folder", txtInputDir, btnBrowseInput);
        AddRow("Engine", cboEngine);
        AddRow("Options", options);
        AddRow(null, hint);
        AddRow("rawtherapee-cli.exe", txtRt, btnRt);
        AddRow("magick.exe", txtMagick, btnMagick);
        AddRow("exiftool.exe", txtExif, btnExif);
        AddRow(null, buttons);
        AddRow(null, progress);
        AddRow(null, txtLog, fill: true);
        panel.RowCount = row;

        Controls.Add(panel);

        // Default tool paths: a "tools" folder next to the app, else an installed copy.
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        txtRt.Text = FindRawTherapee(toolsDir);
        txtMagick.Text = FindTool(toolsDir, "magick.exe");
        txtExif.Text = FindTool(toolsDir, "exiftool.exe");

        // Load saved settings
        var s = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(s.RawTherapeePath) && File.Exists(s.RawTherapeePath)) txtRt.Text = s.RawTherapeePath;
        if (!string.IsNullOrWhiteSpace(s.MagickPath) && File.Exists(s.MagickPath)) txtMagick.Text = s.MagickPath;
        if (!string.IsNullOrWhiteSpace(s.ExifPath) && File.Exists(s.ExifPath)) txtExif.Text = s.ExifPath;
        if (!string.IsNullOrWhiteSpace(s.LastInputDir) && Directory.Exists(s.LastInputDir)) txtInputDir.Text = s.LastInputDir;
        if (s.Quality >= numQuality.Minimum && s.Quality <= numQuality.Maximum) numQuality.Value = s.Quality;
        if (s.Jobs >= numJobs.Minimum && s.Jobs <= numJobs.Maximum) numJobs.Value = s.Jobs;
        chkRecurse.Checked = s.Recurse;
        // No saved engine yet: use RawTherapee when it is installed.
        cboEngine.SelectedIndex = Enum.TryParse<ConvertEngine>(s.Engine, out var saved) && Enum.IsDefined(saved)
            ? (int)saved
            : (int)(File.Exists(txtRt.Text) ? ConvertEngine.RawTherapee : ConvertEngine.ImageMagick);

        // Events
        btnBrowseInput.Click += (_, __) =>
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog(this) == DialogResult.OK) txtInputDir.Text = fbd.SelectedPath;
        };
        btnRt.Click += (_, __) => { var p = PickExe("rawtherapee-cli.exe"); if (!string.IsNullOrEmpty(p)) txtRt.Text = p; };
        btnMagick.Click += (_, __) => { var p = PickExe("magick.exe"); if (!string.IsNullOrEmpty(p)) txtMagick.Text = p; };
        btnExif.Click += (_, __) => { var p = PickExe("exiftool.exe"); if (!string.IsNullOrEmpty(p)) txtExif.Text = p; };
        btnStart.Click += async (_, __) => await StartAsync();
        btnCancel.Click += (_, __) => { cts?.Cancel(); Log("Canceling..."); };
        btnSave.Click += (_, __) => SaveCurrentSettings();
        FormClosing += (_, __) => { cts?.Cancel(); SaveCurrentSettings(); };
    }

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

    string PickExe(string title)
    {
        using var ofd = new OpenFileDialog { Filter = "Executable|*.exe", Title = "Find " + title };
        return ofd.ShowDialog(this) == DialogResult.OK ? ofd.FileName : string.Empty;
    }

    void Warn(string message) => MessageBox.Show(this, message, Text);

    async Task StartAsync()
    {
        SaveCurrentSettings();

        var input = txtInputDir.Text.Trim();
        var engine = (ConvertEngine)cboEngine.SelectedIndex;
        var rt = txtRt.Text.Trim();
        var magick = txtMagick.Text.Trim();
        var exif = txtExif.Text.Trim();
        if (string.IsNullOrWhiteSpace(input) || !Directory.Exists(input)) { Warn("Select a valid input folder"); return; }
        if (engine == ConvertEngine.RawTherapee && !File.Exists(rt)) { Warn("rawtherapee-cli.exe not found. Install RawTherapee, or switch the engine to ImageMagick."); return; }
        if (engine == ConvertEngine.ImageMagick && !File.Exists(magick)) { Warn("magick.exe not found"); return; }
        if (!File.Exists(exif)) { Warn("exiftool.exe not found"); return; }

        var profilesDir = "";
        if (engine == ConvertEngine.RawTherapee)
        {
            profilesDir = Path.Combine(Path.GetDirectoryName(rt)!, "profiles");
            var missing = IsoLevels.Select(l => ProfilePath(profilesDir, l)).FirstOrDefault(p => !File.Exists(p));
            if (missing != null) { Warn("RawTherapee profile not found:\n" + missing); return; }
        }

        int quality = (int)numQuality.Value;
        int jobs = (int)numJobs.Value;
        var searchOption = chkRecurse.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        SetRunning(true);
        txtLog.Clear();
        progress.Value = 0;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var stats = new RunStats();
        var clock = Stopwatch.StartNew();
        try
        {
            var files = await Task.Run(() => Directory.EnumerateFiles(input, "*.ARW", searchOption)
                                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                                      .ToList());
            if (files.Count == 0) { Log("No ARW files found"); return; }
            progress.Maximum = files.Count;
            Log($"Found {files.Count} ARW files. Engine: {engine}, quality {quality}, {jobs} parallel job(s).");

            Func<string, string, ToolCommand> command;
            string[] metaExclusions;
            if (engine == ConvertEngine.RawTherapee)
            {
                Log("Reading ISO values to pick RawTherapee's noise profile for each photo...");
                var isoByFile = await ReadIsoAsync(exif, files, token);
                command = (arw, outFile) => RawTherapeeCommand(rt, profilesDir, isoByFile, arw, outFile, quality);
                metaExclusions = RawTherapeeMetaExclusions;
            }
            else
            {
                command = (arw, outFile) => new ToolCommand(magick, $"{DecodeArgs} \"{arw}\" {JpegArgs} -quality {quality} \"{outFile}\"", "");
                metaExclusions = Array.Empty<string>();
            }

            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = token };
                await Parallel.ForEachAsync(files, options, (arw, ct) => ConvertOneAsync(arw, command, stats, ct));
            }
            catch (OperationCanceledException)
            {
                Log("Canceled. Copying metadata into the files already converted...");
            }

            // One exiftool run for all JPEGs instead of one per file: exiftool's start-up
            // time on Windows is often longer than the metadata copy itself.
            if (!stats.Jpegs.IsEmpty)
                await CopyMetadataAsync(exif, stats.Jpegs.ToList(), metaExclusions);

            LogSummary(stats, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            Log("Canceled");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
        }
        finally
        {
            SetRunning(false);
            cts?.Dispose();
            cts = null;
        }
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

    async ValueTask ConvertOneAsync(string arw, Func<string, string, ToolCommand> command, RunStats stats, CancellationToken ct)
    {
        var jpg = Path.ChangeExtension(arw, ".jpg");
        // Write to a temporary name and rename when complete, so a cancel or crash
        // never leaves a half-written .jpg that looks finished.
        var partial = Path.ChangeExtension(arw, ".partial.jpg");
        try
        {
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
            stats.Jpegs.Enqueue(jpg);

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
            Ui(() => progress.Value = Math.Min(done, progress.Maximum));
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

    async Task CopyMetadataAsync(string exif, List<string> jpegs, IEnumerable<string> exclusions)
    {
        Log($"Copying metadata into {jpegs.Count} JPEG(s) with one exiftool run...");
        var argFile = Path.Combine(Path.GetTempPath(), $"arw2jpg_{Guid.NewGuid():N}.args");
        try
        {
            // One argument per line. %d%f.ARW = the .ARW with the same name in the same folder.
            var lines = new List<string> { "-charset", "filename=utf8", "-tagsFromFile", "%d%f.ARW", "-all:all" };
            lines.AddRange(exclusions);
            lines.Add("-overwrite_original");
            lines.AddRange(jpegs);
            await File.WriteAllLinesAsync(argFile, lines, Utf8NoBom);

            var result = await RunToolAsync(exif, $"-@ \"{argFile}\"", CancellationToken.None);
            foreach (var line in result.All.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                Log("exiftool: " + line.Trim());
            if (result.Exit != 0) Log("exiftool reported errors (see above)");
        }
        finally
        {
            TryDelete(argFile);
        }
    }

    void LogSummary(RunStats s, TimeSpan elapsed)
    {
        Log($"Finished in {elapsed:hh\\:mm\\:ss}: {s.Converted} converted, {s.Failed} failed.");
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

    static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0.0} MB";

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    void SetRunning(bool running)
    {
        btnStart.Enabled = !running;
        btnCancel.Enabled = running;
        foreach (var c in new Control[] { txtInputDir, btnBrowseInput, cboEngine, chkRecurse, numQuality, numJobs,
                                          txtRt, btnRt, txtMagick, btnMagick, txtExif, btnExif, btnSave })
            c.Enabled = !running;
    }

    void SaveCurrentSettings()
    {
        var set = new AppSettings
        {
            Version = AppSettings.CurrentVersion,
            Engine = cboEngine.SelectedIndex >= 0 ? ((ConvertEngine)cboEngine.SelectedIndex).ToString() : null,
            RawTherapeePath = txtRt.Text?.Trim(),
            MagickPath = txtMagick.Text?.Trim(),
            ExifPath = txtExif.Text?.Trim(),
            LastInputDir = txtInputDir.Text?.Trim(),
            Quality = (int)numQuality.Value,
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

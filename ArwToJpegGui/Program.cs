// ARW to JPEG GUI
// Windows .exe without Python. Uses external tools: magick.exe (ImageMagick) and exiftool.exe.
// Place portable copies in a "tools" folder next to the built .exe, or set paths in the UI.
// Build: dotnet 8 SDK -> dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=false

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public class MainForm : Form
{
    TextBox txtInputDir = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    Button btnBrowseInput = new Button { Text = "Browse..." };
    CheckBox chkRecurse = new CheckBox { Text = "Include subfolders", Checked = true };
    Label lblQuality = new Label { Text = "JPEG quality" };
    NumericUpDown numQuality = new NumericUpDown { Minimum = 60, Maximum = 100, Value = 100 };

    TextBox txtMagick = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    Button btnMagick = new Button { Text = "Find magick.exe" };
    TextBox txtExif = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    Button btnExif = new Button { Text = "Find exiftool.exe" };

    Button btnStart = new Button { Text = "Start" };
    Button btnCancel = new Button { Text = "Cancel", Enabled = false };
    ProgressBar progress = new ProgressBar { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, Minimum = 0, Maximum = 100 };
    TextBox txtLog = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom };

    CancellationTokenSource? cts;

    public MainForm()
    {
        Text = "ARW to JPEG Converter";
        Width = 900; Height = 600; MinimizeBox = true; MinimizeBox = true;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 8, Padding = new Padding(10) };
        for (int i = 0; i < 5; i++) panel.ColumnStyles.Add(new ColumnStyle(i == 4 ? SizeType.AutoSize : SizeType.Percent, i == 4 ? 0 : 25));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // input
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // opts
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // magick
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // exif
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // progress
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // footer

        // Input row
        panel.Controls.Add(new Label { Text = "Input folder", AutoSize = true }, 0, 0);
        panel.SetColumnSpan(txtInputDir, 3);
        panel.Controls.Add(txtInputDir, 1, 0);
        panel.Controls.Add(btnBrowseInput, 4, 0);

        // Options
        panel.Controls.Add(chkRecurse, 1, 1);
        panel.Controls.Add(lblQuality, 2, 1);
        panel.Controls.Add(numQuality, 3, 1);

        // magick
        panel.Controls.Add(new Label { Text = "magick.exe", AutoSize = true }, 0, 2);
        panel.SetColumnSpan(txtMagick, 3);
        panel.Controls.Add(txtMagick, 1, 2);
        panel.Controls.Add(btnMagick, 4, 2);

        // exiftool
        panel.Controls.Add(new Label { Text = "exiftool.exe", AutoSize = true }, 0, 3);
        panel.SetColumnSpan(txtExif, 3);
        panel.Controls.Add(txtExif, 1, 3);
        panel.Controls.Add(btnExif, 4, 3);

        // Start/Cancel
        panel.Controls.Add(btnStart, 1, 4);
        panel.Controls.Add(btnCancel, 2, 4);
        panel.Controls.Add(btnSave, 3, 4);

        // Progress
        panel.SetColumnSpan(progress, 4);
        panel.Controls.Add(progress, 1, 5);

        // Log
        panel.SetColumnSpan(txtLog, 4);
        txtLog.Height = 340;
        panel.Controls.Add(txtLog, 1, 6);

        Controls.Add(panel);

        // Defaults: look in tools folder
        var baseDir = AppContext.BaseDirectory;
        var toolsDir = Path.Combine(baseDir, "tools");
        var defaultMagick = Path.Combine(toolsDir, "magick.exe");
        var defaultExif = Path.Combine(toolsDir, "exiftool.exe");
        if (File.Exists(defaultMagick)) txtMagick.Text = defaultMagick;
        if (File.Exists(defaultExif)) txtExif.Text = defaultExif;

        btnBrowseInput.Click += (_, __) =>
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog(this) == DialogResult.OK) txtInputDir.Text = fbd.SelectedPath;
        };
        btnMagick.Click += (_, __) => { var p = PickExe("magick.exe"); if (!string.IsNullOrEmpty(p)) txtMagick.Text = p; };
        btnExif.Click += (_, __) => { var p = PickExe("exiftool.exe"); if (!string.IsNullOrEmpty(p)) txtExif.Text = p; };
        btnStart.Click += async (_, __) => await StartAsync();
        btnCancel.Click += (_, __) => cts?.Cancel();
    }

    string PickExe(string title)
    {
        using var ofd = new OpenFileDialog { Filter = "Executable|*.exe", Title = "Find " + title };
        return ofd.ShowDialog(this) == DialogResult.OK ? ofd.FileName : string.Empty;
    }

    async Task StartAsync()
    {
        var input = txtInputDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(input) || !Directory.Exists(input)) { MessageBox.Show("Select a valid input folder"); return; }
        if (!File.Exists(txtMagick.Text)) { MessageBox.Show("magick.exe not found"); return; }
        if (!File.Exists(txtExif.Text)) { MessageBox.Show("exiftool.exe not found"); return; }

        btnStart.Enabled = false; btnCancel.Enabled = true; progress.Value = 0; txtLog.Clear();
        cts = new CancellationTokenSource();
        try
        {
            var files = Directory.EnumerateFiles(input, "*.ARW", chkRecurse.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                                  .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                  .ToList();
            if (files.Count == 0) { Log("No ARW files found"); return; }
            Log($"Found {files.Count} ARW files");

            int done = 0;
            foreach (var arw in files)
            {
                cts.Token.ThrowIfCancellationRequested();
                var jpg = Path.ChangeExtension(arw, ".jpg");
                Log($"Converting: {arw}");
                var magickArgs = $"\"{arw}\" -quality {(int)numQuality.Value} \"{jpg}\"";
                var ok1 = await RunTool(txtMagick.Text, magickArgs, cts.Token);
                if (!ok1) { Log("magick failed"); continue; }

                Log($"Copying metadata -> {Path.GetFileName(jpg)}");
                var exifArgs = $"-TagsFromFile \"{arw}\" -all:all -overwrite_original \"{jpg}\"";
                var ok2 = await RunTool(txtExif.Text, exifArgs, cts.Token);
                if (!ok2) { Log("exiftool failed"); }

                done++;
                progress.Value = (int)Math.Round((double)done * 100 / files.Count);
            }
            Log("All done");
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
            btnStart.Enabled = true; btnCancel.Enabled = false; cts?.Dispose(); cts = null;
        }
    }

    async Task<bool> RunTool(string exe, string args, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
        p.Exited += (_, __) => tcs.TrySetResult(p.ExitCode);
        if (!p.Start()) return false;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
        {
            var exit = await tcs.Task.ConfigureAwait(false);
            return exit == 0;
        }
    }

    void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(Log), msg); return; }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }
}

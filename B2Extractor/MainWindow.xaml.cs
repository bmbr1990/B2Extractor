using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

namespace B2IndexExtractor
{
    public partial class MainWindow : Window
    {
        private string? _logFilePath;
        public MainWindow() { InitializeComponent(); }

        private void BrowseIndex_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "B2 Index (*.b2index)|*.b2index|All Files (*.*)|*.*",
                Title = "Choose .b2index file"
            };
            if (ofd.ShowDialog() == true)
            {
                IndexPathBox.Text = ofd.FileName;
                if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
                    OutputPathBox.Text = System.IO.Path.GetDirectoryName(ofd.FileName) ?? "";
            }
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            fbd.Description = "Choose output directory...";
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                OutputPathBox.Text = fbd.SelectedPath;
        }

        private async void ExtractBtn_Click(object sender, RoutedEventArgs e)
        {
            ExtractBtn.IsEnabled = false;
            Progress.Value = 0;
            LogBox.Clear();
            try
            {
                var indexPath = IndexPathBox.Text.Trim();
                var outDir = OutputPathBox.Text.Trim();
                if (!File.Exists(indexPath)) { AppendLog("❌ Could not find .b2index file"); return; }
                if (string.IsNullOrWhiteSpace(outDir)) { AppendLog("❌ No output directory provided"); return; }
                Directory.CreateDirectory(outDir);

                if (!NativeMethods.EnsureOodleLoaded())
                    AppendLog("⚠️ Could not preload oo2core_7_win64.dll. if it doesnot exist, decompression will fail.");

                var options = new ExtractOptions
                {
                    OutputDirectory = outDir,
                    SkipWemFiles = SkipWemCheckBox.IsChecked == true,
                    SkipBinkFiles = SkipBinkCheckBox.IsChecked == true,
                    SkipExistingFiles = true, 
                    EnableHeaderPath = UseHeaderPath.IsChecked == true,
                    EnableContentPath = UseContentHeuristic.IsChecked == true,
                    SkipResAndAce = SkipResAceCheckBox.IsChecked == true,
                    SkipConfigFiles = SkipConfigsCheckBox.IsChecked == true,
                    OnlyAssets = OnlyAssetsCheckBox.IsChecked == true,
                    Progress = (p) => Dispatcher.Invoke(() => Progress.Value = p),
                    Logger = (msg) => Dispatcher.Invoke(() => AppendLog(msg)),
                };

                _logFilePath = Path.Combine(outDir, "extract_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

                await Task.Run(() => B2Extractor.ExtractAll(indexPath, options));
                AppendLog("✅ Finished.");
            }
            catch (Exception ex)
            {
                AppendLog("💥 Critical Error: " + ex);
            }
            finally
            {
                ExtractBtn.IsEnabled = true;
                Progress.Value = 100;
            }
        }

        private void AppendLog(string msg)
        {
            const int maxLen = 200;
            if (msg.Length > maxLen)
                msg = msg.Substring(0, maxLen) + "…";

            string line = $"[{DateTime.Now:HH: mm: ss}] {msg}{Environment.NewLine}";

            // Save log in output directory
            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    File.AppendAllText(_logFilePath, line);
                }
                catch { /* ignore save error */ }
            }

            // UI holds 20 recent lines - prevents textbox from eating too much memory
            LogBox.AppendText(line);
            var lines = LogBox.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            if (lines.Length > 20)
            {
                string trimmed = string.Join(Environment.NewLine, lines.Skip(lines.Length - 20));
                LogBox.Text = trimmed;
                LogBox.CaretIndex = LogBox.Text.Length;
            }
            LogBox.ScrollToEnd();
        }

    }
}

using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static System.Windows.Forms.Design.AxImporter;

namespace B2IndexExtractor
{
    public partial class MainWindow : Window
    {
        public enum LogLevel
        {
            Full,       // Log Everything. - wszystkie logi
            Warnings,   // Cumulatively log warnings every 100 files. - błędy + zbiorczo co 100 plików
            Error,      // Cumulatively log errors every 1000 files. - błędy + zbiorczo co 1000
            Minimal,    // Cumulatively log errors every 10000 files. - błędy + zbiorczo co 10000
            Silent,     // Only log errors, and system messages. - tylko błędy + start/stop
            None        // No logging - nic
        }

        private ExtractOptions? ExtractOptions;

        private static int _logCounter = 0;

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
                {
                    OutputPathBox.Text = System.IO.Path.GetDirectoryName(ofd.FileName) + "\\Extracted" ?? "";
                }
            }
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            fbd.Description = "Choose output directory...";
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                OutputPathBox.Text = fbd.SelectedPath;
        }
        private void OnlyAssetsCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            // disable skip checkboxes
            SkipWemCheckBox.IsChecked = true;
            SkipWemCheckBox.IsEnabled = false;
            SkipBinkCheckBox.IsChecked = true;
            SkipBinkCheckBox.IsEnabled = false;
            SkipResAceCheckBox.IsChecked = true;
            SkipResAceCheckBox.IsEnabled = false;
            SkipConfigsCheckBox.IsChecked = true;
            SkipConfigsCheckBox.IsEnabled = false;
        }

        private void OnlyAssetsCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            // re-enable skip checkboxes
            SkipWemCheckBox.IsEnabled = true;
            SkipWemCheckBox.IsChecked = false;
            SkipBinkCheckBox.IsEnabled = true;
            SkipBinkCheckBox.IsChecked = false;
            SkipResAceCheckBox.IsEnabled = true;
            SkipResAceCheckBox.IsChecked = false;
            SkipConfigsCheckBox.IsEnabled = true;
            SkipConfigsCheckBox.IsChecked = false;
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

                ExtractOptions = new ExtractOptions
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
                    LogLevel = (LogLevel)LogLevelComboBox.SelectedIndex
                };

                _logFilePath = Path.Combine(outDir, "extract_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

                await Task.Run(() => B2Extractor.ExtractAll(indexPath, ExtractOptions));
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
            _logCounter++;
            bool isError = msg.StartsWith("❌") || msg.StartsWith("💥");
            bool isWarning = msg.StartsWith("⚠️");
            bool bCanLog = false;
            switch (ExtractOptions?.LogLevel)
            {
                case LogLevel.None:
                    bCanLog = false;
                    break;

                case LogLevel.Silent:
                    if (isError || isWarning || msg.StartsWith("✅") || msg.StartsWith("❌") || msg.StartsWith("💥"))
                        bCanLog = true;
                    break;

                case LogLevel.Minimal:
                    if (isError || isWarning || _logCounter % 10000 == 0 || msg.StartsWith("✅") || msg.StartsWith("❌"))
                    {
                        msg += $" Extracted 10000 files";
                        bCanLog = true;
                    }
                    break;

                case LogLevel.Error:
                    if (isError || isWarning || _logCounter % 1000 == 0 || msg.StartsWith("✅") || msg.StartsWith("❌"))
                    {
                        msg += $" Extracted 1000 files";
                        bCanLog = true;
                    }
                    break;

                case LogLevel.Warnings:
                    if (isError || isWarning || _logCounter % 100 == 0 || msg.StartsWith("✅") || msg.StartsWith("❌"))
                    {
                        msg += $" Extracted 100 files";
                        bCanLog = true;
                    }
                    break;

                case LogLevel.Full:
                default:
                    bCanLog = true;
                    break;
            }
            if (bCanLog)
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
                if (ExtractOptions?.LogLevel != LogLevel.Silent)
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
}

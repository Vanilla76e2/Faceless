using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Faceless
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<FileItem> _files = [];
        private string? _outputFolder = null; // null = рядом с оригиналом

        public MainWindow()
        {
            InitializeComponent();
            FileListBox.ItemsSource = _files;
            UpdateSuffixCheckBox(); // папка не выбрана → суффикс заблокирован
        }

        // ─── Настройки вывода ────────────────────────────────────────────────

        private void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Выберите папку для сохранения результатов"
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                _outputFolder = dlg.FolderName;
                OutputPathLabel.Text       = _outputFolder;
                OutputPathLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
                ClearOutputFolderButton.Visibility = Visibility.Visible;
                UpdateSuffixCheckBox();
            }
        }

        private void ClearOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            _outputFolder = null;
            OutputPathLabel.Text       = "Рядом с оригиналом";
            OutputPathLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            ClearOutputFolderButton.Visibility = Visibility.Collapsed;
            UpdateSuffixCheckBox();
        }

        /// <summary>
        /// Если папка не выбрана — суффикс обязателен (чекбокс включён и заблокирован).
        /// Если папка выбрана — чекбокс свободен.
        /// </summary>
        private void UpdateSuffixCheckBox()
        {
            if (_outputFolder == null)
            {
                AddSuffixCheckBox.IsChecked = true;
                AddSuffixCheckBox.IsEnabled = false;
            }
            else
            {
                AddSuffixCheckBox.IsEnabled = true;
            }
        }

        // ─── Drag & Drop ──────────────────────────────────────────────────────

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
                DropZone.Background  = new SolidColorBrush(Color.FromArgb(0x20, 0x7C, 0x3A, 0xED));
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropZoneStyle();
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            ResetDropZoneStyle();

            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            AddFiles(paths);
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e)
        {
            if (_files.Count == 0)
                OpenFilePickerDialog();
        }

        private void AddMore_Click(object sender, MouseButtonEventArgs e)
        {
            OpenFilePickerDialog();
            e.Handled = true;
        }

        // ─── Кнопки ───────────────────────────────────────────────────────────

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            UpdateUI();
        }

        private async void ProcessButton_Click(object sender, RoutedEventArgs e)
        {
            // Снимаем настройки до запуска (чтобы параллельные задачи читали одно и то же)
            bool addSuffix    = AddSuffixCheckBox.IsChecked == true;
            string? outFolder = _outputFolder;

            SetProcessingState(true);

            int total   = _files.Count;
            int current = 0;

            var tasks = _files
                .Where(f => f.Status != FileStatus.Unsupported)
                .Select(item => Task.Run(() =>
                {
                    item.Status     = FileStatus.Processing;
                    item.StatusText = "Обработка...";

                    try
                    {
                        var output = MetadataCleaner.Clean(item.FilePath, outFolder, addSuffix);
                        item.Status     = FileStatus.Done;
                        item.StatusText = $"✓ {Path.GetFileName(output)}";
                    }
                    catch (Exception ex)
                    {
                        item.Status     = FileStatus.Error;
                        item.StatusText = $"Ошибка: {ex.Message}";
                    }

                    Interlocked.Increment(ref current);
                    Dispatcher.Invoke(() =>
                        ProgressBar.Value = (double)current / total * 100);
                })).ToList();

            await Task.WhenAll(tasks);

            SetProcessingState(false);
        }

        // ─── Вспомогательные методы ───────────────────────────────────────────

        private void OpenFilePickerDialog()
        {
            var dlg = new OpenFileDialog
            {
                Title       = "Выберите файлы для обработки",
                Multiselect = true,
                Filter      = "Поддерживаемые файлы|*.docx;*.xlsx;*.pptx;*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff"
                            + "|Документы Office|*.docx;*.xlsx;*.pptx"
                            + "|PDF|*.pdf"
                            + "|Изображения|*.jpg;*.jpeg;*.png;*.tif;*.tiff"
                            + "|Все файлы|*.*"
            };

            if (dlg.ShowDialog() == true)
                AddFiles(dlg.FileNames);
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            var existing = _files.Select(f => f.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                if (existing.Contains(path)) continue;

                var item = new FileItem
                {
                    FilePath  = path,
                    FileName  = Path.GetFileName(path),
                    Extension = Path.GetExtension(path)
                };

                if (!MetadataCleaner.IsSupported(path))
                {
                    item.Status     = FileStatus.Unsupported;
                    item.StatusText = "Формат не поддерживается";
                }
                else
                {
                    item.StatusText = "Ожидание...";
                }

                _files.Add(item);
                existing.Add(path);
            }

            UpdateUI();
        }

        private void UpdateUI()
        {
            bool hasFiles     = _files.Count > 0;
            bool hasSupported = _files.Any(f => f.Status != FileStatus.Unsupported);

            PlaceholderGrid.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
            FileListGrid.Visibility    = hasFiles ? Visibility.Visible   : Visibility.Collapsed;

            ProcessButton.IsEnabled = hasFiles && hasSupported;
            ClearButton.IsEnabled   = hasFiles;

            CountLabel.Text = _files.Count switch
            {
                0 => "Файлов не выбрано",
                1 => "1 файл выбран",
                _ => $"{_files.Count} файлов выбрано"
            };
        }

        private void SetProcessingState(bool processing)
        {
            ProgressBar.Visibility = processing ? Visibility.Visible : Visibility.Collapsed;

            if (!processing)
                ProgressBar.Value = 0;

            ProcessButton.IsEnabled = !processing;
            ClearButton.IsEnabled   = !processing;
            DropZone.AllowDrop      = !processing;

            CountLabel.Text = processing
                ? "Обработка файлов..."
                : $"{_files.Count} файлов выбрано";
        }

        private void ResetDropZoneStyle()
        {
            DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            DropZone.Background  = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x1F));
        }
    }
}

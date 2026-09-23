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
        private readonly List<MetadataField> _editorFields = [];
        private string? _outputFolder = null; // null = рядом с оригиналом
        private bool _editMode;
        private bool _busy;
        private string? _editorPath;
        private string? _notice;
        private string? _noticePath;

        public MainWindow()
        {
            InitializeComponent();
            FileListBox.ItemsSource = _files;
            ApplyMode();
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

        private void CleanMode_Click(object sender, RoutedEventArgs e)
        {
            if (_editMode)
                SetMode(false);
        }

        private void EditMode_Click(object sender, RoutedEventArgs e)
        {
            if (!_editMode)
                SetMode(true);
        }

        private void SetMode(bool edit)
        {
            _editMode = edit;
            _notice = null;
            _noticePath = null;
            ApplyMode();
        }

        private void ApplyMode()
        {
            StyleModeButton(ModeCleanButton, !_editMode);
            StyleModeButton(ModeEditButton, _editMode);
            SubtitleText.Text = _editMode
                ? "Показывает метаданные файла и сохраняет изменения"
                : "Удаляет информацию об авторах из метаданных файлов";
            AddSuffixCheckBox.Content = _editMode
                ? "Добавлять суффикс _edited к имени файла"
                : "Добавлять суффикс _faceless к имени файла";
            ProcessButton.Content = _editMode ? "Сохранить" : "Удалить авторов";
            ClearButton.Content = _editMode ? "Закрыть" : "Очистить";
            UpdateSuffixCheckBox();
            UpdateUI();
        }

        private static void StyleModeButton(System.Windows.Controls.Button button, bool selected)
        {
            if (selected)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
                button.Foreground = Brushes.White;
                button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
                button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            }
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
            if (_busy)
                return;

            if (_editMode)
            {
                if (_editorPath == null)
                    OpenFilePickerDialog();
                return;
            }

            if (_files.Count == 0)
                OpenFilePickerDialog();
        }

        private void ReplaceEditorFile_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_busy)
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
            _notice = null;
            _noticePath = null;
            if (_editMode)
            {
                CloseEditor();
                UpdateUI();
                return;
            }

            _files.Clear();
            UpdateUI();
        }

        private async void ProcessButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editMode)
            {
                await SaveEditorAsync();
                return;
            }
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
                Title       = _editMode ? "Выберите файл" : "Выберите файлы для обработки",
                Multiselect = !_editMode,
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
            var list = paths.ToList();
            if (_editMode)
            {
                var file = list.FirstOrDefault(File.Exists);
                if (file == null)
                {
                    if (list.Any(Directory.Exists))
                        EditorMessage.Text = "Перетащите файл, а не папку.";
                    return;
                }

                _ = LoadEditorAsync(file, list.Count(File.Exists) > 1);
                return;
            }

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

        private async Task LoadEditorAsync(string path, bool openedFirstOfMany)
        {
            if (_busy)
                return;

            _notice = null;
            _noticePath = null;
            EditorMessage.Text = "";

            if (!MetadataCleaner.IsSupported(path))
            {
                CloseEditor();
                EditorMessage.Text = "Формат не поддерживается";
                UpdateUI();
                return;
            }

            SetBusy(true, "Чтение метаданных...");
            try
            {
                var fields = await Task.Run(() => MetadataEditor.Read(path));
                _editorPath = path;
                _editorFields.Clear();
                _editorFields.AddRange(fields);
                EditorFileName.Text = Path.GetFileName(path);
                EditorFileName.ToolTip = path;
                EditorFileMeta.Text = openedFirstOfMany
                    ? "Открыт первый из нескольких файлов · " + FieldsCount(fields.Count)
                    : FieldsCount(fields.Count);
                ShowEditorRows(fields);
            }
            catch (Exception ex)
            {
                CloseEditor();
                EditorMessage.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task SaveEditorAsync()
        {
            if (_busy || _editorPath == null)
                return;

            bool addSuffix = AddSuffixCheckBox.IsChecked == true;
            string? outFolder = _outputFolder;
            string source = _editorPath;
            var fields = _editorFields.ToList();

            _notice = null;
            _noticePath = null;
            SetBusy(true, "Сохранение...");
            string? saved = null;
            Exception? error = null;
            try
            {
                saved = await Task.Run(() => MetadataEditor.Save(source, fields, outFolder, addSuffix));
                foreach (var field in _editorFields)
                    field.AcceptChanges();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                SetBusy(false);
            }

            if (error != null)
            {
                MessageBox.Show(this, error.Message, "Не удалось сохранить", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (saved != null)
            {
                _notice = "Сохранено: " + Path.GetFileName(saved);
                _noticePath = saved;
                UpdateUI();
            }
        }

        private void CloseEditor()
        {
            _editorPath = null;
            _editorFields.Clear();
            MetadataItems.ItemsSource = null;
            EditorFileName.Text = "";
            EditorFileMeta.Text = "";
        }

        private void ShowEditorRows(List<MetadataField> fields)
        {
            var rows = new List<object>();
            foreach (var group in fields.GroupBy(f => f.Group))
            {
                rows.Add(new MetadataGroupHeader { Title = group.Key });
                rows.AddRange(group);
            }

            MetadataItems.ItemsSource = rows;
            MetadataScroll.ScrollToTop();
        }

        private static string FieldsCount(int count)
        {
            int n100 = count % 100;
            int n10 = count % 10;
            string word = n100 is >= 11 and <= 14
                ? "полей"
                : n10 == 1
                    ? "поле"
                    : n10 is >= 2 and <= 4
                        ? "поля"
                        : "полей";
            return count == 0 ? "Метаданные не найдены" : $"{count} {word}";
        }

        private void UpdateUI()
        {
            if (_busy)
                return;

            bool edit = _editMode;
            EditPanel.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;

            if (edit)
            {
                PlaceholderGrid.Visibility = Visibility.Collapsed;
                FileListGrid.Visibility = Visibility.Collapsed;
                bool open = _editorPath != null;
                EditorPlaceholder.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
                EditorFieldsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                ProcessButton.IsEnabled = open;
                ClearButton.IsEnabled = open;
                CountLabel.Text = _notice ?? (open
                    ? FieldsCount(_editorFields.Count)
                    : "Файл не выбран");
                CountLabel.ToolTip = _noticePath;
                return;
            }

            bool hasFiles     = _files.Count > 0;
            bool hasSupported = _files.Any(f => f.Status != FileStatus.Unsupported);

            PlaceholderGrid.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
            FileListGrid.Visibility    = hasFiles ? Visibility.Visible   : Visibility.Collapsed;

            ProcessButton.IsEnabled = hasFiles && hasSupported;
            ClearButton.IsEnabled   = hasFiles;

            CountLabel.ToolTip = _noticePath;
            CountLabel.Text = _notice ?? (_files.Count switch
            {
                0 => "Файлов не выбрано",
                1 => "1 файл выбран",
                _ => $"{_files.Count} файлов выбрано"
            });
        }

        private void SetProcessingState(bool processing)
        {
            SetBusy(processing, processing ? "Обработка файлов..." : null);
            if (processing)
                ProgressBar.IsIndeterminate = false;
        }

        private void SetBusy(bool busy, string? message = null)
        {
            _busy = busy;
            ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ProgressBar.IsIndeterminate = busy && _editMode;
            if (!busy)
                ProgressBar.Value = 0;

            DropZone.AllowDrop = !busy;
            OutputSettingsBorder.IsEnabled = !busy;
            ModeCleanButton.IsEnabled = !busy;
            ModeEditButton.IsEnabled = !busy;
            MetadataScroll.IsEnabled = !busy;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;

            if (busy)
            {
                ProcessButton.IsEnabled = false;
                ClearButton.IsEnabled = false;
                if (message != null)
                    CountLabel.Text = message;
                return;
            }

            UpdateUI();
        }

        private void ResetDropZoneStyle()
        {
            DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            DropZone.Background  = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x1F));
        }
    }
}

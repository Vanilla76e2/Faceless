using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Faceless
{
    public enum FileStatus
    {
        Pending,
        Processing,
        Done,
        Error,
        Unsupported
    }

    public class FileItem : INotifyPropertyChanged
    {
        private FileStatus _status = FileStatus.Pending;
        private string _statusText = "Ожидание...";

        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;

        public string Icon => Extension.ToLowerInvariant() switch
        {
            ".pdf"  => "📄",
            ".docx" => "📝",
            ".xlsx" => "📊",
            ".pptx" => "📑",
            ".jpg" or ".jpeg" => "🖼",
            ".png"  => "🖼",
            ".tif" or ".tiff" => "🖼",
            _       => "📁"
        };

        public FileStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusColor));
            }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public string StatusIcon => Status switch
        {
            FileStatus.Pending     => "🕐",
            FileStatus.Processing  => "⏳",
            FileStatus.Done        => "✅",
            FileStatus.Error       => "❌",
            FileStatus.Unsupported => "⚠️",
            _                      => ""
        };

        public string StatusColor => Status switch
        {
            FileStatus.Done        => "#4ADE80",
            FileStatus.Error       => "#F87171",
            FileStatus.Unsupported => "#FBBF24",
            _                      => "#6B7280"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

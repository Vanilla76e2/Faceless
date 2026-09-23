using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Faceless
{
    /// <summary>Одно поле метаданных, которое можно показать и, если разрешено, изменить.</summary>
    public sealed class MetadataField : INotifyPropertyChanged
    {
        private string _value = "";

        public string Id { get; init; } = "";
        public string Group { get; init; } = "";
        public string Name { get; init; } = "";
        public string TechnicalName { get; init; } = "";
        public string Kind { get; init; } = FieldKind.Text;
        public bool IsReadOnly { get; init; }
        public string OriginalValue { get; set; } = "";

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                    return;
                _value = value;
                OnPropertyChanged();
            }
        }

        public string ToolTipText =>
            IsReadOnly
                ? string.IsNullOrEmpty(TechnicalName)
                    ? "Только для чтения"
                    : TechnicalName + " — только для чтения"
                : TechnicalName;

        public bool IsChanged =>
            !IsReadOnly && Normalize(Value) != Normalize(OriginalValue);

        public void AcceptChanges() => OriginalValue = Value;

        public static string Normalize(string? text) =>
            (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class MetadataGroupHeader
    {
        public string Title { get; init; } = "";
    }

    public static class FieldKind
    {
        public const string Text = "text";
        public const string Ascii = "ascii";
        public const string Utf16 = "utf16";
        public const string UserCommentLe = "uc-le";
        public const string UserCommentBe = "uc-be";
        public const string UserCommentAscii = "uc-ascii";
        public const string Byte = "byte";
        public const string UShort = "ushort";
        public const string ULong = "ulong";
        public const string SShort = "sshort";
        public const string SLong = "slong";
        public const string Rational = "rational";
        public const string SRational = "srational";
        public const string Float = "float";
        public const string Double = "double";
        public const string PdfDate = "pdf-date";
        public const string Xmp = "xmp";
        public const string JpegCom = "jpeg-com";
        public const string Iptc = "iptc";
        public const string ReadOnly = "readonly";
    }
}

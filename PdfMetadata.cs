using System.Globalization;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Faceless
{
    /// <summary>Информационный словарь PDF и, если он есть, пакет XMP.</summary>
    static class PdfMetadata
    {
        private static readonly (string Key, string Name, string Kind)[] Standard =
        [
            ("/Title", "Название", FieldKind.Text),
            ("/Author", "Автор", FieldKind.Text),
            ("/Subject", "Тема", FieldKind.Text),
            ("/Keywords", "Ключевые слова", FieldKind.Text),
            ("/Creator", "Программа", FieldKind.Text),
            ("/Producer", "Создатель PDF", FieldKind.Text),
            ("/CreationDate", "Создан", FieldKind.PdfDate),
            ("/ModDate", "Изменён", FieldKind.PdfDate)
        ];

        private static readonly string[] DateFormats =
        [
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "dd.MM.yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm",
            "dd.MM.yyyy"
        ];

        public static List<MetadataField> Read(string path)
        {
            using var document = Open(path);
            var info = document.Info.Elements;
            var fields = new List<MetadataField>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (key, name, kind) in Standard)
            {
                seen.Add(key);
                string value = kind == FieldKind.PdfDate
                    ? ReadDate(info, key)
                    : ReadString(info, key);
                var readOnly = key == "/Producer";
                var technical = readOnly
                    ? "/Producer · при сохранении сюда дописывается PDFsharp"
                    : key;
                fields.Add(Make("pdf:" + key, "PDF", name, technical, kind, value, readOnly));
            }

            foreach (var key in info.Keys)
            {
                if (seen.Contains(key) || key is "/Trapped")
                    continue;
                string value;
                try
                {
                    value = info.GetString(key);
                }
                catch (InvalidCastException)
                {
                    continue;
                }

                var label = key.TrimStart('/');
                fields.Add(Make("pdf:" + key, "PDF", label, key, FieldKind.Text, value, readOnly: false));
            }

            if (TryReadXmp(document, out var xml) && xml != null)
                fields.AddRange(XmpMetadata.Read(xml, "pdf"));

            return fields;
        }

        public static void Write(string source, string target, IReadOnlyList<MetadataField> fields)
        {
            var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                FileIo.CopyShared(source, temp);
                using (var document = PdfReader.Open(temp, PdfDocumentOpenMode.Modify))
                {
                    ApplyInfo(document, fields);
                    ApplyXmp(document, fields);
                    document.Save(target);
                }
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        private static void ApplyInfo(PdfDocument document, IReadOnlyList<MetadataField> fields)
        {
            foreach (var field in fields.Where(f => f.IsChanged && f.Id.StartsWith("pdf:", StringComparison.Ordinal)))
            {
                var key = field.Id["pdf:".Length..];
                var text = MetadataField.Normalize(field.Value);
                if (field.Kind == FieldKind.PdfDate)
                {
                    if (text.Length == 0)
                    {
                        if (document.Info.Elements.ContainsKey(key))
                            document.Info.Elements.Remove(key);
                        continue;
                    }

                    if (!TryParseDate(text, out var date))
                        throw new FormatException($"Поле «{field.Name}»: ожидается дата, например 2026-09-23 15:04:00.");

                    if (key == "/CreationDate")
                        document.Info.CreationDate = date;
                    else if (key == "/ModDate")
                        document.Info.ModificationDate = date;
                    else
                        document.Info.Elements.SetDateTime(key, date);
                    continue;
                }

                switch (key)
                {
                    case "/Title":
                        document.Info.Title = text;
                        break;
                    case "/Author":
                        document.Info.Author = text;
                        break;
                    case "/Subject":
                        document.Info.Subject = text;
                        break;
                    case "/Keywords":
                        document.Info.Keywords = text;
                        break;
                    case "/Creator":
                        document.Info.Creator = text;
                        break;
                    default:
                        document.Info.Elements[key] = new PdfString(text, PdfStringEncoding.Unicode);
                        break;
                }
            }
        }

        private static void ApplyXmp(PdfDocument document, IReadOnlyList<MetadataField> fields)
        {
            if (!fields.Any(f => f.IsChanged && f.Id.StartsWith("xmp:pdf#", StringComparison.Ordinal)))
                return;
            if (!TryReadXmp(document, out var xml) || xml == null)
                throw new InvalidDataException("Не удалось прочитать XMP PDF.");

            var updated = XmpMetadata.Apply(xml, fields, "pdf");
            if (updated == null)
                return;

            var catalog = document.Internals.Catalog;
            if (!catalog.Elements.TryGetValue("/Metadata", out var item) || item == null)
                return;

            if (Resolve(item) is not { } meta || meta.Stream == null)
                return;

            meta.Stream.TryUncompress();
            if (meta.Elements.ContainsKey("/Filter"))
                meta.Elements.Remove("/Filter");
            if (meta.Elements.ContainsKey("/DecodeParms"))
                meta.Elements.Remove("/DecodeParms");
            meta.Stream.Value = System.Text.Encoding.UTF8.GetBytes(updated);
        }

        private static PdfDocument Open(string path) =>
            PdfReader.Open(path, PdfDocumentOpenMode.Import);

        private static string ReadString(PdfDictionary.DictionaryElements info, string key)
        {
            if (!info.ContainsKey(key))
                return "";
            try
            {
                return info.GetString(key);
            }
            catch (InvalidCastException)
            {
                return "";
            }
        }

        private static string ReadDate(PdfDictionary.DictionaryElements info, string key)
        {
            if (!info.ContainsKey(key))
                return "";
            var date = info.GetDateTime(key, DateTime.MinValue);
            if (date == DateTime.MinValue)
                return "";
            return date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static bool TryParseDate(string text, out DateTime date)
        {
            if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date))
                return true;
            return DateTime.TryParse(text, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AssumeLocal, out date);
        }

        private static bool TryReadXmp(PdfDocument document, out string? xml)
        {
            xml = null;
            try
            {
                var catalog = document.Internals.Catalog;
                if (!catalog.Elements.TryGetValue("/Metadata", out var item) || item == null)
                    return false;
                if (Resolve(item) is not { } meta || meta.Stream == null)
                    return false;

                var bytes = meta.Stream.UnfilteredValue;
                if (bytes == null || bytes.Length == 0)
                    return false;
                xml = System.Text.Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static PdfDictionary? Resolve(PdfItem item)
        {
            if (item is PdfReference reference)
                return reference.Value as PdfDictionary;
            return item as PdfDictionary;
        }

        private static MetadataField Make(string id, string group, string name, string technical, string kind, string value, bool readOnly) =>
            new()
            {
                Id = id,
                Group = group,
                Name = name,
                TechnicalName = technical,
                Kind = kind,
                IsReadOnly = readOnly,
                OriginalValue = value,
                Value = value
            };
    }
}

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Faceless
{
    /// <summary>Чтение и сохранение метаданных поддерживаемых форматов.</summary>
    public static class MetadataEditor
    {
        public static bool IsSupported(string filePath) => MetadataCleaner.IsSupported(filePath);

        public static List<MetadataField> Read(string filePath)
        {
            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                return ext switch
                {
                    ".docx" or ".xlsx" or ".pptx" => OfficeMetadata.Read(filePath),
                    ".pdf" => PdfMetadata.Read(filePath),
                    ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" => ImageMetadata.Read(filePath),
                    _ => throw new NotSupportedException($"Формат {ext} не поддерживается.")
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Не удалось открыть файл. Возможно, он занят другой программой. " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Сохраняет изменённые поля. Если правок нет, файл копируется как есть.
        /// Возвращает путь результата.
        /// </summary>
        public static string Save(string filePath, IReadOnlyList<MetadataField> fields, string? outputFolder, bool addSuffix)
        {
            var output = BuildOutputPath(filePath, outputFolder, addSuffix);
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            bool inPlace = string.Equals(
                Path.GetFullPath(output),
                Path.GetFullPath(filePath),
                StringComparison.OrdinalIgnoreCase);

            if (!fields.Any(f => f.IsChanged))
            {
                if (!inPlace)
                {
                    FileIo.CopyShared(filePath, output);
                    ApplyExplorerDates(filePath, output, fields);
                }

                return output;
            }

            var target = inPlace
                ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(filePath))
                : output;

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                switch (ext)
                {
                    case ".docx":
                    case ".xlsx":
                    case ".pptx":
                        OfficeMetadata.Write(filePath, target, fields);
                        break;
                    case ".pdf":
                        PdfMetadata.Write(filePath, target, fields);
                        break;
                    case ".jpg":
                    case ".jpeg":
                    case ".png":
                    case ".tif":
                    case ".tiff":
                        ImageMetadata.Write(filePath, target, fields);
                        break;
                    default:
                        throw new NotSupportedException($"Формат {ext} не поддерживается.");
                }

                if (inPlace)
                    FileIo.CopyShared(target, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Не удалось сохранить файл. " + ex.Message, ex);
            }
            finally
            {
                if (inPlace && File.Exists(target))
                    File.Delete(target);
            }

            ApplyExplorerDates(filePath, output, fields);
            return output;
        }

        /// <summary>
        /// «Дата изменения» во вкладке Windows «Подробно» — это время файла, а не поле внутри него.
        /// После записи возвращаем время исходного файла. Если дату правили в редакторе, ставим её.
        /// </summary>
        private static void ApplyExplorerDates(string source, string output, IReadOnlyList<MetadataField> fields)
        {
            var created = File.GetCreationTimeUtc(source);
            var modified = File.GetLastWriteTimeUtc(source);

            if (EditedStamp(fields, "Создан", "Дата создания", "Дата съёмки") is { } createdStamp)
                created = createdStamp;
            if (EditedStamp(fields, "Изменён", "Дата изменения", "Дата и время") is { } modifiedStamp)
                modified = modifiedStamp;

            File.SetCreationTimeUtc(output, created);
            File.SetLastWriteTimeUtc(output, modified);
            SHChangeNotify(ShcneUpdateItem, ShcnfPath, output, IntPtr.Zero);
        }

        private static DateTime? EditedStamp(IReadOnlyList<MetadataField> fields, params string[] names)
        {
            foreach (var name in names)
            {
                foreach (var field in fields)
                {
                    if (field.IsReadOnly || !field.IsChanged || field.Name != name)
                        continue;
                    if (TryParseUserDate(field.Value, out var utc))
                        return utc;
                }
            }

            return null;
        }

        private static bool TryParseUserDate(string? text, out DateTime utc)
        {
            utc = default;
            var value = MetadataField.Normalize(text).Trim();
            if (value.Length == 0)
                return false;

            string[] formats =
            [
                "yyyy:MM:dd HH:mm:ss",
                "yyyy:MM:dd HH:mm",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-ddTHH:mm:ssK",
                "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
                "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
                "yyyy-MM-dd",
                "yyyyMMdd",
                "yyyyMMddHHmmss",
                "dd.MM.yyyy HH:mm:ss",
                "dd.MM.yyyy HH:mm",
                "dd.MM.yyyy"
            ];

            if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            {
                utc = ToUtc(exact);
                return true;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var iso))
            {
                utc = ToUtc(iso);
                return true;
            }

            if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var local))
            {
                utc = ToUtc(local);
                return true;
            }

            return false;
        }

        private static DateTime ToUtc(DateTime value) =>
            value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
            };

        private const uint ShcneUpdateItem = 0x00002000;
        private const uint ShcnfPath = 0x0005;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);

        private static string BuildOutputPath(string inputPath, string? outputFolder, bool addSuffix)
        {
            var dir = outputFolder ?? Path.GetDirectoryName(inputPath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(inputPath);
            var ext = Path.GetExtension(inputPath);
            var fileName = addSuffix ? $"{name}_edited{ext}" : $"{name}{ext}";
            return Path.Combine(dir, fileName);
        }
    }

    static class FileIo
    {
        public static byte[] ReadAllBytesShared(string path)
        {
            using var input = OpenRead(path);
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            return ms.ToArray();
        }

        public static void CopyShared(string source, string destination)
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var input = OpenRead(source);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
            input.CopyTo(output);
        }

        public static void WriteAllBytes(string path, byte[] data)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, data);
        }

        private static FileStream OpenRead(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}

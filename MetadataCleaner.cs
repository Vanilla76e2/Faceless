using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Faceless
{
    /// <summary>
    /// Удаляет информацию об авторах из метаданных файлов.
    /// Поддерживаемые форматы: DOCX, XLSX, PPTX, PDF, JPEG, PNG, TIFF.
    /// </summary>
    public static class MetadataCleaner
    {
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".docx", ".xlsx", ".pptx", ".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff"
        };

        public static bool IsSupported(string filePath)
            => SupportedExtensions.Contains(Path.GetExtension(filePath));

        /// <summary>
        /// Очищает метаданные авторов.
        /// <para><paramref name="outputFolder"/> — папка для сохранения; null = рядом с оригиналом.</para>
        /// <para><paramref name="addSuffix"/> — добавлять суффикс _faceless к имени файла.</para>
        /// </summary>
        public static string Clean(string filePath, string? outputFolder = null, bool addSuffix = true)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var outputPath = BuildOutputPath(filePath, outputFolder, addSuffix);

            // Если вывод совпадает с вводом — работаем через временный файл
            bool inPlace = string.Equals(
                Path.GetFullPath(outputPath),
                Path.GetFullPath(filePath),
                StringComparison.OrdinalIgnoreCase);

            string workOutput = inPlace ? Path.GetTempFileName() : outputPath;

            // Убеждаемся что папка назначения существует
            var outDir = Path.GetDirectoryName(workOutput);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            switch (ext)
            {
                case ".docx":
                case ".xlsx":
                case ".pptx":
                    CleanOfficeXml(filePath, workOutput);
                    break;
                case ".pdf":
                    CleanPdf(filePath, workOutput);
                    break;
                case ".jpg":
                case ".jpeg":
                case ".png":
                case ".tif":
                case ".tiff":
                    CleanImageExif(filePath, workOutput);
                    break;
                default:
                    throw new NotSupportedException($"Формат {ext} не поддерживается.");
            }

            if (inPlace)
            {
                File.Copy(workOutput, outputPath, overwrite: true);
                File.Delete(workOutput);
            }

            return outputPath;
        }

        // ─── Office Open XML (DOCX / XLSX / PPTX) ───────────────────────────

        private static void CleanOfficeXml(string inputPath, string outputPath)
        {
            // Копируем файл во временную папку, правим ZIP, сохраняем
            var tempPath = Path.GetTempFileName();
            try
            {
                File.Copy(inputPath, tempPath, overwrite: true);

                using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Update))
                {
                    // docProps/core.xml содержит автора, последнего редактора и т.д.
                    var coreEntry = archive.GetEntry("docProps/core.xml");
                    if (coreEntry != null)
                        SanitizeCoreXml(coreEntry);

                    // docProps/app.xml содержит Company и имя приложения
                    var appEntry = archive.GetEntry("docProps/app.xml");
                    if (appEntry != null)
                        SanitizeAppXml(appEntry);
                }

                File.Copy(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static void SanitizeCoreXml(ZipArchiveEntry entry)
        {
            XDocument doc;
            using (var stream = entry.Open())
                doc = XDocument.Load(stream);

            XNamespace dc   = "http://purl.org/dc/elements/1.1/";
            XNamespace cp   = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
            XNamespace dcterms = "http://purl.org/dc/terms/";

            // Поля авторства, которые нужно очистить
            string[] authorFields = ["creator", "lastModifiedBy", "description"];

            foreach (var fieldName in authorFields)
            {
                // dc:creator, dc:description
                doc.Descendants(dc + fieldName).ToList()
                   .ForEach(e => e.Value = string.Empty);

                // cp:lastModifiedBy и другие в пространстве cp
                doc.Descendants(cp + fieldName).ToList()
                   .ForEach(e => e.Value = string.Empty);
            }

            // Явно целимся в cp:lastModifiedBy
            doc.Descendants(cp + "lastModifiedBy").ToList()
               .ForEach(e => e.Value = string.Empty);

            using var writeStream = entry.Open();
            writeStream.SetLength(0);
            doc.Save(writeStream);
        }

        private static void SanitizeAppXml(ZipArchiveEntry entry)
        {
            XDocument doc;
            using (var stream = entry.Open())
                doc = XDocument.Load(stream);

            XNamespace vt = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
            XNamespace ns = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

            // Company, Manager
            string[] appFields = ["Company", "Manager"];
            foreach (var fieldName in appFields)
                doc.Descendants(ns + fieldName).ToList()
                   .ForEach(e => e.Value = string.Empty);

            using var writeStream = entry.Open();
            writeStream.SetLength(0);
            doc.Save(writeStream);
        }

        // ─── PDF ─────────────────────────────────────────────────────────────

        private static void CleanPdf(string inputPath, string outputPath)
        {
            using var document = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);
            var info = document.Info;

            info.Author  = string.Empty;
            info.Creator = string.Empty;
            info.Subject = string.Empty;
            // Title и Keywords оставляем — они не являются авторством

            // Удаляем XMP-метаданные (могут содержать авторов отдельно)
            RemovePdfXmpMetadata(document);

            document.Save(outputPath);
        }

        private static void RemovePdfXmpMetadata(PdfDocument document)
        {
            // XMP хранится в потоке /Metadata в корневом словаре
            var catalog = document.Internals.Catalog;
            if (catalog.Elements.ContainsKey("/Metadata"))
            {
                var xmpRef = catalog.Elements["/Metadata"];
                if (xmpRef != null)
                {
                    catalog.Elements.Remove("/Metadata");
                }
            }
        }

        // ─── Изображения (JPEG / PNG / TIFF) — стрип EXIF ───────────────────

        private static void CleanImageExif(string inputPath, string outputPath)
        {
            var ext = Path.GetExtension(inputPath).ToLowerInvariant();

            if (ext is ".jpg" or ".jpeg")
                StripJpegExif(inputPath, outputPath);
            else if (ext is ".png")
                StripPngMetadata(inputPath, outputPath);
            else if (ext is ".tif" or ".tiff")
                StripTiffMetadata(inputPath, outputPath);
        }

        /// <summary>
        /// Удаляет APP1 (EXIF) и APP13 (IPTC/Photoshop) сегменты из JPEG.
        /// Остальные данные изображения не затрагиваются.
        /// </summary>
        private static void StripJpegExif(string inputPath, string outputPath)
        {
            using var input  = File.OpenRead(inputPath);
            using var output = File.Create(outputPath);
            using var reader = new BinaryReader(input);
            using var writer = new BinaryWriter(output);

            // Проверяем SOI маркер
            if (reader.ReadByte() != 0xFF || reader.ReadByte() != 0xD8)
                throw new InvalidDataException("Файл не является корректным JPEG.");

            writer.Write((byte)0xFF);
            writer.Write((byte)0xD8);

            while (input.Position < input.Length)
            {
                byte b = reader.ReadByte();
                if (b != 0xFF) break;

                byte marker = reader.ReadByte();

                // EOI или сжатые данные — копируем остаток как есть
                if (marker == 0xD9 || marker == 0xDA)
                {
                    writer.Write((byte)0xFF);
                    writer.Write(marker);
                    input.CopyTo(output);
                    break;
                }

                // Маркеры без длины (RST0–RST7, SOI)
                if (marker >= 0xD0 && marker <= 0xD7 || marker == 0x01)
                {
                    writer.Write((byte)0xFF);
                    writer.Write(marker);
                    continue;
                }

                // Сегмент с длиной
                int lengthHigh = reader.ReadByte();
                int lengthLow  = reader.ReadByte();
                int segLength  = (lengthHigh << 8) | lengthLow;
                int dataLength = segLength - 2;

                byte[] segData = reader.ReadBytes(dataLength);

                // APP1 (EXIF/XMP) и APP13 (IPTC) — пропускаем
                bool isApp1  = marker == 0xE1; // EXIF или XMP
                bool isApp13 = marker == 0xED; // IPTC / Photoshop IRB

                if (!isApp1 && !isApp13)
                {
                    writer.Write((byte)0xFF);
                    writer.Write(marker);
                    writer.Write((byte)lengthHigh);
                    writer.Write((byte)lengthLow);
                    writer.Write(segData);
                }
            }
        }

        /// <summary>
        /// Удаляет tEXt/zTXt/iTXt чанки из PNG, содержащие метаданные.
        /// </summary>
        private static void StripPngMetadata(string inputPath, string outputPath)
        {
            using var input  = File.OpenRead(inputPath);
            using var output = File.Create(outputPath);
            using var reader = new BinaryReader(input);
            using var writer = new BinaryWriter(output);

            // PNG signature (8 байт)
            byte[] sig = reader.ReadBytes(8);
            writer.Write(sig);

            while (input.Position < input.Length - 4)
            {
                // Длина данных (big-endian)
                byte[] lenBytes = reader.ReadBytes(4);
                int dataLen = (lenBytes[0] << 24) | (lenBytes[1] << 16) |
                              (lenBytes[2] << 8)  | lenBytes[3];

                // Тип чанка (4 байта ASCII)
                byte[] typeBytes = reader.ReadBytes(4);
                string chunkType = System.Text.Encoding.ASCII.GetString(typeBytes);

                byte[] data = reader.ReadBytes(dataLen);
                byte[] crc  = reader.ReadBytes(4);

                // Пропускаем текстовые метаданные
                if (chunkType is "tEXt" or "zTXt" or "iTXt")
                    continue;

                writer.Write(lenBytes);
                writer.Write(typeBytes);
                writer.Write(data);
                writer.Write(crc);
            }
        }

        /// <summary>
        /// Для TIFF используем простой подход — копируем файл
        /// и обнуляем поля Artist (0x013B) и Copyright (0x8298) в IFD.
        /// </summary>
        private static void StripTiffMetadata(string inputPath, string outputPath)
        {
            File.Copy(inputPath, outputPath, overwrite: true);

            using var fs = File.Open(outputPath, FileMode.Open, FileAccess.ReadWrite);
            using var reader = new BinaryReader(fs);
            using var writer = new BinaryWriter(fs);

            // Читаем byte order
            byte[] bom = reader.ReadBytes(2);
            bool littleEndian = bom[0] == 0x49; // 'II'

            ushort ReadUInt16()
            {
                var b = reader.ReadBytes(2);
                return littleEndian
                    ? (ushort)(b[0] | (b[1] << 8))
                    : (ushort)((b[0] << 8) | b[1]);
            }

            uint ReadUInt32()
            {
                var b = reader.ReadBytes(4);
                return littleEndian
                    ? (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24))
                    : (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
            }

            void WriteZeroAt(long pos, int count)
            {
                long saved = fs.Position;
                fs.Seek(pos, SeekOrigin.Begin);
                writer.Write(new byte[count]);
                fs.Seek(saved, SeekOrigin.Begin);
            }

            ushort magic = ReadUInt16();
            if (magic != 42) return; // Не TIFF

            uint ifdOffset = ReadUInt32();
            fs.Seek(ifdOffset, SeekOrigin.Begin);

            ushort entryCount = ReadUInt16();

            for (int i = 0; i < entryCount; i++)
            {
                long entryPos = fs.Position;
                ushort tag  = ReadUInt16();
                ushort type = ReadUInt16();
                uint   count = ReadUInt32();
                byte[] valueOffset = reader.ReadBytes(4);

                // Artist = 0x013B (315), Copyright = 0x8298 (33432)
                if (tag == 0x013B || tag == 0x8298)
                {
                    // Тип 2 = ASCII, значение либо inline (≤4 байта) либо по смещению
                    bool isInline = count <= 4;
                    if (isInline)
                    {
                        // Обнуляем value/offset прямо в записи IFD
                        WriteZeroAt(entryPos + 8, 4);
                    }
                    else
                    {
                        uint offset = littleEndian
                            ? (uint)(valueOffset[0] | (valueOffset[1] << 8) |
                                     (valueOffset[2] << 16) | (valueOffset[3] << 24))
                            : (uint)((valueOffset[0] << 24) | (valueOffset[1] << 16) |
                                     (valueOffset[2] << 8) | valueOffset[3]);
                        WriteZeroAt(offset, (int)count);
                    }
                }
            }
        }

        // ─── Утилиты ─────────────────────────────────────────────────────────

        private static string BuildOutputPath(string inputPath, string? outputFolder, bool addSuffix)
        {
            var dir      = outputFolder ?? Path.GetDirectoryName(inputPath) ?? ".";
            var name     = Path.GetFileNameWithoutExtension(inputPath);
            var ext      = Path.GetExtension(inputPath);
            var fileName = addSuffix ? $"{name}_faceless{ext}" : $"{name}{ext}";
            return Path.Combine(dir, fileName);
        }
    }
}

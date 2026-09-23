using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Faceless
{
    /// <summary>JPEG, PNG и TIFF: комментарии, EXIF, IPTC, XMP и текстовые чанки PNG.</summary>
    static class ImageMetadata
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");
        private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
        private static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
        private static readonly byte[] PhotoshopHeader = "Photoshop 3.0\0"u8.ToArray();
        private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

        private static readonly (string Keyword, string Name)[] PngPlaceholders =
        [
            ("Title", "Название"),
            ("Author", "Автор"),
            ("Description", "Описание"),
            ("Copyright", "Авторские права"),
            ("Software", "Программа"),
            ("Comment", "Комментарий"),
            ("Source", "Источник")
        ];

        private static readonly Dictionary<string, string> PngNames = new(StringComparer.Ordinal)
        {
            ["Title"] = "Название",
            ["Author"] = "Автор",
            ["Description"] = "Описание",
            ["Copyright"] = "Авторские права",
            ["Software"] = "Программа",
            ["Comment"] = "Комментарий",
            ["Source"] = "Источник",
            ["Disclaimer"] = "Отказ от ответственности",
            ["Warning"] = "Предупреждение",
            ["Creation Time"] = "Дата создания"
        };

        private static readonly Dictionary<(byte Record, byte Dataset), string> IptcNames = new()
        {
            [(2, 5)] = "Название",
            [(2, 7)] = "Статус",
            [(2, 10)] = "Срочность",
            [(2, 15)] = "Категория",
            [(2, 20)] = "Доп. категория",
            [(2, 25)] = "Ключевые слова",
            [(2, 40)] = "Инструкции",
            [(2, 55)] = "Дата создания",
            [(2, 60)] = "Время создания",
            [(2, 80)] = "Автор",
            [(2, 85)] = "Должность",
            [(2, 90)] = "Город",
            [(2, 92)] = "Место",
            [(2, 95)] = "Регион",
            [(2, 100)] = "Код страны",
            [(2, 101)] = "Страна",
            [(2, 103)] = "Ссылка на источник",
            [(2, 105)] = "Заголовок",
            [(2, 110)] = "Поставщик",
            [(2, 115)] = "Источник",
            [(2, 116)] = "Авторские права",
            [(2, 120)] = "Описание",
            [(2, 122)] = "Автор подписи"
        };

        public static List<MetadataField> Read(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var data = FileIo.ReadAllBytesShared(path);
            return ext switch
            {
                ".jpg" or ".jpeg" => ReadJpeg(data),
                ".png" => ReadPng(data),
                ".tif" or ".tiff" => TiffMetadata.Read(data, jpegExif: false),
                _ => throw new NotSupportedException($"Формат {ext} не поддерживается.")
            };
        }

        public static void Write(string source, string target, IReadOnlyList<MetadataField> fields)
        {
            var ext = Path.GetExtension(source).ToLowerInvariant();
            var data = FileIo.ReadAllBytesShared(source);
            byte[] output = ext switch
            {
                ".jpg" or ".jpeg" => WriteJpeg(data, fields),
                ".png" => WritePng(data, fields),
                ".tif" or ".tiff" => TiffMetadata.Apply(data, fields, jpegExif: false),
                _ => throw new NotSupportedException($"Формат {ext} не поддерживается.")
            };
            FileIo.WriteAllBytes(target, output);
        }

        // ─── JPEG ────────────────────────────────────────────────────────────

        private static List<MetadataField> ReadJpeg(byte[] data)
        {
            var parsed = ParseJpeg(data);
            var fields = new List<MetadataField>();
            int comIndex = 0;
            bool hasExif = false;

            foreach (var seg in parsed.Segments)
            {
                if (seg.Marker == 0xFE)
                {
                    var text = DecodeFlexible(seg.Payload);
                    fields.Add(Field($"jpeg:com#{comIndex}", "Комментарий", comIndex == 0 ? "Комментарий" : $"Комментарий ({comIndex + 1})",
                        "JPEG COM", FieldKind.JpegCom, text));
                    comIndex++;
                    continue;
                }

                if (seg.Marker == 0xE1 && StartsWith(seg.Payload, ExifHeader))
                {
                    if (!hasExif)
                    {
                        var tiff = seg.Payload[ExifHeader.Length..];
                        fields.AddRange(TiffMetadata.Read(tiff, jpegExif: true));
                        hasExif = true;
                    }

                    continue;
                }

                if (seg.Marker == 0xE1 && StartsWith(seg.Payload, XmpHeader))
                {
                    var xml = Encoding.UTF8.GetString(seg.Payload, XmpHeader.Length, seg.Payload.Length - XmpHeader.Length);
                    fields.AddRange(XmpMetadata.Read(xml, "jpeg"));
                    continue;
                }

                if (seg.Marker == 0xED && StartsWith(seg.Payload, PhotoshopHeader))
                    fields.AddRange(ReadIptc(seg.Payload));
            }

            if (comIndex == 0)
            {
                fields.Insert(0, Field("jpeg:com#0", "Комментарий", "Комментарий", "JPEG COM", FieldKind.JpegCom, ""));
            }

            if (!hasExif)
                fields.AddRange(TiffMetadata.Read(EmptyTiff(), jpegExif: true));

            return fields;
        }

        private static byte[] WriteJpeg(byte[] data, IReadOnlyList<MetadataField> fields)
        {
            var parsed = ParseJpeg(data);
            bool exifDone = false;

            for (int i = 0; i < parsed.Segments.Count; i++)
            {
                var seg = parsed.Segments[i];
                if (!exifDone && seg.Marker == 0xE1 && StartsWith(seg.Payload, ExifHeader))
                {
                    if (fields.Any(f => f.IsChanged && (f.Id.StartsWith("exif:", StringComparison.Ordinal) || f.Id.StartsWith("xmp:tiff#", StringComparison.Ordinal))))
                    {
                        var tiff = TiffMetadata.Apply(seg.Payload[ExifHeader.Length..], fields, jpegExif: true);
                        seg.Payload = Concat(ExifHeader, tiff);
                        EnsureSegmentSize(seg.Payload, "EXIF");
                    }

                    exifDone = true;
                    continue;
                }

                if (seg.Marker == 0xE1 && StartsWith(seg.Payload, XmpHeader))
                {
                    var xml = Encoding.UTF8.GetString(seg.Payload, XmpHeader.Length, seg.Payload.Length - XmpHeader.Length);
                    var updated = XmpMetadata.Apply(xml, fields, "jpeg");
                    if (updated != null)
                    {
                        seg.Payload = Concat(XmpHeader, Encoding.UTF8.GetBytes(updated));
                        EnsureSegmentSize(seg.Payload, "XMP");
                    }
                }
                else if (seg.Marker == 0xED && StartsWith(seg.Payload, PhotoshopHeader))
                {
                    if (fields.Any(f => f.IsChanged && f.Kind is FieldKind.Iptc or "iptc-utf8" or "iptc-latin1"))
                        seg.Payload = WriteIptc(seg.Payload, fields);
                }
            }

            if (!exifDone && fields.Any(f => f.IsChanged && f.Id.StartsWith("exif:", StringComparison.Ordinal)))
            {
                var tiff = TiffMetadata.Apply(EmptyTiff(), fields, jpegExif: true);
                var payload = Concat(ExifHeader, tiff);
                EnsureSegmentSize(payload, "EXIF");
                int insertAt = LastApp0(parsed.Segments) + 1;
                parsed.Segments.Insert(insertAt, new JpegSeg(0xE1, payload));
            }

            ApplyComments(parsed.Segments, fields);
            return BuildJpeg(parsed);
        }

        private static void ApplyComments(List<JpegSeg> segments, IReadOnlyList<MetadataField> fields)
        {
            if (!fields.Any(f => f.IsChanged && f.Id.StartsWith("jpeg:com#", StringComparison.Ordinal)))
                return;

            var indexes = new List<int>();
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Marker == 0xFE)
                    indexes.Add(i);
            }

            var removals = new List<int>();
            foreach (var field in fields.Where(f => f.Id.StartsWith("jpeg:com#", StringComparison.Ordinal) && f.IsChanged))
            {
                if (!int.TryParse(field.Id["jpeg:com#".Length..], out var index))
                    continue;
                var text = MetadataField.Normalize(field.Value);
                if (index >= 0 && index < indexes.Count)
                {
                    if (text.Length == 0)
                        removals.Add(indexes[index]);
                    else
                    {
                        segments[indexes[index]].Payload = Encoding.UTF8.GetBytes(text);
                        EnsureSegmentSize(segments[indexes[index]].Payload, "комментарий");
                    }
                }
                else if (index == indexes.Count && text.Length > 0)
                {
                    var payload = Encoding.UTF8.GetBytes(text);
                    EnsureSegmentSize(payload, "комментарий");
                    segments.Add(new JpegSeg(0xFE, payload));
                }
            }

            foreach (var index in removals.OrderByDescending(i => i))
                segments.RemoveAt(index);
        }

        private static int LastApp0(List<JpegSeg> segments)
        {
            int last = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Marker == 0xE0)
                    last = i;
            }

            return last;
        }

        private static JpegFile ParseJpeg(byte[] data)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
                throw new InvalidDataException("Файл не является корректным JPEG.");

            var segments = new List<JpegSeg>();
            int i = 2;
            while (i + 1 < data.Length)
            {
                if (data[i] != 0xFF)
                    throw new InvalidDataException("Повреждённая структура JPEG.");

                int markerPos = i;
                i++;
                while (i < data.Length && data[i] == 0xFF)
                    i++;
                if (i >= data.Length)
                    break;

                byte marker = data[i++];
                if (marker == 0x00)
                    continue;

                if (marker is 0xD9 or 0xDA)
                    return new JpegFile(segments, data[markerPos..]);

                if (marker is >= 0xD0 and <= 0xD7 or 0x01)
                {
                    segments.Add(new JpegSeg(marker, []));
                    continue;
                }

                if (i + 1 >= data.Length)
                    throw new InvalidDataException("Повреждённая структура JPEG.");

                int length = (data[i] << 8) | data[i + 1];
                if (length < 2 || i + length > data.Length)
                    throw new InvalidDataException("Повреждённая структура JPEG.");

                var payload = new byte[length - 2];
                Buffer.BlockCopy(data, i + 2, payload, 0, payload.Length);
                segments.Add(new JpegSeg(marker, payload));
                i += length;
            }

            return new JpegFile(segments, []);
        }

        private static byte[] BuildJpeg(JpegFile file)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0xFF);
            ms.WriteByte(0xD8);
            foreach (var seg in file.Segments)
            {
                ms.WriteByte(0xFF);
                ms.WriteByte(seg.Marker);
                bool hasLength = seg.Marker is not (>= 0xD0 and <= 0xD7 or 0x01);
                if (!hasLength)
                    continue;

                int length = seg.Payload.Length + 2;
                ms.WriteByte((byte)(length >> 8));
                ms.WriteByte((byte)length);
                ms.Write(seg.Payload);
            }

            ms.Write(file.Tail);
            return ms.ToArray();
        }

        // ─── IPTC ────────────────────────────────────────────────────────────

        private static List<MetadataField> ReadIptc(byte[] app13)
        {
            if (!TryParsePhotoshop(app13, out var ps))
                return [];

            var iptc = ps.Blocks.FirstOrDefault(b => b.Id == 0x0404);
            if (iptc == null || !TryParseDatasets(iptc.Data, out var datasets))
                return [];

            bool utf8 = datasets.Any(d => d.Record == 1 && d.Dataset == 90 && d.Data.Contains((byte)0x1B));
            var fields = new List<MetadataField>();
            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < datasets.Count; i++)
            {
                var ds = datasets[i];
                if (ds.Record == 1 || ds.Dataset == 0)
                    continue;
                if (!LooksLikeText(ds.Data))
                    continue;

                string baseName = IptcNames.TryGetValue((ds.Record, ds.Dataset), out var name)
                    ? name
                    : $"IPTC {ds.Record}:{ds.Dataset}";
                used.TryGetValue(baseName, out var n);
                used[baseName] = n + 1;
                var label = n == 0 ? baseName : $"{baseName} ({n + 1})";
                var text = utf8 && IsUtf8(ds.Data) ? Encoding.UTF8.GetString(ds.Data) : Latin1.GetString(ds.Data);
                fields.Add(Field(
                    $"iptc#{i}",
                    "IPTC",
                    label,
                    $"IPTC {ds.Record}:{ds.Dataset}",
                    utf8 ? "iptc-utf8" : "iptc-latin1",
                    text.TrimEnd('\0')));
            }

            return fields;
        }

        private static byte[] WriteIptc(byte[] app13, IReadOnlyList<MetadataField> fields)
        {
            if (!TryParsePhotoshop(app13, out var ps))
                return app13;

            int blockIndex = ps.Blocks.FindIndex(b => b.Id == 0x0404);
            if (blockIndex < 0 || !TryParseDatasets(ps.Blocks[blockIndex].Data, out var datasets))
                return app13;

            foreach (var field in fields.Where(f => f.IsChanged && f.Id.StartsWith("iptc#", StringComparison.Ordinal)))
            {
                if (!int.TryParse(field.Id["iptc#".Length..], out var index) || index < 0 || index >= datasets.Count)
                    throw new InvalidDataException($"Не найдено поле IPTC «{field.Name}».");

                datasets[index].Data = EncodeIptc(MetadataField.Normalize(field.Value), field.Kind, field.Name);
            }

            ps.Blocks[blockIndex].Data = SerializeDatasets(datasets);
            ps.Blocks[blockIndex].Raw = null;
            return SerializePhotoshop(ps);
        }

        private static byte[] EncodeIptc(string text, string kind, string fieldName)
        {
            if (kind == "iptc-utf8")
                return Encoding.UTF8.GetBytes(text);

            if (text.Any(c => c > 255))
                throw new FormatException($"Поле «{fieldName}» не поддерживает этот символ. Кодировка IPTC в файле — Latin-1.");

            return Latin1.GetBytes(text);
        }

        private static bool TryParsePhotoshop(byte[] data, out PhotoshopBlock ps)
        {
            ps = new PhotoshopBlock();
            if (!StartsWith(data, PhotoshopHeader))
                return false;

            int i = PhotoshopHeader.Length;
            while (i + 12 <= data.Length && data[i] == (byte)'8' && data[i + 1] == (byte)'B' && data[i + 2] == (byte)'I' && data[i + 3] == (byte)'M')
            {
                int start = i;
                i += 4;
                ushort id = (ushort)((data[i] << 8) | data[i + 1]);
                i += 2;
                int nameLen = data[i];
                int nameField = 1 + nameLen;
                if ((nameField & 1) != 0)
                    nameField++;
                i += nameField;
                if (i + 4 > data.Length)
                    return false;

                int size = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
                i += 4;
                if (size < 0 || i + size > data.Length)
                    return false;

                var blockData = new byte[size];
                Buffer.BlockCopy(data, i, blockData, 0, size);
                i += size;
                if ((size & 1) != 0 && i < data.Length)
                    i++;

                var raw = new byte[i - start];
                Buffer.BlockCopy(data, start, raw, 0, raw.Length);
                ps.Blocks.Add(new IrbBlock { Id = id, Data = blockData, Raw = raw });
            }

            ps.Suffix = data[i..];
            return true;
        }

        private static byte[] SerializePhotoshop(PhotoshopBlock ps)
        {
            using var ms = new MemoryStream();
            ms.Write(PhotoshopHeader);
            foreach (var block in ps.Blocks)
            {
                if (block.Raw != null)
                {
                    ms.Write(block.Raw);
                    continue;
                }

                ms.Write("8BIM"u8);
                ms.WriteByte((byte)(block.Id >> 8));
                ms.WriteByte((byte)block.Id);
                ms.WriteByte(0);
                ms.WriteByte(0);
                ms.WriteByte((byte)(block.Data.Length >> 24));
                ms.WriteByte((byte)(block.Data.Length >> 16));
                ms.WriteByte((byte)(block.Data.Length >> 8));
                ms.WriteByte((byte)block.Data.Length);
                ms.Write(block.Data);
                if ((block.Data.Length & 1) != 0)
                    ms.WriteByte(0);
            }

            ms.Write(ps.Suffix);
            return ms.ToArray();
        }

        private static bool TryParseDatasets(byte[] data, out List<IptcDataset> datasets)
        {
            datasets = [];
            int i = 0;
            while (i + 5 <= data.Length)
            {
                if (data[i] != 0x1C)
                {
                    if (data[i] == 0)
                    {
                        i++;
                        continue;
                    }

                    return datasets.Count > 0 && i >= data.Length - 1;
                }

                byte record = data[i + 1];
                byte set = data[i + 2];
                int len = (data[i + 3] << 8) | data[i + 4];
                i += 5;
                if ((len & 0x8000) != 0)
                    return false;
                if (i + len > data.Length)
                    return false;

                var bytes = new byte[len];
                Buffer.BlockCopy(data, i, bytes, 0, len);
                datasets.Add(new IptcDataset { Record = record, Dataset = set, Data = bytes });
                i += len;
            }

            return true;
        }

        private static byte[] SerializeDatasets(List<IptcDataset> datasets)
        {
            using var ms = new MemoryStream();
            foreach (var ds in datasets)
            {
                ms.WriteByte(0x1C);
                ms.WriteByte(ds.Record);
                ms.WriteByte(ds.Dataset);
                ms.WriteByte((byte)(ds.Data.Length >> 8));
                ms.WriteByte((byte)ds.Data.Length);
                ms.Write(ds.Data);
            }

            return ms.ToArray();
        }

        // ─── PNG ─────────────────────────────────────────────────────────────

        private static List<MetadataField> ReadPng(byte[] data)
        {
            var chunks = ParsePng(data);
            var fields = new List<MetadataField>();
            var keywords = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (chunk.Type is "tEXt" or "zTXt" or "iTXt" && TryReadPngText(chunk, out var keyword, out var text))
                {
                    keywords.Add(keyword);
                    var name = PngNames.TryGetValue(keyword, out var friendly) ? friendly : keyword;
                    fields.Add(Field($"png:{chunk.Type}#{i}", "PNG", name, keyword, FieldKind.Text, text));
                }
                else if (chunk.Type == "eXIf")
                {
                    fields.AddRange(TiffMetadata.Read(chunk.Data, jpegExif: true));
                }
            }

            foreach (var (keyword, name) in PngPlaceholders)
            {
                if (keywords.Contains(keyword))
                    continue;
                fields.Add(Field($"png:new#{keyword}", "PNG", name, keyword, FieldKind.Text, ""));
            }

            return fields
                .GroupBy(f => f.Group)
                .SelectMany(g => g)
                .ToList();
        }

        private static byte[] WritePng(byte[] data, IReadOnlyList<MetadataField> fields)
        {
            var chunks = ParsePng(data);

            foreach (var field in fields.Where(f => f.IsChanged && f.Id.StartsWith("png:", StringComparison.Ordinal) && !f.Id.StartsWith("png:new#", StringComparison.Ordinal)))
            {
                var hash = field.Id.LastIndexOf('#');
                var type = field.Id[4..hash];
                if (!int.TryParse(field.Id[(hash + 1)..], out var index) || index < 0 || index >= chunks.Count || chunks[index].Type != type)
                    throw new InvalidDataException($"Не найден текстовый блок PNG «{field.Name}».");

                var text = MetadataField.Normalize(field.Value);
                if (!TryReadPngText(chunks[index], out var keyword, out _))
                    throw new InvalidDataException($"Не удалось прочитать блок PNG «{field.Name}».");

                chunks[index] = text.Any(c => c > 255) || type == "iTXt"
                    ? new PngChunk("iTXt", BuildIText(keyword, text, compress: type == "zTXt"))
                    : new PngChunk("tEXt", BuildText(keyword, text));
            }

            if (fields.Any(f => f.IsChanged && (f.Id.StartsWith("exif:", StringComparison.Ordinal) || f.Id.StartsWith("xmp:tiff#", StringComparison.Ordinal))))
            {
                int exif = chunks.FindIndex(c => c.Type == "eXIf");
                var source = exif >= 0 ? chunks[exif].Data : EmptyTiff();
                var updated = TiffMetadata.Apply(source, fields, jpegExif: true);
                var chunk = new PngChunk("eXIf", updated);
                if (exif >= 0)
                    chunks[exif] = chunk;
                else
                    InsertBeforeEnd(chunks, chunk);
            }

            foreach (var field in fields.Where(f => f.IsChanged && f.Id.StartsWith("png:new#", StringComparison.Ordinal)))
            {
                var text = MetadataField.Normalize(field.Value);
                if (text.Length == 0)
                    continue;
                var keyword = field.Id["png:new#".Length..];
                var chunk = text.Any(c => c > 255)
                    ? new PngChunk("iTXt", BuildIText(keyword, text, compress: false))
                    : new PngChunk("tEXt", BuildText(keyword, text));
                InsertBeforeEnd(chunks, chunk);
            }

            return BuildPng(chunks);
        }

        private static bool TryReadPngText(PngChunk chunk, out string keyword, out string text)
        {
            keyword = "";
            text = "";
            try
            {
                if (chunk.Type == "tEXt")
                {
                    int nul = Array.IndexOf(chunk.Data, (byte)0);
                    if (nul <= 0)
                        return false;
                    keyword = Latin1.GetString(chunk.Data, 0, nul);
                    text = Latin1.GetString(chunk.Data, nul + 1, chunk.Data.Length - nul - 1);
                    return true;
                }

                if (chunk.Type == "zTXt")
                {
                    int nul = Array.IndexOf(chunk.Data, (byte)0);
                    if (nul <= 0 || nul + 2 > chunk.Data.Length)
                        return false;
                    keyword = Latin1.GetString(chunk.Data, 0, nul);
                    text = Latin1.GetString(Inflate(chunk.Data[(nul + 2)..]));
                    return true;
                }

                if (chunk.Type == "iTXt")
                {
                    int nul = Array.IndexOf(chunk.Data, (byte)0);
                    if (nul <= 0 || nul + 3 >= chunk.Data.Length)
                        return false;
                    keyword = Latin1.GetString(chunk.Data, 0, nul);
                    bool compressed = chunk.Data[nul + 1] == 1;
                    int p = nul + 3;
                    int lang = Array.IndexOf(chunk.Data, (byte)0, p);
                    if (lang < 0)
                        return false;
                    int trans = Array.IndexOf(chunk.Data, (byte)0, lang + 1);
                    if (trans < 0)
                        return false;
                    var body = chunk.Data[(trans + 1)..];
                    if (compressed)
                        body = Inflate(body);
                    text = Encoding.UTF8.GetString(body);
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }

            return false;
        }

        private static byte[] BuildText(string keyword, string text)
        {
            var key = Latin1.GetBytes(keyword);
            var body = Latin1.GetBytes(text);
            var data = new byte[key.Length + 1 + body.Length];
            Buffer.BlockCopy(key, 0, data, 0, key.Length);
            Buffer.BlockCopy(body, 0, data, key.Length + 1, body.Length);
            return data;
        }

        private static byte[] BuildIText(string keyword, string text, bool compress)
        {
            var key = Latin1.GetBytes(keyword);
            var body = Encoding.UTF8.GetBytes(text);
            if (compress)
                body = Deflate(body);

            var data = new byte[key.Length + 1 + 2 + 2 + body.Length];
            Buffer.BlockCopy(key, 0, data, 0, key.Length);
            data[key.Length + 1] = compress ? (byte)1 : (byte)0;
            Buffer.BlockCopy(body, 0, data, key.Length + 5, body.Length);
            return data;
        }

        private static byte[] Inflate(byte[] zlib)
        {
            using var input = new MemoryStream(zlib);
            using var zs = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zs.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zs = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(data);
            return output.ToArray();
        }

        private static List<PngChunk> ParsePng(byte[] data)
        {
            if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(PngSignature))
                throw new InvalidDataException("Файл не является корректным PNG.");

            var chunks = new List<PngChunk>();
            int i = 8;
            while (i + 12 <= data.Length)
            {
                int length = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
                if (length < 0 || i + 12 + length > data.Length)
                    throw new InvalidDataException("Повреждённая структура PNG.");

                var type = Encoding.ASCII.GetString(data, i + 4, 4);
                var chunkData = new byte[length];
                Buffer.BlockCopy(data, i + 8, chunkData, 0, length);
                chunks.Add(new PngChunk(type, chunkData));
                i += 12 + length;
                if (type == "IEND")
                    break;
            }

            return chunks;
        }

        private static byte[] BuildPng(List<PngChunk> chunks)
        {
            using var ms = new MemoryStream();
            ms.Write(PngSignature);
            foreach (var chunk in chunks)
            {
                int length = chunk.Data.Length;
                ms.WriteByte((byte)(length >> 24));
                ms.WriteByte((byte)(length >> 16));
                ms.WriteByte((byte)(length >> 8));
                ms.WriteByte((byte)length);
                var type = Encoding.ASCII.GetBytes(chunk.Type);
                ms.Write(type);
                ms.Write(chunk.Data);
                var crcSource = new byte[4 + chunk.Data.Length];
                Buffer.BlockCopy(type, 0, crcSource, 0, 4);
                Buffer.BlockCopy(chunk.Data, 0, crcSource, 4, chunk.Data.Length);
                uint crc = Crc32(crcSource);
                ms.WriteByte((byte)(crc >> 24));
                ms.WriteByte((byte)(crc >> 16));
                ms.WriteByte((byte)(crc >> 8));
                ms.WriteByte((byte)crc);
            }

            return ms.ToArray();
        }

        private static void InsertBeforeEnd(List<PngChunk> chunks, PngChunk chunk)
        {
            int iend = chunks.FindIndex(c => c.Type == "IEND");
            if (iend < 0)
                chunks.Add(chunk);
            else
                chunks.Insert(iend, chunk);
        }

        private static readonly uint[] CrcTable = CreateCrcTable();

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var b in data)
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        // ─── Общее ───────────────────────────────────────────────────────────

        private static byte[] EmptyTiff() =>
        [
            0x49, 0x49, 0x2A, 0x00,
            0x08, 0x00, 0x00, 0x00,
            0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];

        private static void EnsureSegmentSize(byte[] payload, string what)
        {
            if (payload.Length > 65533)
                throw new InvalidDataException($"Блок {what} не помещается в JPEG (больше 64 КБ).");
        }

        private static bool StartsWith(byte[] data, byte[] prefix) =>
            data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private static string DecodeFlexible(byte[] data)
        {
            if (data.Length == 0)
                return "";
            if (IsUtf8(data))
                return Encoding.UTF8.GetString(data);
            return Latin1.GetString(data);
        }

        private static bool LooksLikeText(byte[] data)
        {
            if (data.Length > 100_000)
                return false;
            return data.All(b => b is 9 or 10 or 13 or >= 32);
        }

        private static bool IsUtf8(byte[] data)
        {
            int i = 0;
            while (i < data.Length)
            {
                byte b = data[i];
                if (b <= 0x7F)
                {
                    i++;
                    continue;
                }

                int need = b switch
                {
                    >= 0xC2 and <= 0xDF => 1,
                    >= 0xE0 and <= 0xEF => 2,
                    >= 0xF0 and <= 0xF4 => 3,
                    _ => -1
                };
                if (need < 0 || i + need >= data.Length)
                    return false;
                for (int k = 1; k <= need; k++)
                {
                    if ((data[i + k] & 0xC0) != 0x80)
                        return false;
                }

                i += need + 1;
            }

            return true;
        }

        private static MetadataField Field(string id, string group, string name, string technical, string kind, string value) =>
            new()
            {
                Id = id,
                Group = group,
                Name = name,
                TechnicalName = technical,
                Kind = kind,
                OriginalValue = value,
                Value = value
            };

        private sealed class JpegFile(List<JpegSeg> segments, byte[] tail)
        {
            public List<JpegSeg> Segments { get; } = segments;
            public byte[] Tail { get; } = tail;
        }

        private sealed class JpegSeg(byte marker, byte[] payload)
        {
            public byte Marker { get; } = marker;
            public byte[] Payload { get; set; } = payload;
        }

        private sealed class PhotoshopBlock
        {
            public List<IrbBlock> Blocks { get; } = [];
            public byte[] Suffix { get; set; } = [];
        }

        private sealed class IrbBlock
        {
            public ushort Id;
            public byte[] Data = [];
            public byte[]? Raw;
        }

        private sealed class IptcDataset
        {
            public byte Record;
            public byte Dataset;
            public byte[] Data = [];
        }

        private sealed record PngChunk(string Type, byte[] Data);
    }
}

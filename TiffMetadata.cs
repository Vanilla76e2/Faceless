using System.Globalization;
using System.IO;
using System.Text;

namespace Faceless
{
    /// <summary>
    /// Чтение и точечная правка TIFF/EXIF. Значения, которые не влезают на старое место,
    /// дописываются в конец, а при добавлении нового тега сдвигаются только смещения.
    /// </summary>
    static class TiffMetadata
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        private static readonly HashSet<ushort> HiddenTags =
        [
            0x0111, 0x0117, 0x0144, 0x0145, 0x0201, 0x0202, 0x014A, 0x8769, 0x8825, 0xA005
        ];

        private static readonly HashSet<ushort> ReadOnlyTags =
        [
            0x00FE, 0x0100, 0x0101, 0x0102, 0x0103, 0x0106,
            0x0115, 0x0116, 0x011C, 0x0153
        ];

        private static readonly HashSet<ushort> OffsetTags =
        [
            0x0111, 0x0144, 0x0201, 0x014A, 0x8769, 0x8825, 0xA005
        ];

        private static readonly HashSet<ushort> IfdPointerTags =
        [
            0x8769, 0x8825, 0xA005, 0x014A
        ];

        private static readonly (ushort Tag, string Name, string Kind)[] Placeholders =
        [
            (0x013B, "Автор", FieldKind.Ascii),
            (0x010E, "Описание", FieldKind.Ascii),
            (0x8298, "Авторские права", FieldKind.Ascii),
            (0x9C9D, "Автор (Windows)", FieldKind.Utf16),
            (0x9C9B, "Заголовок", FieldKind.Utf16),
            (0x9C9C, "Комментарий (Windows)", FieldKind.Utf16),
            (0x9C9E, "Ключевые слова", FieldKind.Utf16),
            (0x9C9F, "Тема", FieldKind.Utf16),
            (0x0131, "Программа", FieldKind.Ascii),
            (0x0132, "Дата и время", FieldKind.Ascii)
        ];

        private static readonly Dictionary<ushort, string> Names = new()
        {
            [0x0100] = "Ширина",
            [0x0101] = "Высота",
            [0x0102] = "Глубина цвета",
            [0x0103] = "Сжатие",
            [0x0106] = "Фотометрия",
            [0x010E] = "Описание",
            [0x010F] = "Производитель",
            [0x0110] = "Модель",
            [0x0112] = "Ориентация",
            [0x0115] = "Каналов на пиксель",
            [0x0116] = "Строк в полосе",
            [0x011A] = "Разрешение X",
            [0x011B] = "Разрешение Y",
            [0x011C] = "Порядок каналов",
            [0x0128] = "Единица разрешения",
            [0x0131] = "Программа",
            [0x0132] = "Дата и время",
            [0x013B] = "Автор",
            [0x013C] = "Узел",
            [0x013E] = "Точка белого",
            [0x0142] = "TileWidth",
            [0x0143] = "TileLength",
            [0x8298] = "Авторские права",
            [0x829A] = "Выдержка",
            [0x829D] = "Диафрагма",
            [0x8822] = "Режим экспозиции",
            [0x8827] = "ISO",
            [0x8830] = "Чувствительность",
            [0x8832] = "Рекомендуемый индекс экспозиции",
            [0x9000] = "Версия EXIF",
            [0x9003] = "Дата съёмки",
            [0x9004] = "Дата оцифровки",
            [0x9101] = "Порядок компонентов",
            [0x9201] = "Выдержка (APEX)",
            [0x9202] = "Диафрагма (APEX)",
            [0x9203] = "Яркость",
            [0x9204] = "Компенсация экспозиции",
            [0x9205] = "Максимальная диафрагма",
            [0x9207] = "Замер",
            [0x9208] = "Источник света",
            [0x9209] = "Вспышка",
            [0x920A] = "Фокусное расстояние",
            [0x927C] = "Данные производителя",
            [0x9286] = "Комментарий пользователя",
            [0x9290] = "Доли секунды",
            [0x9291] = "Доли секунды съёмки",
            [0x9292] = "Доли секунды оцифровки",
            [0xA000] = "Версия Flashpix",
            [0xA001] = "Цветовое пространство",
            [0xA002] = "Ширина снимка",
            [0xA003] = "Высота снимка",
            [0xA20E] = "Разрешение матрицы X",
            [0xA20F] = "Разрешение матрицы Y",
            [0xA210] = "Единица разрешения матрицы",
            [0xA217] = "Тип сенсора",
            [0xA300] = "Источник файла",
            [0xA301] = "Тип сцены",
            [0xA401] = "Обработка",
            [0xA402] = "Режим экспозиции",
            [0xA403] = "Баланс белого",
            [0xA404] = "Цифровой зум",
            [0xA405] = "Фокусное 35 мм",
            [0xA406] = "Тип сцены",
            [0xA407] = "Усиление",
            [0xA408] = "Контраст",
            [0xA409] = "Насыщенность",
            [0xA40A] = "Резкость",
            [0xA420] = "Уникальный ID",
            [0xA430] = "Владелец камеры",
            [0xA431] = "Серийный номер",
            [0xA432] = "Параметры объектива",
            [0xA433] = "Производитель объектива",
            [0xA434] = "Объектив",
            [0xA435] = "Серийный номер объектива",
            [0x9C9B] = "Заголовок",
            [0x9C9C] = "Комментарий (Windows)",
            [0x9C9D] = "Автор (Windows)",
            [0x9C9E] = "Ключевые слова",
            [0x9C9F] = "Тема"
        };

        private static readonly Dictionary<ushort, string> GpsNames = new()
        {
            [0] = "Версия GPS",
            [1] = "Широта (N/S)",
            [2] = "Широта",
            [3] = "Долгота (E/W)",
            [4] = "Долгота",
            [5] = "Высота (отн. уровня моря)",
            [6] = "Высота",
            [7] = "Время GPS",
            [8] = "Спутники",
            [9] = "Статус GPS",
            [10] = "Режим измерения",
            [11] = "Точность (DOP)",
            [12] = "Единица скорости",
            [13] = "Скорость",
            [14] = "Направление движения (отн.)",
            [15] = "Направление движения",
            [16] = "Направление съёмки (отн.)",
            [17] = "Направление съёмки",
            [18] = "Датум",
            [19] = "Широта назначения (N/S)",
            [20] = "Широта назначения",
            [21] = "Долгота назначения (E/W)",
            [22] = "Долгота назначения",
            [23] = "Пеленг (отн.)",
            [24] = "Пеленг",
            [25] = "Расстояние (ед.)",
            [26] = "Расстояние",
            [27] = "Метод определения",
            [28] = "Область",
            [29] = "Дата GPS",
            [30] = "Дифференциальная поправка"
        };

        public static List<MetadataField> Read(byte[] tiff, bool jpegExif)
        {
            var doc = Parse(tiff, jpegExif, out var error);
            if (error != null)
            {
                return
                [
                    new MetadataField
                    {
                        Id = "exif:error",
                        Group = "EXIF",
                        Name = "EXIF",
                        TechnicalName = "EXIF",
                        Kind = FieldKind.ReadOnly,
                        IsReadOnly = true,
                        OriginalValue = error,
                        Value = error
                    }
                ];
            }

            AddPlaceholders(doc);
            return Sort(doc.Fields);
        }

        public static byte[] Apply(byte[] tiff, IReadOnlyList<MetadataField> fields, bool jpegExif)
        {
            var doc = Parse(tiff, jpegExif, out var error);
            if (error != null)
                throw new InvalidDataException(error);

            PatchXmp(doc, fields);
            PatchExisting(doc, fields);
            InsertMissing(doc, fields);
            return doc.Data;
        }

        private static void AddPlaceholders(TiffDoc doc)
        {
            var present = doc.Fields.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (tag, name, kind) in Placeholders)
            {
                var id = Id("0", tag);
                if (present.Contains(id))
                    continue;

                doc.Fields.Add(MakeField(id, "EXIF", name, Technical(tag, "0"), kind, "", readOnly: false));
            }
        }

        private static List<MetadataField> Sort(List<MetadataField> fields)
        {
            return fields
                .GroupBy(f => f.Group)
                .SelectMany(g => g.OrderBy(f => Priority(f.Id)))
                .ToList();
        }

        private static int Priority(string id)
        {
            if (!TryParseId(id, out _, out var tag))
                return 50;
            return tag switch
            {
                0x013B => 0,
                0x010E => 1,
                0x8298 => 2,
                0x9C9D => 3,
                0x9C9B => 4,
                0x9C9C => 5,
                0x9C9E => 6,
                0x9C9F => 7,
                0x0131 => 8,
                0x0132 => 9,
                0x010F => 10,
                0x0110 => 11,
                _ => 50
            };
        }

        private static void PatchXmp(TiffDoc doc, IReadOnlyList<MetadataField> fields)
        {
            if (!fields.Any(f => f.Id.StartsWith("xmp:tiff#", StringComparison.Ordinal) && f.IsChanged))
                return;

            var entry = doc.Entries.FirstOrDefault(e => e.Tag == 0x02BC);
            if (entry == null)
                return;

            var xml = DecodeXml(entry.Raw);
            var updated = XmpMetadata.Apply(xml, fields, "tiff");
            if (updated == null)
                return;

            var bytes = Encoding.UTF8.GetBytes(updated);
            WriteValue(doc, entry, bytes);
        }

        private static void PatchExisting(TiffDoc doc, IReadOnlyList<MetadataField> fields)
        {
            var byId = doc.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (!field.IsChanged || !byId.TryGetValue(field.Id, out var entry))
                    continue;
                if (entry.Tag == 0x02BC)
                    continue;

                try
                {
                    WriteValue(doc, entry, Encode(field, entry, doc.LittleEndian));
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                {
                    throw new FormatException($"Поле «{field.Name}»: {ex.Message}");
                }
            }
        }

        private static void InsertMissing(TiffDoc doc, IReadOnlyList<MetadataField> fields)
        {
            foreach (var field in fields)
            {
                if (!field.IsChanged)
                    continue;
                if (!TryParseId(field.Id, out var path, out var tag) || path != "0")
                    continue;
                if (string.IsNullOrEmpty(MetadataField.Normalize(field.Value)))
                    continue;

                if (TagExists(doc, tag))
                    continue;

                var kind = PlaceholderKind(tag) ?? field.Kind;
                var type = kind == FieldKind.Utf16 ? (ushort)1 : (ushort)2;
                byte[] value;
                try
                {
                    value = kind == FieldKind.Utf16
                        ? EncodeUtf16(MetadataField.Normalize(field.Value))
                        : EncodeAscii(MetadataField.Normalize(field.Value), field.Name);
                }
                catch (ArgumentException ex)
                {
                    throw new FormatException($"Поле «{field.Name}»: {ex.Message}");
                }

                InsertEntry(doc, tag, type, value);
            }
        }

        private static bool TagExists(TiffDoc doc, ushort tag)
        {
            if (doc.Data.Length < 8)
                return false;
            int ifd = (int)ReadU32(doc, 4);
            if (ifd < 0 || ifd + 2 > doc.Data.Length)
                return false;
            int count = ReadU16(doc, ifd);
            for (int i = 0; i < count; i++)
            {
                int entry = ifd + 2 + i * 12;
                if (entry + 12 > doc.Data.Length)
                    break;
                if (ReadU16(doc, entry) == tag)
                    return true;
            }

            return false;
        }

        private static string? PlaceholderKind(ushort tag)
        {
            foreach (var item in Placeholders)
            {
                if (item.Tag == tag)
                    return item.Kind;
            }

            return null;
        }

        private static TiffDoc Parse(byte[] tiff, bool jpegExif, out string? error)
        {
            error = null;
            var doc = new TiffDoc
            {
                Data = (byte[])tiff.Clone(),
                LittleEndian = true
            };

            if (tiff.Length < 8)
            {
                error = "Блок EXIF повреждён.";
                return doc;
            }

            bool le = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
            bool be = tiff[0] == (byte)'M' && tiff[1] == (byte)'M';
            if (!le && !be)
            {
                error = "Блок EXIF повреждён.";
                return doc;
            }

            doc.LittleEndian = le;
            ushort magic = ReadU16(doc, 2);
            if (magic == 43)
            {
                error = "Формат BigTIFF пока не поддерживается.";
                return doc;
            }

            if (magic != 42)
            {
                error = "Блок EXIF повреждён.";
                return doc;
            }

            int ifd0 = (int)ReadU32(doc, 4);
            Walk(doc, ifd0, "0", 0, jpegExif, new HashSet<int>());
            return doc;
        }

        private static void Walk(TiffDoc doc, int ifdPos, string path, int pageIndex, bool jpegExif, HashSet<int> seen)
        {
            if (pageIndex > 16 || !seen.Add(ifdPos))
                return;
            if (ifdPos < 0 || ifdPos + 2 > doc.Data.Length)
                return;

            int count = ReadU16(doc, ifdPos);
            if (count > 2000)
                return;

            int entries = ifdPos + 2;
            if (entries + count * 12 + 4 > doc.Data.Length)
                return;

            string group = GroupName(path, pageIndex, jpegExif);
            bool gps = path.Contains("/gps", StringComparison.Ordinal);

            for (int i = 0; i < count; i++)
            {
                int entryPos = entries + i * 12;
                ushort tag = ReadU16(doc, entryPos);
                ushort type = ReadU16(doc, entryPos + 2);
                uint cnt = ReadU32(doc, entryPos + 4);
                int typeSize = TypeSize(type);
                if (typeSize == 0 || cnt == 0 || cnt > 5_000_000)
                    continue;

                long byteLenLong = (long)typeSize * cnt;
                if (byteLenLong > int.MaxValue)
                    continue;
                int byteLen = (int)byteLenLong;

                bool inline = byteLen <= 4;
                int valuePos = entryPos + 8;
                if (!inline)
                {
                    uint offset = ReadU32(doc, entryPos + 8);
                    if (offset > doc.Data.Length || byteLen > doc.Data.Length - offset)
                        continue;
                    valuePos = (int)offset;
                }

                if (tag == 0x8769)
                {
                    uint p = ReadOffset(doc, entryPos, type, cnt, byteLen);
                    if (p > 0)
                        Walk(doc, (int)p, path + "/exif", pageIndex, jpegExif, seen);
                    continue;
                }

                if (tag == 0x8825)
                {
                    uint p = ReadOffset(doc, entryPos, type, cnt, byteLen);
                    if (p > 0)
                        Walk(doc, (int)p, path + "/gps", pageIndex, jpegExif, seen);
                    continue;
                }

                if (tag == 0xA005)
                {
                    uint p = ReadOffset(doc, entryPos, type, cnt, byteLen);
                    if (p > 0)
                        Walk(doc, (int)p, path + "/interop", pageIndex, jpegExif, seen);
                    continue;
                }

                if (HiddenTags.Contains(tag))
                    continue;

                var raw = new byte[byteLen];
                Buffer.BlockCopy(doc.Data, valuePos, raw, 0, byteLen);

                if (tag == 0x02BC && pageIndex == 0 && path == "0")
                {
                    var xml = DecodeXml(raw);
                    doc.Fields.AddRange(XmpMetadata.Read(xml, "tiff"));
                    doc.Entries.Add(new TiffEntry
                    {
                        Id = Id(path, tag),
                        Tag = tag,
                        Type = type,
                        Count = cnt,
                        EntryOffset = entryPos,
                        Inline = inline,
                        ValueOffset = valuePos,
                        Raw = raw
                    });
                    continue;
                }

                var described = Describe(tag, type, cnt, raw, doc.LittleEndian, gps);
                bool readOnly = described.ReadOnly || ReadOnlyTags.Contains(tag);
                var field = MakeField(
                    Id(path, tag),
                    group,
                    described.Name,
                    described.Technical,
                    described.Kind,
                    described.Text,
                    readOnly);
                doc.Fields.Add(field);
                doc.Entries.Add(new TiffEntry
                {
                    Id = field.Id,
                    Tag = tag,
                    Type = type,
                    Count = cnt,
                    EntryOffset = entryPos,
                    Inline = inline,
                    ValueOffset = valuePos,
                    Raw = raw
                });
            }

            uint next = ReadU32(doc, entries + count * 12);
            if (next > 0 && next < doc.Data.Length)
                Walk(doc, (int)next, (pageIndex + 1).ToString(CultureInfo.InvariantCulture), pageIndex + 1, jpegExif, seen);
        }

        private static string GroupName(string path, int pageIndex, bool jpegExif)
        {
            if (path.Contains("/gps", StringComparison.Ordinal))
                return pageIndex == 0 ? "GPS" : jpegExif ? "Миниатюра — GPS" : $"Страница {pageIndex + 1} — GPS";
            if (path.Contains("/exif", StringComparison.Ordinal) && !path.Contains("/interop", StringComparison.Ordinal))
                return pageIndex == 0 ? "Камера" : jpegExif ? "Миниатюра — камера" : $"Страница {pageIndex + 1} — камера";
            if (path.Contains("/interop", StringComparison.Ordinal))
                return "Совместимость";
            if (pageIndex == 0)
                return "EXIF";
            if (jpegExif)
                return pageIndex == 1 ? "Миниатюра" : $"Миниатюра {pageIndex}";
            return $"Страница {pageIndex + 1}";
        }

        private static (string Name, string Technical, string Kind, string Text, bool ReadOnly) Describe(
            ushort tag, ushort type, uint count, byte[] raw, bool le, bool gps)
        {
            string technical = (gps ? "GPS 0x" : "EXIF 0x") + tag.ToString("X4", CultureInfo.InvariantCulture);
            string name = gps
                ? GpsNames.TryGetValue(tag, out var g) ? g : "GPS 0x" + tag.ToString("X4", CultureInfo.InvariantCulture)
                : Names.TryGetValue(tag, out var n) ? n : "Тег 0x" + tag.ToString("X4", CultureInfo.InvariantCulture);

            if (tag == 0x927C)
                return (name, technical, FieldKind.ReadOnly, $"({raw.Length} байт)", true);

            if (tag is >= 0x9C9B and <= 0x9C9F)
                return (name, technical, FieldKind.Utf16, DecodeUtf16(raw), false);

            if (tag == 0x9286)
                return DescribeUserComment(name, technical, raw);

            if (tag == 0x0112)
                technical += " · 1 нормально, 3 = 180°, 6 = 90° по часовой, 8 = 90° против";

            if (type == 2)
                return (name, technical, FieldKind.Ascii, DecodeAscii(raw), false);

            if (type is 1 or 6 && count <= 16)
            {
                var text = Join(raw.Select(b => b.ToString(CultureInfo.InvariantCulture)));
                return (name, technical, FieldKind.Byte, text, false);
            }

            if (type is 3 or 8 && Fits(count, 2, raw))
            {
                bool signed = type == 8;
                var text = Join(Enumerable.Range(0, (int)count).Select(i =>
                    signed
                        ? ReadI16(raw, i * 2, le).ToString(CultureInfo.InvariantCulture)
                        : ReadU16(raw, i * 2, le).ToString(CultureInfo.InvariantCulture)));
                return (name, technical, signed ? FieldKind.SShort : FieldKind.UShort, text, false);
            }

            if (type is 4 or 9 && Fits(count, 4, raw))
            {
                bool signed = type == 9;
                var text = Join(Enumerable.Range(0, (int)count).Select(i =>
                    signed
                        ? ReadI32(raw, i * 4, le).ToString(CultureInfo.InvariantCulture)
                        : ReadU32(raw, i * 4, le).ToString(CultureInfo.InvariantCulture)));
                return (name, technical, signed ? FieldKind.SLong : FieldKind.ULong, text, false);
            }

            if (type is 5 or 10 && Fits(count, 8, raw))
            {
                bool signed = type == 10;
                var text = Join(Enumerable.Range(0, (int)count).Select(i =>
                {
                    if (signed)
                        return FormatRational(ReadI32(raw, i * 8, le), ReadI32(raw, i * 8 + 4, le));

                    return FormatRational(ReadU32(raw, i * 8, le), ReadU32(raw, i * 8 + 4, le));
                }));
                return (name, technical, signed ? FieldKind.SRational : FieldKind.Rational, text, false);
            }

            if (type == 11 && Fits(count, 4, raw))
            {
                var text = Join(Enumerable.Range(0, (int)count).Select(i =>
                    ReadFloat(raw, i * 4, le).ToString("G9", CultureInfo.InvariantCulture)));
                return (name, technical, FieldKind.Float, text, false);
            }

            if (type == 12 && Fits(count, 8, raw))
            {
                var text = Join(Enumerable.Range(0, (int)count).Select(i =>
                    ReadDouble(raw, i * 8, le).ToString("G17", CultureInfo.InvariantCulture)));
                return (name, technical, FieldKind.Double, text, false);
            }

            if (IsPrintable(raw) && raw.Length <= 64)
                return (name, technical, FieldKind.ReadOnly, DecodeAscii(raw), true);

            return (name, technical, FieldKind.ReadOnly, $"({raw.Length} байт)", true);
        }

        private static (string Name, string Technical, string Kind, string Text, bool ReadOnly) DescribeUserComment(
            string name, string technical, byte[] raw)
        {
            if (raw.Length < 8)
                return (name, technical, FieldKind.ReadOnly, $"({raw.Length} байт)", true);

            var header = Encoding.ASCII.GetString(raw, 0, 8);
            var body = raw[8..];
            if (header.StartsWith("UNICODE", StringComparison.Ordinal))
            {
                bool le = LooksLikeUtf16Le(body);
                return (name, technical, le ? FieldKind.UserCommentLe : FieldKind.UserCommentBe, DecodeUtf16Body(body, le), false);
            }

            if (header.StartsWith("ASCII", StringComparison.Ordinal) || header.All(c => c == '\0'))
                return (name, technical, FieldKind.UserCommentAscii, DecodeAscii(body), false);

            return (name, technical, FieldKind.ReadOnly, $"({raw.Length} байт)", true);
        }

        private static byte[] Encode(MetadataField field, TiffEntry entry, bool le)
        {
            var text = MetadataField.Normalize(field.Value);
            return field.Kind switch
            {
                FieldKind.Ascii => EncodeAscii(text, field.Name),
                FieldKind.Utf16 => EncodeUtf16(text),
                FieldKind.UserCommentLe => EncodeUserComment(entry.Raw, text, unicodeLe: true),
                FieldKind.UserCommentBe => EncodeUserComment(entry.Raw, text, unicodeLe: false),
                FieldKind.UserCommentAscii => EncodeUserCommentAscii(entry.Raw, text, field.Name),
                FieldKind.Byte => EncodeBytes(text, entry.Count),
                FieldKind.UShort => EncodeU16(text, entry.Count, le, signed: false),
                FieldKind.SShort => EncodeU16(text, entry.Count, le, signed: true),
                FieldKind.ULong => EncodeU32(text, entry.Count, le, signed: false),
                FieldKind.SLong => EncodeU32(text, entry.Count, le, signed: true),
                FieldKind.Rational => EncodeRational(text, entry.Count, le, signed: false),
                FieldKind.SRational => EncodeRational(text, entry.Count, le, signed: true),
                FieldKind.Float => EncodeFloat(text, entry.Count, le),
                FieldKind.Double => EncodeDouble(text, entry.Count, le),
                _ => throw new InvalidOperationException("это поле нельзя изменить")
            };
        }

        private static byte[] EncodeAscii(string text, string? fieldName)
        {
            if (text.Any(c => c > 255))
                throw new ArgumentException(
                    fieldName == null
                        ? "символ не помещается в ASCII/Latin-1. Для Unicode используйте поле Windows."
                        : "символ не помещается в это поле. Для Unicode используйте поле «Автор (Windows)», «Заголовок» или «Комментарий».");

            var bytes = Latin1.GetBytes(text);
            var result = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static byte[] EncodeUtf16(string text) =>
            Encoding.Unicode.GetBytes(text + "\0");

        private static byte[] EncodeUserComment(byte[] original, string text, bool unicodeLe)
        {
            var header = original.Length >= 8 ? original[..8] : "UNICODE\0"u8.ToArray();
            var body = unicodeLe
                ? Encoding.Unicode.GetBytes(text)
                : Encoding.BigEndianUnicode.GetBytes(text);
            var result = new byte[8 + body.Length];
            Buffer.BlockCopy(header, 0, result, 0, 8);
            Buffer.BlockCopy(body, 0, result, 8, body.Length);
            return result;
        }

        private static byte[] EncodeUserCommentAscii(byte[] original, string text, string fieldName)
        {
            if (text.Any(c => c > 255))
                throw new ArgumentException("символ не помещается в ASCII-комментарий.");

            var header = original.Length >= 8 ? original[..8] : "ASCII\0\0\0"u8.ToArray();
            var body = Latin1.GetBytes(text);
            var result = new byte[8 + body.Length];
            Buffer.BlockCopy(header, 0, result, 0, 8);
            Buffer.BlockCopy(body, 0, result, 8, body.Length);
            return result;
        }

        private static string[] Parts(string text, uint expected)
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != expected)
                throw new FormatException($"ожидается {expected} значений через запятую.");
            return parts;
        }

        private static byte[] EncodeBytes(string text, uint count)
        {
            var parts = Parts(text, count);
            var raw = new byte[count];
            for (int i = 0; i < parts.Length; i++)
                raw[i] = byte.Parse(parts[i], CultureInfo.InvariantCulture);
            return raw;
        }

        private static byte[] EncodeU16(string text, uint count, bool le, bool signed)
        {
            var parts = Parts(text, count);
            var raw = new byte[count * 2];
            for (int i = 0; i < parts.Length; i++)
            {
                ushort value = signed
                    ? (ushort)short.Parse(parts[i], CultureInfo.InvariantCulture)
                    : ushort.Parse(parts[i], CultureInfo.InvariantCulture);
                WriteU16(raw, i * 2, value, le);
            }

            return raw;
        }

        private static byte[] EncodeU32(string text, uint count, bool le, bool signed)
        {
            var parts = Parts(text, count);
            var raw = new byte[count * 4];
            for (int i = 0; i < parts.Length; i++)
            {
                uint value = signed
                    ? (uint)int.Parse(parts[i], CultureInfo.InvariantCulture)
                    : uint.Parse(parts[i], CultureInfo.InvariantCulture);
                WriteU32(raw, i * 4, value, le);
            }

            return raw;
        }

        private static byte[] EncodeRational(string text, uint count, bool le, bool signed)
        {
            var parts = Parts(text, count);
            var raw = new byte[count * 8];
            for (int i = 0; i < parts.Length; i++)
            {
                var bits = parts[i].Split('/');
                if (bits.Length != 2)
                    throw new FormatException("ожидается формат 72/1.");

                if (signed)
                {
                    WriteU32(raw, i * 8, (uint)int.Parse(bits[0].Trim(), CultureInfo.InvariantCulture), le);
                    WriteU32(raw, i * 8 + 4, (uint)int.Parse(bits[1].Trim(), CultureInfo.InvariantCulture), le);
                }
                else
                {
                    WriteU32(raw, i * 8, uint.Parse(bits[0].Trim(), CultureInfo.InvariantCulture), le);
                    WriteU32(raw, i * 8 + 4, uint.Parse(bits[1].Trim(), CultureInfo.InvariantCulture), le);
                }
            }

            return raw;
        }

        private static byte[] EncodeFloat(string text, uint count, bool le)
        {
            var parts = Parts(text, count);
            var raw = new byte[count * 4];
            for (int i = 0; i < parts.Length; i++)
            {
                var bytes = BitConverter.GetBytes(float.Parse(parts[i], CultureInfo.InvariantCulture));
                if (!le)
                    Array.Reverse(bytes);
                Buffer.BlockCopy(bytes, 0, raw, i * 4, 4);
            }

            return raw;
        }

        private static byte[] EncodeDouble(string text, uint count, bool le)
        {
            var parts = Parts(text, count);
            var raw = new byte[count * 8];
            for (int i = 0; i < parts.Length; i++)
            {
                var bytes = BitConverter.GetBytes(double.Parse(parts[i], CultureInfo.InvariantCulture));
                if (!le)
                    Array.Reverse(bytes);
                Buffer.BlockCopy(bytes, 0, raw, i * 8, 8);
            }

            return raw;
        }

        private static void WriteValue(TiffDoc doc, TiffEntry entry, byte[] value)
        {
            uint count = entry.Type is 2 or 1 or 7
                ? (uint)value.Length
                : entry.Count;

            if (entry.Type is not (2 or 1 or 7))
            {
                int expected = TypeSize(entry.Type) * (int)entry.Count;
                if (value.Length != expected)
                    throw new InvalidOperationException("несовпадение размера значения.");
            }

            WriteU32(doc, entry.EntryOffset + 4, count);
            if (value.Length <= 4)
            {
                var slot = new byte[4];
                Buffer.BlockCopy(value, 0, slot, 0, value.Length);
                Buffer.BlockCopy(slot, 0, doc.Data, entry.EntryOffset + 8, 4);
                entry.Inline = true;
                entry.Raw = value;
                entry.Count = count;
                return;
            }

            if (!entry.Inline && value.Length <= entry.Raw.Length)
            {
                Buffer.BlockCopy(value, 0, doc.Data, entry.ValueOffset, value.Length);
                entry.Raw = value;
                entry.Count = count;
                return;
            }

            AlignEven(doc);
            int offset = doc.Data.Length;
            var grown = new byte[doc.Data.Length + value.Length];
            Buffer.BlockCopy(doc.Data, 0, grown, 0, doc.Data.Length);
            Buffer.BlockCopy(value, 0, grown, offset, value.Length);
            doc.Data = grown;
            WriteU32(doc, entry.EntryOffset + 8, (uint)offset);
            entry.Inline = false;
            entry.ValueOffset = offset;
            entry.Raw = value;
            entry.Count = count;
        }

        private static void InsertEntry(TiffDoc doc, ushort tag, ushort type, byte[] value)
        {
            int ifdPos = (int)ReadU32(doc, 4);
            int count = ReadU16(doc, ifdPos);
            int insertPos = ifdPos + 2 + count * 12;
            doc.Data = Insert(doc.Data, insertPos, new byte[12]);
            Fixup(doc, ifdPos, insertPos, 12, new HashSet<int>());
            WriteU16(doc, ifdPos, (ushort)(count + 1));

            uint valueCount = type is 1 or 2 or 7 ? (uint)value.Length : (uint)(value.Length / TypeSize(type));
            WriteU16(doc, insertPos, tag);
            WriteU16(doc, insertPos + 2, type);
            WriteU32(doc, insertPos + 4, valueCount);

            if (value.Length <= 4)
            {
                var slot = new byte[4];
                Buffer.BlockCopy(value, 0, slot, 0, value.Length);
                Buffer.BlockCopy(slot, 0, doc.Data, insertPos + 8, 4);
                return;
            }

            AlignEven(doc);
            int offset = doc.Data.Length;
            var grown = new byte[doc.Data.Length + value.Length];
            Buffer.BlockCopy(doc.Data, 0, grown, 0, doc.Data.Length);
            Buffer.BlockCopy(value, 0, grown, offset, value.Length);
            doc.Data = grown;
            WriteU32(doc, insertPos + 8, (uint)offset);
        }

        private static void Fixup(TiffDoc doc, int ifdPos, int insertPos, int delta, HashSet<int> seen)
        {
            if (!seen.Add(ifdPos))
                return;
            if (ifdPos < 0 || ifdPos + 6 > doc.Data.Length)
                return;

            int count = ReadU16(doc, ifdPos);
            if (count > 2000)
                return;

            int entries = ifdPos + 2;
            int naturalNext = entries + count * 12;
            int nextPos = naturalNext == insertPos ? naturalNext + delta : naturalNext;
            if (nextPos + 4 > doc.Data.Length || entries + count * 12 > doc.Data.Length)
                return;

            for (int i = 0; i < count; i++)
            {
                int entry = entries + i * 12;
                ushort tag = ReadU16(doc, entry);
                ushort type = ReadU16(doc, entry + 2);
                uint cnt = ReadU32(doc, entry + 4);
                int typeSize = TypeSize(type);
                if (typeSize == 0)
                    continue;

                long byteLenLong = (long)typeSize * cnt;
                if (byteLenLong > int.MaxValue)
                    continue;
                int byteLen = (int)byteLenLong;

                if (IfdPointerTags.Contains(tag) || OffsetTags.Contains(tag))
                {
                    ShiftOffsetValues(doc, entry, type, cnt, byteLen, insertPos, delta);
                    if (IfdPointerTags.Contains(tag))
                    {
                        foreach (var p in ReadOffsetList(doc, entry, type, cnt, byteLen))
                        {
                            if (p > 0 && p < doc.Data.Length)
                                Fixup(doc, (int)p, insertPos, delta, seen);
                        }
                    }
                }
                else if (byteLen > 4)
                {
                    uint p = ReadU32(doc, entry + 8);
                    if (p >= (uint)insertPos)
                        WriteU32(doc, entry + 8, p + (uint)delta);
                }
            }

            uint next = ReadU32(doc, nextPos);
            if (next >= (uint)insertPos)
            {
                next += (uint)delta;
                WriteU32(doc, nextPos, next);
            }

            if (next > 0 && next < doc.Data.Length)
                Fixup(doc, (int)next, insertPos, delta, seen);
        }

        private static void ShiftOffsetValues(TiffDoc doc, int entry, ushort type, uint count, int byteLen, int insertPos, int delta)
        {
            int typeSize = TypeSize(type);
            if (typeSize is not (2 or 4))
                return;

            if (byteLen <= 4)
            {
                for (int i = 0; i < count; i++)
                {
                    int pos = entry + 8 + i * typeSize;
                    uint p = typeSize == 2 ? ReadU16(doc, pos) : ReadU32(doc, pos);
                    if (p >= (uint)insertPos)
                    {
                        p += (uint)delta;
                        if (typeSize == 2)
                            WriteU16(doc, pos, (ushort)p);
                        else
                            WriteU32(doc, pos, p);
                    }
                }

                return;
            }

            uint arrayPos = ReadU32(doc, entry + 8);
            if (arrayPos >= (uint)insertPos)
            {
                arrayPos += (uint)delta;
                WriteU32(doc, entry + 8, arrayPos);
            }

            for (int i = 0; i < count; i++)
            {
                int pos = (int)arrayPos + i * typeSize;
                if (pos < 0 || pos + typeSize > doc.Data.Length)
                    return;
                uint p = typeSize == 2 ? ReadU16(doc, pos) : ReadU32(doc, pos);
                if (p >= (uint)insertPos)
                {
                    p += (uint)delta;
                    if (typeSize == 2)
                        WriteU16(doc, pos, (ushort)p);
                    else
                        WriteU32(doc, pos, p);
                }
            }
        }

        private static uint ReadOffset(TiffDoc doc, int entry, ushort type, uint count, int byteLen)
        {
            var list = ReadOffsetList(doc, entry, type, count, byteLen);
            return list.Length > 0 ? list[0] : 0;
        }

        private static uint[] ReadOffsetList(TiffDoc doc, int entry, ushort type, uint count, int byteLen)
        {
            int typeSize = TypeSize(type);
            if (typeSize is not (2 or 4) || count == 0)
                return [];

            var result = new uint[count];
            if (byteLen <= 4)
            {
                for (int i = 0; i < count; i++)
                {
                    int pos = entry + 8 + i * typeSize;
                    result[i] = typeSize == 2 ? ReadU16(doc, pos) : ReadU32(doc, pos);
                }

                return result;
            }

            int arrayPos = (int)ReadU32(doc, entry + 8);
            for (int i = 0; i < count; i++)
            {
                int pos = arrayPos + i * typeSize;
                if (pos < 0 || pos + typeSize > doc.Data.Length)
                    return result;
                result[i] = typeSize == 2 ? ReadU16(doc, pos) : ReadU32(doc, pos);
            }

            return result;
        }

        private static void AlignEven(TiffDoc doc)
        {
            if ((doc.Data.Length & 1) == 0)
                return;
            var grown = new byte[doc.Data.Length + 1];
            Buffer.BlockCopy(doc.Data, 0, grown, 0, doc.Data.Length);
            doc.Data = grown;
        }

        private static byte[] Insert(byte[] source, int index, byte[] extra)
        {
            var result = new byte[source.Length + extra.Length];
            Buffer.BlockCopy(source, 0, result, 0, index);
            Buffer.BlockCopy(extra, 0, result, index, extra.Length);
            Buffer.BlockCopy(source, index, result, index + extra.Length, source.Length - index);
            return result;
        }

        private static string DecodeAscii(byte[] raw)
        {
            int end = Array.IndexOf(raw, (byte)0);
            if (end < 0)
                end = raw.Length;
            return Latin1.GetString(raw, 0, end);
        }

        private static string DecodeUtf16(byte[] raw)
        {
            int len = raw.Length;
            if (len >= 2 && raw[len - 1] == 0 && raw[len - 2] == 0)
                len -= 2;
            if ((len & 1) == 1)
                len--;
            return Encoding.Unicode.GetString(raw, 0, Math.Max(0, len));
        }

        private static string DecodeUtf16Body(byte[] raw, bool le)
        {
            int len = raw.Length - (raw.Length & 1);
            var encoding = le ? Encoding.Unicode : Encoding.BigEndianUnicode;
            var text = encoding.GetString(raw, 0, len);
            return text.TrimEnd('\0');
        }

        private static string DecodeXml(byte[] raw)
        {
            int len = raw.Length;
            while (len > 0 && raw[len - 1] == 0)
                len--;
            return Encoding.UTF8.GetString(raw, 0, len);
        }

        private static bool LooksLikeUtf16Le(byte[] data)
        {
            int le = 0, be = 0;
            int len = data.Length - (data.Length & 1);
            for (int i = 0; i + 1 < len; i += 2)
            {
                if (data[i + 1] == 0 && data[i] != 0) le++;
                if (data[i] == 0 && data[i + 1] != 0) be++;
            }

            return le >= be;
        }

        private static bool IsPrintable(byte[] raw)
        {
            if (raw.Length == 0)
                return false;
            return raw.All(b => b is >= 32 and <= 126 or 0);
        }

        private static bool Fits(uint count, int size, byte[] raw) =>
            count > 0 && count <= int.MaxValue / size && (long)count * size == raw.Length;

        private static string FormatRational(int numerator, int denominator) =>
            numerator.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture);

        private static string FormatRational(uint numerator, uint denominator) =>
            numerator.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture);

        private static string Join(IEnumerable<string> parts) => string.Join(", ", parts);

        private static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 0
        };

        private static string Id(string path, ushort tag) =>
            "exif:" + path + ":" + tag.ToString("X4", CultureInfo.InvariantCulture);

        private static string Technical(ushort tag, string path) =>
            (path.Contains("/gps", StringComparison.Ordinal) ? "GPS 0x" : "EXIF 0x")
            + tag.ToString("X4", CultureInfo.InvariantCulture);

        private static bool TryParseId(string id, out string path, out ushort tag)
        {
            path = "";
            tag = 0;
            if (!id.StartsWith("exif:", StringComparison.Ordinal))
                return false;
            var rest = id[5..];
            int colon = rest.LastIndexOf(':');
            if (colon <= 0)
                return false;
            path = rest[..colon];
            return ushort.TryParse(rest[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out tag);
        }

        private static MetadataField MakeField(string id, string group, string name, string technical, string kind, string value, bool readOnly) =>
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

        private static ushort ReadU16(TiffDoc doc, int pos) => ReadU16(doc.Data, pos, doc.LittleEndian);
        private static uint ReadU32(TiffDoc doc, int pos) => ReadU32(doc.Data, pos, doc.LittleEndian);

        private static ushort ReadU16(byte[] data, int pos, bool le)
        {
            if ((uint)pos + 1 >= (uint)data.Length)
                return 0;
            return le
                ? (ushort)(data[pos] | (data[pos + 1] << 8))
                : (ushort)((data[pos] << 8) | data[pos + 1]);
        }

        private static uint ReadU32(byte[] data, int pos, bool le)
        {
            if ((uint)pos + 3 >= (uint)data.Length)
                return 0;
            return le
                ? (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24))
                : (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        }

        private static short ReadI16(byte[] data, int pos, bool le) => (short)ReadU16(data, pos, le);
        private static int ReadI32(byte[] data, int pos, bool le) => (int)ReadU32(data, pos, le);

        private static float ReadFloat(byte[] data, int pos, bool le)
        {
            var bytes = data[pos..(pos + 4)];
            if (!le)
                Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        private static double ReadDouble(byte[] data, int pos, bool le)
        {
            var bytes = data[pos..(pos + 8)];
            if (!le)
                Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        private static void WriteU16(TiffDoc doc, int pos, ushort value) => WriteU16(doc.Data, pos, value, doc.LittleEndian);
        private static void WriteU32(TiffDoc doc, int pos, uint value) => WriteU32(doc.Data, pos, value, doc.LittleEndian);

        private static void WriteU16(byte[] data, int pos, ushort value, bool le)
        {
            if (le)
            {
                data[pos] = (byte)value;
                data[pos + 1] = (byte)(value >> 8);
            }
            else
            {
                data[pos] = (byte)(value >> 8);
                data[pos + 1] = (byte)value;
            }
        }

        private static void WriteU32(byte[] data, int pos, uint value, bool le)
        {
            if (le)
            {
                data[pos] = (byte)value;
                data[pos + 1] = (byte)(value >> 8);
                data[pos + 2] = (byte)(value >> 16);
                data[pos + 3] = (byte)(value >> 24);
            }
            else
            {
                data[pos] = (byte)(value >> 24);
                data[pos + 1] = (byte)(value >> 16);
                data[pos + 2] = (byte)(value >> 8);
                data[pos + 3] = (byte)value;
            }
        }

        private sealed class TiffDoc
        {
            public byte[] Data = [];
            public bool LittleEndian;
            public List<MetadataField> Fields = [];
            public List<TiffEntry> Entries = [];
        }

        private sealed class TiffEntry
        {
            public string Id = "";
            public ushort Tag;
            public ushort Type;
            public uint Count;
            public int EntryOffset;
            public bool Inline;
            public int ValueOffset;
            public byte[] Raw = [];
        }
    }
}

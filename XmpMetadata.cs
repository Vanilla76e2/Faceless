using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Faceless
{
    /// <summary>Читает и записывает текстовые поля XMP-пакета.</summary>
    static class XmpMetadata
    {
        private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            ["creator"] = "Автор",
            ["title"] = "Название",
            ["description"] = "Описание",
            ["rights"] = "Права",
            ["subject"] = "Ключевые слова",
            ["creatortool"] = "Программа",
            ["createdate"] = "Дата создания",
            ["modifydate"] = "Дата изменения",
            ["metadatadate"] = "Дата метаданных",
            ["credit"] = "Поставщик",
            ["source"] = "Источник",
            ["authorsposition"] = "Должность",
            ["captionwriter"] = "Автор подписи",
            ["headline"] = "Заголовок",
            ["city"] = "Город",
            ["state"] = "Регион",
            ["country"] = "Страна",
            ["copyright"] = "Авторские права",
            ["keywords"] = "Ключевые слова",
            ["producer"] = "Программа PDF",
            ["nickname"] = "Псевдоним",
            ["label"] = "Метка",
            ["rating"] = "Рейтинг"
        };

        public static List<MetadataField> Read(string xml, string source)
        {
            var fields = new List<MetadataField>();
            if (!TryParse(xml, out var doc) || doc?.Root == null)
                return fields;

            var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int ordinal = 0;
            foreach (var slot in Enumerate(doc))
            {
                var label = DisplayName(slot.Label, usedNames);
                var value = slot.Value;
                fields.Add(new MetadataField
                {
                    Id = $"xmp:{source}#{ordinal}",
                    Group = "XMP",
                    Name = label,
                    TechnicalName = slot.Technical,
                    Kind = FieldKind.Xmp,
                    OriginalValue = value,
                    Value = value
                });
                ordinal++;
            }

            return fields;
        }

        /// <summary>Возвращает новый XML, если изменилось хотя бы одно поле этого источника.</summary>
        public static string? Apply(string xml, IReadOnlyList<MetadataField> fields, string source)
        {
            var changed = fields.Where(f => f.Id.StartsWith($"xmp:{source}#", StringComparison.Ordinal) && f.IsChanged).ToList();
            if (changed.Count == 0)
                return null;

            if (!TryParse(xml, out var doc) || doc == null)
                throw new InvalidDataException("Не удалось разобрать XMP.");

            var slots = Enumerate(doc).ToList();
            foreach (var field in changed)
            {
                var hash = field.Id.LastIndexOf('#');
                if (hash < 0 || !int.TryParse(field.Id[(hash + 1)..], out var ordinal) || ordinal < 0 || ordinal >= slots.Count)
                    throw new InvalidDataException($"Не найдено поле XMP «{field.Name}».");

                slots[ordinal].Set(MetadataField.Normalize(field.Value));
            }

            return ToXml(doc);
        }

        private static bool TryParse(string xml, out XDocument? doc)
        {
            doc = null;
            try
            {
                doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private static IEnumerable<XmpSlot> Enumerate(XDocument doc)
        {
            foreach (var desc in doc.Descendants().Where(e => e.Name.LocalName == "Description"))
            {
                foreach (var attr in desc.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                        continue;
                    if (attr.Name.LocalName is "about" or "parseType" or "lang")
                        continue;
                    if (attr.Value.Length > 4000)
                        continue;
                    yield return new XmpAttrSlot(attr);
                }

                foreach (var el in desc.Descendants())
                {
                    if (el.Elements().Any())
                        continue;
                    if (el.Value.Length > 4000)
                        continue;
                    if (string.IsNullOrWhiteSpace(el.Value) && el.Name.LocalName != "li")
                        continue;
                    yield return new XmpElementSlot(el);
                }
            }
        }

        private static string DisplayName(string local, Dictionary<string, int> used)
        {
            var baseName = Names.TryGetValue(local, out var friendly) ? friendly : local;
            used.TryGetValue(baseName, out var n);
            used[baseName] = n + 1;
            return n == 0 ? baseName : $"{baseName} ({n + 1})";
        }

        private static string ToXml(XDocument doc)
        {
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = doc.Declaration == null,
                Indent = false,
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                ConformanceLevel = ConformanceLevel.Document
            };

            using var ms = new MemoryStream();
            using (var writer = XmlWriter.Create(ms, settings))
                doc.Save(writer);
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        private abstract class XmpSlot
        {
            public abstract string Label { get; }
            public abstract string Technical { get; }
            public abstract string Value { get; }
            public abstract void Set(string value);
        }

        private sealed class XmpAttrSlot(XAttribute attr) : XmpSlot
        {
            public override string Label => attr.Name.LocalName;
            public override string Technical => attr.Name.ToString();
            public override string Value => attr.Value;
            public override void Set(string value) => attr.Value = value;
        }

        private sealed class XmpElementSlot(XElement el) : XmpSlot
        {
            public override string Label => el.Name.LocalName;
            public override string Technical => el.Name.ToString();
            public override string Value => el.Value;
            public override void Set(string value) => el.Value = value;
        }
    }
}

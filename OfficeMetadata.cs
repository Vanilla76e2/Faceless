using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace Faceless
{
    /// <summary>Свойства документов Office Open XML: DOCX, XLSX, PPTX.</summary>
    static class OfficeMetadata
    {
        private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
        private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace AppNs = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

        private static readonly Spec[] CoreSpecs =
        [
            new("title", "Название", Dc, "dc:title", null),
            new("subject", "Тема", Dc, "dc:subject", null),
            new("creator", "Автор", Dc, "dc:creator", null),
            new("keywords", "Ключевые слова", Cp, "cp:keywords", null),
            new("description", "Описание", Dc, "dc:description", null),
            new("lastModifiedBy", "Последний изменивший", Cp, "cp:lastModifiedBy", null),
            new("revision", "Редакция", Cp, "cp:revision", null),
            new("category", "Категория", Cp, "cp:category", null),
            new("contentStatus", "Статус", Cp, "cp:contentStatus", null),
            new("created", "Создан", DcTerms, "dcterms:created", "dcterms:W3CDTF"),
            new("modified", "Изменён", DcTerms, "dcterms:modified", "dcterms:W3CDTF")
        ];

        private static readonly Spec[] AppSpecs =
        [
            new("Company", "Организация", AppNs, "Company", null),
            new("Manager", "Руководитель", AppNs, "Manager", null),
            new("Template", "Шаблон", AppNs, "Template", null),
            new("Application", "Приложение", AppNs, "Application", null),
            new("AppVersion", "Версия приложения", AppNs, "AppVersion", null),
            new("TotalTime", "Время редактирования", AppNs, "TotalTime", null)
        ];

        public static List<MetadataField> Read(string path)
        {
            var fields = new List<MetadataField>();
            using var archive = OpenRead(path);
            if (archive.GetEntry("docProps/core.xml") is { } core)
                fields.AddRange(ReadPart(core, CoreSpecs, "core", "Документ"));
            if (archive.GetEntry("docProps/app.xml") is { } app)
                fields.AddRange(ReadPart(app, AppSpecs, "app", "Приложение"));
            if (archive.GetEntry("docProps/custom.xml") is { } custom)
                fields.AddRange(ReadCustom(custom));
            return fields;
        }

        public static void Write(string source, string target, IReadOnlyList<MetadataField> fields)
        {
            var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(source));
            try
            {
                FileIo.CopyShared(source, temp);
                using (var archive = ZipFile.Open(temp, ZipArchiveMode.Update))
                {
                    if (archive.GetEntry("docProps/core.xml") is { } core)
                        UpdatePart(core, CoreSpecs, "core", fields);
                    if (archive.GetEntry("docProps/app.xml") is { } app)
                        UpdatePart(app, AppSpecs, "app", fields);
                    if (archive.GetEntry("docProps/custom.xml") is { } custom)
                        UpdateCustom(custom, fields);
                }

                FileIo.CopyShared(temp, target);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        private static List<MetadataField> ReadPart(ZipArchiveEntry entry, Spec[] specs, string prefix, string group)
        {
            var doc = Load(entry);
            var root = doc.Root ?? throw new InvalidDataException("Пустой файл свойств документа.");
            var simple = root.Elements().Where(e => !e.Elements().Any()).ToList();
            var used = new HashSet<XElement>();
            var fields = new List<MetadataField>();

            foreach (var spec in specs)
            {
                var matches = simple.Where(e => e.Name == spec.Ns + spec.Local).ToList();
                if (matches.Count == 0)
                {
                    fields.Add(Make($"{prefix}:{spec.Local}#0", group, spec.Name, spec.Technical, ""));
                    continue;
                }

                for (int i = 0; i < matches.Count; i++)
                {
                    used.Add(matches[i]);
                    var label = i == 0 ? spec.Name : $"{spec.Name} ({i + 1})";
                    fields.Add(Make($"{prefix}:{spec.Local}#{i}", group, label, spec.Technical, matches[i].Value));
                }
            }

            foreach (var extra in simple.Where(e => !used.Contains(e)))
            {
                int index = simple.Where(e => e.Name.LocalName == extra.Name.LocalName).ToList().IndexOf(extra);
                fields.Add(Make(
                    $"{prefix}:{extra.Name.LocalName}#{index}",
                    group,
                    extra.Name.LocalName,
                    extra.Name.LocalName,
                    extra.Value));
            }

            return fields;
        }

        private static void UpdatePart(ZipArchiveEntry entry, Spec[] specs, string prefix, IReadOnlyList<MetadataField> fields)
        {
            var relevant = fields.Where(f => f.IsChanged && f.Id.StartsWith(prefix + ":", StringComparison.Ordinal)).ToList();
            if (relevant.Count == 0)
                return;

            var doc = Load(entry);
            var root = doc.Root ?? throw new InvalidDataException("Пустой файл свойств документа.");

            foreach (var field in relevant)
            {
                if (!TryParseId(field.Id, prefix, out var local, out var index))
                    continue;

                var matches = root.Elements().Where(e => e.Name.LocalName == local && !e.Elements().Any()).ToList();
                var value = MetadataField.Normalize(field.Value);
                if (index < matches.Count)
                {
                    matches[index].Value = value;
                    continue;
                }

                if (index == 0 && matches.Count == 0 && value.Length > 0)
                {
                    var spec = specs.FirstOrDefault(s => s.Local == local);
                    if (spec == null)
                        continue;
                    var element = new XElement(spec.Ns + spec.Local, value);
                    if (spec.XsiType != null)
                        element.SetAttributeValue(Xsi + "type", spec.XsiType);
                    root.Add(element);
                }
            }

            Save(entry, doc);
        }

        private static List<MetadataField> ReadCustom(ZipArchiveEntry entry)
        {
            var doc = Load(entry);
            var fields = new List<MetadataField>();
            if (doc.Root == null)
                return fields;

            int index = 0;
            foreach (var prop in doc.Root.Elements().Where(e => e.Name.LocalName == "property"))
            {
                var name = (string?)prop.Attribute("name") ?? $"Свойство {index + 1}";
                var value = prop.Elements().FirstOrDefault()?.Value ?? "";
                fields.Add(Make($"custom#{index}", "Пользовательские", name, name, value));
                index++;
            }

            return fields;
        }

        private static void UpdateCustom(ZipArchiveEntry entry, IReadOnlyList<MetadataField> fields)
        {
            var relevant = fields.Where(f => f.IsChanged && f.Id.StartsWith("custom#", StringComparison.Ordinal)).ToList();
            if (relevant.Count == 0)
                return;

            var doc = Load(entry);
            if (doc.Root == null)
                return;

            var props = doc.Root.Elements().Where(e => e.Name.LocalName == "property").ToList();
            foreach (var field in relevant)
            {
                if (!int.TryParse(field.Id["custom#".Length..], out var index) || index < 0 || index >= props.Count)
                    throw new InvalidDataException($"Не найдено свойство «{field.Name}».");

                var valueElement = props[index].Elements().FirstOrDefault();
                if (valueElement != null)
                    valueElement.Value = MetadataField.Normalize(field.Value);
            }

            Save(entry, doc);
        }

        private static ZipArchive OpenRead(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static XDocument Load(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }

        private static void Save(ZipArchiveEntry entry, XDocument doc)
        {
            using var stream = entry.Open();
            stream.SetLength(0);
            doc.Save(stream);
        }

        private static bool TryParseId(string id, string prefix, out string local, out int index)
        {
            local = "";
            index = 0;
            var rest = id[(prefix.Length + 1)..];
            int hash = rest.LastIndexOf('#');
            if (hash <= 0)
                return false;
            local = rest[..hash];
            return int.TryParse(rest[(hash + 1)..], out index);
        }

        private static MetadataField Make(string id, string group, string name, string technical, string value) =>
            new()
            {
                Id = id,
                Group = group,
                Name = name,
                TechnicalName = technical,
                Kind = FieldKind.Text,
                OriginalValue = value,
                Value = value
            };

        private sealed record Spec(string Local, string Name, XNamespace Ns, string Technical, string? XsiType);
    }
}

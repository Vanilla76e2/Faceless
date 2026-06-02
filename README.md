# Faceless

Простая WPF-утилита для удаления информации об авторах из метаданных файлов.

## Поддерживаемые форматы

| Формат | Что очищается |
|--------|--------------|
| DOCX / XLSX / PPTX | `dc:creator`, `cp:lastModifiedBy`, `Company`, `Manager` в `docProps/core.xml` и `docProps/app.xml` |
| PDF | `Author`, `Creator`, `Subject`, XMP-метаданные |
| JPEG | Сегменты APP1 (EXIF/XMP) и APP13 (IPTC/Photoshop) |
| PNG | Чанки `tEXt`, `zTXt`, `iTXt` |
| TIFF | Поля `Artist` (0x013B) и `Copyright` (0x8298) в IFD |

## Использование

1. Перетащите файлы в окно приложения или нажмите на зону дропа для выбора через диалог.
2. При необходимости выберите папку для сохранения (по умолчанию — рядом с оригиналом).
3. Опционально отключите суффикс `_faceless` в имени выходного файла.
4. Нажмите **Удалить авторов**.

Исходные файлы не изменяются — результат всегда сохраняется как отдельный файл.

## Требования

- Windows 10/11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Зависимости

- [PDFsharp](https://github.com/empira/PDFsharp) 6.2.0 — работа с PDF-файлами

## Лицензия

[MIT](LICENSE)

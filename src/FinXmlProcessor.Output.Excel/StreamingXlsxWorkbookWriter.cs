using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FinXmlProcessor.Application.Abstractions;
using CellType = FinXmlProcessor.Domain.Cells.CellType;
using CellValue = FinXmlProcessor.Domain.Cells.CellValue;
using OxCellValue = DocumentFormat.OpenXml.Spreadsheet.CellValue;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Tables;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Output.Excel;

/// <summary>
/// Forward-only XLSX writer built on <see cref="OpenXmlWriter"/>. Every data row is streamed straight into the
/// worksheet part; inline strings avoid an unbounded shared-string table; sheets split at the row limit.
/// </summary>
public sealed class StreamingXlsxWorkbookWriter : IWorkbookWriter
{
    private readonly ILogger<StreamingXlsxWorkbookWriter> _logger;

    public StreamingXlsxWorkbookWriter(ILogger<StreamingXlsxWorkbookWriter> logger)
    {
        _logger = logger;
    }

    public Task<IWorkbookSession> BeginAsync(string finalPath, IReadOnlyList<OutputTableDefinition> tables, WorkbookWriterOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = new Session(finalPath, tables, options, _logger);
        return Task.FromResult<IWorkbookSession>(session);
    }

    public Task<IReadOnlyList<RecordIssue>> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        var issues = new List<RecordIssue>();
        try
        {
            using SpreadsheetDocument document = SpreadsheetDocument.Open(path, isEditable: false);
            WorkbookPart? workbook = document.WorkbookPart;
            if (workbook?.Workbook?.Sheets is null)
            {
                issues.Add(RecordIssue.Fatal(IssueCodes.OutputPackageInvalid, "Workbook has no sheet list."));
                return Task.FromResult<IReadOnlyList<RecordIssue>>(issues);
            }

            foreach (Sheet sheet in workbook.Workbook.Sheets.Elements<Sheet>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sheet.Id?.Value is null || workbook.GetPartById(sheet.Id.Value) is not WorksheetPart part)
                {
                    issues.Add(RecordIssue.Fatal(IssueCodes.OutputPackageInvalid, $"Sheet '{sheet.Name}' has no worksheet part."));
                    continue;
                }

                // Forward-only walk: verifies the part is well-formed XML without loading it as a DOM.
                using OpenXmlReader reader = OpenXmlReader.Create(part);
                long rows = 0;
                while (reader.Read())
                {
                    if (reader.ElementType == typeof(Row) && reader.IsStartElement)
                    {
                        rows++;
                    }
                }

                if (rows == 0)
                {
                    issues.Add(RecordIssue.Warning(IssueCodes.OutputPackageInvalid, null, $"Sheet '{sheet.Name}' contains no rows."));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OpenXmlPackageException or System.Xml.XmlException or FileFormatException)
        {
            issues.Add(RecordIssue.Fatal(IssueCodes.OutputPackageInvalid, $"Workbook could not be opened ({ex.GetType().Name})."));
        }

        return Task.FromResult<IReadOnlyList<RecordIssue>>(issues);
    }

    private sealed class SheetState
    {
        public SheetState(OutputTableDefinition table, string baseName)
        {
            Table = table;
            BaseName = baseName;
        }

        public OutputTableDefinition Table { get; }

        public string BaseName { get; }

        public WorksheetPart? Part { get; set; }

        public OpenXmlWriter? Writer { get; set; }

        public string? CurrentName { get; set; }

        public int PartNumber { get; set; }

        public uint RowsInSheet { get; set; }

        public long TotalRows { get; set; }

        public uint[] StyleIndexes { get; set; } = [];
    }

    private sealed class Session : IWorkbookSession
    {
        private static readonly OutputTableDefinition RejectedTable = new(
            "__rejected",
            "Rejected Records",
            [
                new OutputColumnDefinition("ordinal", "Source Record #", CellType.Integer, Width: 16),
                new OutputColumnDefinition("identifier", "Identifier", CellType.Text, Width: 26),
                new OutputColumnDefinition("codes", "Error Codes", CellType.Text, Width: 20),
                new OutputColumnDefinition("messages", "Messages", CellType.Text, Width: 80),
                new OutputColumnDefinition("fields", "Safe Field Values", CellType.Text, Width: 80),
            ]);

        private readonly string _finalPath;
        private readonly string _stagingPath;
        private readonly WorkbookWriterOptions _options;
        private readonly ILogger _logger;
        private readonly SpreadsheetDocument _document;
        private readonly WorkbookPart _workbookPart;
        private readonly XlsxStyles _styles;
        private readonly SheetNaming.Allocator _names = new();
        private readonly Dictionary<string, SheetState> _states = new(StringComparer.Ordinal);
        private readonly List<(string Name, string RelId, bool AutoFilter, int Columns, uint LastRow)> _sheets = [];
        private SheetState? _rejected;
        private long _rowsWritten;
        private long _truncatedCells;
        private bool _completed;
        private bool _disposed;

        public Session(string finalPath, IReadOnlyList<OutputTableDefinition> tables, WorkbookWriterOptions options, ILogger logger)
        {
            _finalPath = finalPath;
            _stagingPath = finalPath + ".part";
            _options = options;
            _logger = logger;
            _names.Allocate("Summary");
            if (options.IncludeRejectedSheet)
            {
                _names.Allocate(RejectedTable.SheetName);
            }

            if (File.Exists(_stagingPath))
            {
                File.Delete(_stagingPath);
            }

            _document = SpreadsheetDocument.Create(_stagingPath, SpreadsheetDocumentType.Workbook);
            _workbookPart = _document.AddWorkbookPart();
            _styles = XlsxStyles.Create(_workbookPart, tables.Concat([RejectedTable]).ToList());

            foreach (OutputTableDefinition table in tables)
            {
                var state = new SheetState(table, table.SheetName) { StyleIndexes = _styles.ColumnStyles(table) };
                _states[table.Id] = state;
                OpenNextSheet(state);
            }
        }

        public long RowsWritten => _rowsWritten;

        public long TruncatedCells => _truncatedCells;

        public void WriteRow(OutputRow row)
        {
            ThrowIfUnusable();
            if (!_states.TryGetValue(row.TableId, out SheetState? state))
            {
                throw new InvalidOperationException($"Unknown output table '{row.TableId}'.");
            }

            if (state.RowsInSheet >= _options.MaxRowsPerSheet)
            {
                CloseSheet(state, autoFilter: true);
                OpenNextSheet(state);
            }

            WriteCells(state, row.Cells);
            _rowsWritten++;
        }

        public void WriteRejected(RejectedRecordLine line)
        {
            ThrowIfUnusable();
            if (!_options.IncludeRejectedSheet)
            {
                return;
            }

            if (_rejected is null)
            {
                _rejected = new SheetState(RejectedTable, RejectedTable.SheetName) { StyleIndexes = _styles.ColumnStyles(RejectedTable) };
                OpenNextSheet(_rejected);
            }

            if (_rejected.RowsInSheet >= _options.MaxRowsPerSheet)
            {
                CloseSheet(_rejected, autoFilter: true);
                OpenNextSheet(_rejected);
            }

            var fields = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in line.SafeFields)
            {
                if (fields.Length > 0)
                {
                    fields.Append("; ");
                }

                fields.Append(pair.Key).Append('=').Append(pair.Value);
            }

            WriteCells(_rejected,
            [
                CellValue.FromInteger(line.SourceOrdinal),
                CellValue.FromText(line.SafeIdentifier ?? string.Empty),
                CellValue.FromText(line.Codes),
                CellValue.FromText(line.Messages),
                CellValue.FromText(fields.ToString()),
            ]);
        }

        public Task<string> CompleteAsync(IReadOnlyList<SummaryEntry> summary, IReadOnlyList<RecordIssue> jobIssues, CancellationToken cancellationToken)
        {
            ThrowIfUnusable();
            cancellationToken.ThrowIfCancellationRequested();

            foreach (SheetState state in _states.Values)
            {
                CloseSheet(state, autoFilter: true);
            }

            if (_rejected is not null)
            {
                CloseSheet(_rejected, autoFilter: true);
            }

            WriteSummarySheet(summary, jobIssues);
            WriteWorkbook();
            _document.Dispose();
            _completed = true;

            if (File.Exists(_finalPath))
            {
                throw new IOException($"Output file already exists: {Path.GetFileName(_finalPath)}");
            }

            File.Move(_stagingPath, _finalPath, overwrite: false);
            return Task.FromResult(_finalPath);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            if (!_completed)
            {
                foreach (SheetState state in _states.Values)
                {
                    state.Writer?.Dispose();
                }

                _rejected?.Writer?.Dispose();
                try
                {
                    _document.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Ignoring error while discarding staging workbook");
                }

                try
                {
                    if (File.Exists(_stagingPath))
                    {
                        File.Delete(_stagingPath);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not delete staging workbook {Path}", _stagingPath);
                }
            }

            return ValueTask.CompletedTask;
        }

        private void ThrowIfUnusable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException("The workbook session is already completed.");
            }
        }

        private void OpenNextSheet(SheetState state)
        {
            state.PartNumber++;
            state.CurrentName = _names.Allocate(state.PartNumber == 1 ? state.BaseName : SheetNaming.WithSuffix(SheetNaming.Sanitize(state.BaseName), state.PartNumber));
            state.Part = _workbookPart.AddNewPart<WorksheetPart>();
            state.Writer = OpenXmlWriter.Create(state.Part);
            state.RowsInSheet = 0;
            OpenXmlWriter w = state.Writer;
            w.WriteStartElement(new Worksheet());
            WriteSheetViews(w);
            WriteColumns(w, state.Table);
            w.WriteStartElement(new SheetData());
            WriteHeaderRow(state);
        }

        private static void WriteSheetViews(OpenXmlWriter w)
        {
            w.WriteStartElement(new SheetViews());
            w.WriteStartElement(new SheetView { WorkbookViewId = 0 });
            w.WriteElement(new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen });
            w.WriteElement(new Selection { Pane = PaneValues.BottomLeft });
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void WriteColumns(OpenXmlWriter w, OutputTableDefinition table)
        {
            w.WriteStartElement(new Columns());
            for (int i = 0; i < table.Columns.Count; i++)
            {
                OutputColumnDefinition column = table.Columns[i];
                double width = column.Width ?? DefaultWidth(column);
                w.WriteElement(new Column { Min = (uint)(i + 1), Max = (uint)(i + 1), Width = Math.Clamp(width, 4, 255), CustomWidth = true });
            }

            w.WriteEndElement();
        }

        private static double DefaultWidth(OutputColumnDefinition column) => column.CellType switch
        {
            CellType.Text => Math.Clamp(column.Heading.Length + 4, 12, 40),
            CellType.Integer => 12,
            CellType.Decimal => 16,
            CellType.Date => 12,
            CellType.DateTime => 20,
            CellType.Boolean => 10,
            _ => 14,
        };

        private void WriteHeaderRow(SheetState state)
        {
            OpenXmlWriter w = state.Writer!;
            state.RowsInSheet++;
            w.WriteStartElement(new Row { RowIndex = state.RowsInSheet });
            foreach (OutputColumnDefinition column in state.Table.Columns)
            {
                WriteInlineString(w, column.Heading, _styles.HeaderStyle);
            }

            w.WriteEndElement();
        }

        private void WriteCells(SheetState state, IReadOnlyList<CellValue> cells)
        {
            OpenXmlWriter w = state.Writer!;
            state.RowsInSheet++;
            state.TotalRows++;
            w.WriteStartElement(new Row { RowIndex = state.RowsInSheet });
            int count = Math.Min(cells.Count, state.Table.Columns.Count);
            for (int i = 0; i < count; i++)
            {
                CellValue cell = cells[i];
                uint style = state.StyleIndexes[i];
                if (cell.IsBlank)
                {
                    w.WriteElement(new Cell { StyleIndex = style });
                    continue;
                }

                switch (cell.Type)
                {
                    case CellType.Text:
                        WriteInlineString(w, cell.TextValue, style);
                        break;
                    case CellType.Integer:
                        w.WriteElement(new Cell { StyleIndex = style, CellValue = new OxCellValue(cell.IntegerValue.ToString(CultureInfo.InvariantCulture)) });
                        break;
                    case CellType.Decimal:
                        w.WriteElement(new Cell { StyleIndex = style, CellValue = new OxCellValue(cell.DecimalValue.ToString(CultureInfo.InvariantCulture)) });
                        break;
                    case CellType.Date:
                        w.WriteElement(new Cell { StyleIndex = style, CellValue = new OxCellValue(cell.DateValue.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture)) });
                        break;
                    case CellType.DateTime:
                        w.WriteElement(new Cell { StyleIndex = style, CellValue = new OxCellValue(cell.DateTimeValue.ToOADate().ToString("R", CultureInfo.InvariantCulture)) });
                        break;
                    case CellType.Boolean:
                        w.WriteElement(new Cell { StyleIndex = style, DataType = CellValues.Boolean, CellValue = new OxCellValue(cell.BooleanValue ? "1" : "0") });
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown cell type {cell.Type}.");
                }
            }

            w.WriteEndElement();
        }

        /// <summary>
        /// Inline strings are always literal: Excel never evaluates them, so text beginning with =, +, - or @ cannot
        /// become a formula. No <c>&lt;f&gt;</c> element is ever emitted by this writer.
        /// </summary>
        private void WriteInlineString(OpenXmlWriter w, string text, uint style)
        {
            string safe = SanitizeText(text, _options.MaxCellTextLength, ref _truncatedCells);
            w.WriteStartElement(new Cell { DataType = CellValues.InlineString, StyleIndex = style });
            w.WriteStartElement(new InlineString());
            var t = new Text(safe);
            if (safe.Length > 0 && (char.IsWhiteSpace(safe[0]) || char.IsWhiteSpace(safe[^1])))
            {
                t.Space = SpaceProcessingModeValues.Preserve;
            }

            w.WriteElement(t);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static string SanitizeText(string text, int maxLength, ref long truncated)
        {
            bool needsCopy = text.Length > maxLength;
            if (!needsCopy)
            {
                foreach (char c in text)
                {
                    if (IsInvalidXmlChar(c))
                    {
                        needsCopy = true;
                        break;
                    }
                }
            }

            if (!needsCopy)
            {
                return text;
            }

            int length = Math.Min(text.Length, maxLength);
            if (text.Length > maxLength)
            {
                truncated++;
            }

            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                char c = text[i];
                sb.Append(IsInvalidXmlChar(c) ? '�' : c);
            }

            return sb.ToString();
        }

        private static bool IsInvalidXmlChar(char c) =>
            (c < 0x20 && c != 0x09 && c != 0x0A && c != 0x0D) || c == 0xFFFE || c == 0xFFFF;

        private void CloseSheet(SheetState state, bool autoFilter)
        {
            if (state.Writer is null || state.Part is null)
            {
                return;
            }

            OpenXmlWriter w = state.Writer;
            w.WriteEndElement(); // sheetData
            if (autoFilter && state.RowsInSheet > 0)
            {
                w.WriteElement(new AutoFilter { Reference = $"A1:{ColumnLetter(state.Table.Columns.Count)}{state.RowsInSheet}" });
            }

            w.WriteEndElement(); // worksheet
            w.Close();
            w.Dispose();
            _sheets.Add((state.CurrentName!, _workbookPart.GetIdOfPart(state.Part), autoFilter, state.Table.Columns.Count, state.RowsInSheet));
            state.Writer = null;
            state.Part = null;
        }

        private void WriteSummarySheet(IReadOnlyList<SummaryEntry> summary, IReadOnlyList<RecordIssue> jobIssues)
        {
            var table = new OutputTableDefinition("__summary", "Summary",
            [
                new OutputColumnDefinition("item", "Item", CellType.Text, Width: 28),
                new OutputColumnDefinition("value", "Value", CellType.Text, Width: 90),
            ]);
            WorksheetPart part = _workbookPart.AddNewPart<WorksheetPart>();
            using (OpenXmlWriter w = OpenXmlWriter.Create(part))
            {
                w.WriteStartElement(new Worksheet());
                WriteColumns(w, table);
                w.WriteStartElement(new SheetData());
                uint rowIndex = 1;
                w.WriteStartElement(new Row { RowIndex = rowIndex });
                WriteInlineString(w, "Item", _styles.HeaderStyle);
                WriteInlineString(w, "Value", _styles.HeaderStyle);
                w.WriteEndElement();
                foreach (SummaryEntry entry in summary)
                {
                    rowIndex++;
                    w.WriteStartElement(new Row { RowIndex = rowIndex });
                    WriteInlineString(w, entry.Label, _styles.TextStyle);
                    WriteInlineString(w, entry.Value, _styles.TextStyle);
                    w.WriteEndElement();
                }

                var notable = jobIssues.Where(i => i.Severity >= IssueSeverity.Warning && i.SourceOrdinal is null).Take(200).ToList();
                if (notable.Count > 0)
                {
                    rowIndex++;
                    w.WriteStartElement(new Row { RowIndex = rowIndex });
                    WriteInlineString(w, "Job issues", _styles.HeaderStyle);
                    WriteInlineString(w, string.Empty, _styles.HeaderStyle);
                    w.WriteEndElement();
                    foreach (RecordIssue issue in notable)
                    {
                        rowIndex++;
                        w.WriteStartElement(new Row { RowIndex = rowIndex });
                        WriteInlineString(w, $"{issue.Severity} {issue.Code}", _styles.WarningStyle);
                        WriteInlineString(w, issue.Message, _styles.WarningStyle);
                        w.WriteEndElement();
                    }
                }

                w.WriteEndElement(); // sheetData
                w.WriteEndElement(); // worksheet
            }

            _sheets.Insert(0, ("Summary", _workbookPart.GetIdOfPart(part), false, 2, 1));
        }

        private void WriteWorkbook()
        {
            using OpenXmlWriter w = OpenXmlWriter.Create(_workbookPart);
            w.WriteStartElement(new Workbook());
            w.WriteStartElement(new BookViews());
            w.WriteElement(new WorkbookView { ActiveTab = 0 });
            w.WriteEndElement();
            w.WriteStartElement(new Sheets());
            uint sheetId = 1;
            foreach ((string name, string relId, _, _, _) in _sheets)
            {
                w.WriteElement(new Sheet { Name = name, SheetId = sheetId++, Id = relId });
            }

            w.WriteEndElement();

            var filtered = _sheets.Select((s, index) => (s, index)).Where(x => x.s.AutoFilter).ToList();
            if (filtered.Count > 0)
            {
                w.WriteStartElement(new DefinedNames());
                foreach (((string name, _, _, int columns, uint lastRow), int index) in filtered)
                {
                    w.WriteElement(new DefinedName($"'{name.Replace("'", "''", StringComparison.Ordinal)}'!$A$1:${ColumnLetter(columns)}${lastRow}")
                    {
                        Name = "_xlnm._FilterDatabase",
                        LocalSheetId = (uint)index,
                        Hidden = true,
                    });
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        private static string ColumnLetter(int columnCount)
        {
            int n = Math.Max(1, columnCount);
            var sb = new StringBuilder();
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }

            return sb.ToString();
        }
    }
}

namespace Kb.Plugins.Actions;

/// <summary>A page as a PDF or a Word file, with its stored images.</summary>
[Action("page.export", Read = true)]
public sealed record ExportPage : IAction<FileOut>
{
    public required Guid PageId { get; init; }
    /// <summary>"pdf" or "docx", in any case. Not given: pdf.</summary>
    public string? Format { get; init; }
}

/// <summary>One table of a page as a CSV or an Excel file.</summary>
[Action("page.export.table", Read = true)]
public sealed record ExportPageTable : IAction<FileOut>
{
    public required Guid PageId { get; init; }
    /// <summary>Which table of the page, counted from 1.</summary>
    public required int Number { get; init; }
    /// <summary>"csv" or "xlsx", in any case. Not given: csv.</summary>
    public string? Format { get; init; }
}

/// <summary>One code block of a page as a file, with the extension of its language.</summary>
[Action("page.export.code", Read = true)]
public sealed record ExportPageCode : IAction<FileOut>
{
    public required Guid PageId { get; init; }
    /// <summary>Which code block of the page, counted from 1.</summary>
    public required int Number { get; init; }
}

namespace FinXmlProcessor.Domain.Tables;

public sealed record OutputTableDefinition(string Id, string SheetName, IReadOnlyList<OutputColumnDefinition> Columns)
{
    public int IndexOfColumn(string columnId)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i].Id, columnId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

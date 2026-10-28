using MaintenancePlanner.Application.Imports;

namespace MaintenancePlanner.UnitTests;

public sealed class ImportRecordReaderTests
{
    private static readonly string[] Columns = ["id", "name", "skills"];

    [Fact]
    public void Csv_QuotedFieldsKeepCommasQuotesAndNewlines_AndRowsKeepTheirLineNumbers()
    {
        var read = ImportRecordReader.ReadCsv(
            "﻿id,name,skills\r\nT-1,\"Rao, \"\"Asha\"\"\",A;B\r\nT-2,\"Two\nlines\",C\r\n\r\nT-3,Plain,D\r\n",
            Columns);

        Assert.Empty(read.Issues);
        Assert.Equal(3, read.TotalRecords);
        Assert.Equal(new[] { 2, 3, 6 }, read.Records.Select(record => record.RecordNumber));
        Assert.Equal("Rao, \"Asha\"", read.Records[0].Fields["NAME"]);
        Assert.Equal("Two\nlines", read.Records[1].Fields["name"]);
        Assert.Equal("row 6", read.Records[2].Location);
    }

    [Fact]
    public void Csv_TextAfterAClosingQuote_IsReportedOnItsRow()
    {
        var read = ImportRecordReader.ReadCsv("id,name,skills\nT-1,\"Asha\"x,A", Columns);

        var issue = Assert.Single(read.Issues);
        Assert.Equal(("row 2", 2), (issue.Location, issue.RecordNumber));
        Assert.Contains("after a closing double quote", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_ArraysBecomeListValues_NumbersKeepTheirText_AndDuplicatePropertiesAreReported()
    {
        var read = ImportRecordReader.ReadJson(
            """[{ "id": "T-1", "name": 12.50, "skills": ["A", "B"] }, { "id": "T-2", "ID": "T-3" }, { "id": { "nested": true } }]""",
            Columns);

        var record = Assert.Single(read.Records);
        Assert.Equal(("12.50", "A;B"), (record.Fields["name"], record.Fields["skills"]));
        Assert.Equal(3, read.TotalRecords);
        Assert.Contains(read.Issues, issue => issue.Location == "record 2" && issue.Message.Contains("more than once"));
        Assert.Contains(read.Issues, issue => issue.Location == "record 3" && issue.Field == "id");
    }
}

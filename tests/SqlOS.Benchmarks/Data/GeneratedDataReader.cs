using System.Collections;
using System.Data.Common;

namespace SqlOS.Benchmarks.Data;

/// <summary>
/// A forward-only reader over generated rows, so <c>SqlBulkCopy</c> streams straight from the generator
/// without materializing tens of millions of rows.
/// </summary>
internal sealed class GeneratedDataReader(IReadOnlyList<(string Name, Type Type)> columns, IEnumerable<object?[]> rows) : DbDataReader
{
    private readonly IEnumerator<object?[]> _rows = rows.GetEnumerator();
    private object?[] _current = [];
    private bool _closed;

    public override int FieldCount => columns.Count;
    public override bool HasRows => true;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => -1;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (!_rows.MoveNext())
        {
            return false;
        }

        _current = _rows.Current;
        return true;
    }

    public override object GetValue(int ordinal) => _current[ordinal] ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => _current[ordinal] is null;
    public override string GetName(int ordinal) => columns[ordinal].Name;

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException(name);
    }

    public override Type GetFieldType(int ordinal) => columns[ordinal].Type;
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, columns.Count);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override void Close()
    {
        _closed = true;
        _rows.Dispose();
    }
}

using System.Text;

namespace Meitou.Diagnostics;

/// <summary>
/// Mirrors existing console output into a bounded private log tail, leaving the original log writer in place.
/// </summary>
sealed class DiagnosticConsoleWriter(TextWriter inner, DiagnosticRecorder recorder) : TextWriter
{
    /// <summary>
    /// Serializes fragments from concurrent writers.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// At most one bounded incomplete line.
    /// </summary>
    readonly StringBuilder _line = new();
    /// <summary>
    /// Incomplete lines cannot span a disabled collection interval.
    /// </summary>
    long _epoch;
    /// <summary>
    /// The original console encoding.
    /// </summary>
    public override Encoding Encoding => inner.Encoding;

    /// <summary>
    /// Mirrors a single character without performing diagnostic disk IO.
    /// </summary>
    public override void Write(char value)
    {
        lock (_gate)
        {
            inner.Write(value);
            Capture(value);
        }
    }

    /// <summary>
    /// Mirrors an existing log fragment.
    /// </summary>
    public override void Write(string? value)
    {
        lock (_gate)
        {
            inner.Write(value);
            if (value is null) return;
            foreach (char character in value) Capture(character);
        }
    }

    /// <summary>
    /// Preserves complete lines and the underlying writer's batching.
    /// </summary>
    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            inner.WriteLine(value);
            if (value is not null)
                foreach (char character in value) Capture(character);
            Capture('\n');
        }
    }

    /// <summary>
    /// Flushes the preexisting log writer.
    /// </summary>
    public override void Flush() => inner.Flush();

    /// <summary>
    /// Assembles bounded lines; disabled collection never retains partial text.
    /// </summary>
    void Capture(char value)
    {
        if (_epoch != recorder.Epoch)
        {
            _line.Clear();
            _epoch = recorder.Epoch;
        }

        if (!recorder.Enabled)
        {
            _line.Clear();
            return;
        }

        if (value == '\n')
        {
            recorder.Log(_line.ToString());
            _line.Clear();
        }
        else if (value != '\r' && _line.Length < 1024) _line.Append(value);
    }
}

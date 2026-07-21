using System;
using System.Text;
using System.Windows.Threading;
using TextBox = System.Windows.Controls.TextBox;

namespace RemoteControlServer.Utils;

/// <summary>
/// A TextWriter that redirects everything written via Console.WriteLine/Console.Write into a
/// WPF TextBox instead of (or as well as) an actual console window. This is what lets the main
/// window show a live "debug log" panel while the app itself stays a normal windowed .exe with
/// no separate black cmd window popping up alongside it.
/// </summary>
public class UiConsoleWriter : System.IO.TextWriter
{
    private readonly TextBox _target;
    private readonly Dispatcher _dispatcher;
    private readonly StringBuilder _lineBuffer = new();
    private const int MaxCharsKept = 200_000; // trim old lines so the log box doesn't grow forever

    public UiConsoleWriter(TextBox target)
    {
        _target = target;
        _dispatcher = target.Dispatcher;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            string line = _lineBuffer.ToString();
            _lineBuffer.Clear();
            AppendLine(line);
        }
        else if (value != '\r')
        {
            _lineBuffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (char c in value) Write(c);
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    private void AppendLine(string line)
    {
        string timestamped = $"[{DateTime.Now:HH:mm:ss}] {line}";

        _dispatcher.BeginInvoke(new Action(() =>
        {
            _target.AppendText(timestamped + Environment.NewLine);

            if (_target.Text.Length > MaxCharsKept)
                _target.Text = _target.Text[^MaxCharsKept..];

            _target.CaretIndex = _target.Text.Length;
            _target.ScrollToEnd();
        }));
    }
}

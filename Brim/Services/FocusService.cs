using System.Diagnostics;
using System.Media;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Services;

/// <summary>Pomodoro-style focus/break timer, task list and a stopwatch.</summary>
public sealed class FocusService
{
    readonly AppSettings _s;
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(250) };
    DateTime _endsAt;

    public string Mode { get; private set; } = "Focus";
    public bool Running { get; private set; }
    public TimeSpan Remaining { get; private set; }
    public TimeSpan Total => TimeSpan.FromMinutes(Mode == "Focus" ? _s.FocusMinutes : _s.BreakMinutes);
    public event Action? Changed;
    /// <summary>Raised when a session ends, with the mode that just finished.</summary>
    public event Action<string>? Finished;

    public FocusService(AppSettings s)
    {
        _s = s;
        Remaining = Total;
        _t.Tick += (_, _) => Step();
    }

    void Step()
    {
        if (Running)
        {
            Remaining = _endsAt - DateTime.Now;
            if (Remaining <= TimeSpan.Zero)
            {
                var done = Mode;
                Running = false;
                Mode = done == "Focus" ? "Break" : "Focus";
                Remaining = Total;
                try { SystemSounds.Asterisk.Play(); } catch { }
                Finished?.Invoke(done);
            }
        }
        if (SwRunning) SwChanged?.Invoke();
        if (!Running && !SwRunning) _t.Stop();
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (Running) { Remaining = _endsAt - DateTime.Now; Running = false; }
        else { _endsAt = DateTime.Now + Remaining; Running = true; _t.Start(); }
        Changed?.Invoke();
    }

    public void Reset() { Running = false; Remaining = Total; Changed?.Invoke(); }

    public void SetMode(string mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        Running = false;
        Remaining = Total;
        Changed?.Invoke();
    }

    public void Adjust(string which, int delta)
    {
        if (which == "Focus") _s.FocusMinutes = Math.Clamp(_s.FocusMinutes + delta, 5, 120);
        else _s.BreakMinutes = Math.Clamp(_s.BreakMinutes + delta, 1, 60);
        _s.Changed("Focus");
        if (!Running && which == Mode) Remaining = Total;
        Changed?.Invoke();
    }

    public string NextTask => _s.Tasks.FirstOrDefault(t => !t.Done)?.Text ?? "";

    // ---- tasks
    public void AddTask(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _s.Tasks.Add(new FocusTask { Text = text.Trim() });
        _s.Changed("Tasks");
        Changed?.Invoke();
    }

    public void ToggleTask(FocusTask t) { t.Done = !t.Done; _s.Changed("Tasks"); Changed?.Invoke(); }
    public void RemoveTask(FocusTask t) { _s.Tasks.Remove(t); _s.Changed("Tasks"); Changed?.Invoke(); }

    // ---- stopwatch
    readonly Stopwatch _sw = new();
    public bool SwRunning => _sw.IsRunning;
    public TimeSpan SwElapsed => _sw.Elapsed;
    public List<TimeSpan> Laps { get; } = [];
    public event Action? SwChanged;

    public void SwToggle()
    {
        if (_sw.IsRunning) _sw.Stop(); else { _sw.Start(); _t.Start(); }
        SwChanged?.Invoke();
    }

    public void SwLap() { if (_sw.IsRunning) { Laps.Insert(0, _sw.Elapsed); SwChanged?.Invoke(); } }
    public void SwReset() { _sw.Reset(); Laps.Clear(); SwChanged?.Invoke(); }
}

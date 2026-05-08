namespace Chocobot;

internal sealed class ActiveAlert
{
    public ActiveAlert(string triggerId, string text, DateTime startedAtUtc, TimeSpan duration, TimeSpan countdown, bool speak)
    {
        TriggerId = triggerId;
        Text = text;
        StartedAtUtc = startedAtUtc;
        Duration = duration;
        Countdown = countdown;
        Speak = speak;
    }

    public string TriggerId { get; }
    public string Text { get; }
    public DateTime StartedAtUtc { get; }
    public TimeSpan Duration { get; }
    public TimeSpan Countdown { get; }
    public bool Speak { get; }
    public bool Spoken { get; private set; }
    public DateTime CueAtUtc => StartedAtUtc + Countdown;
    public DateTime ExpiresAtUtc => CueAtUtc + Duration;

    public bool IsPending(DateTime nowUtc)
    {
        return nowUtc < CueAtUtc;
    }

    public bool IsLive(DateTime nowUtc)
    {
        return nowUtc >= CueAtUtc && nowUtc < ExpiresAtUtc;
    }

    public TimeSpan CountdownRemaining(DateTime nowUtc)
    {
        return CueAtUtc > nowUtc ? CueAtUtc - nowUtc : TimeSpan.Zero;
    }

    public bool ShouldSpeak(DateTime nowUtc)
    {
        return Speak && !Spoken && nowUtc >= CueAtUtc;
    }

    public void MarkSpoken()
    {
        Spoken = true;
    }

    public float CountdownProgress(DateTime nowUtc)
    {
        if (Countdown.TotalMilliseconds <= 0)
            return 1;

        var elapsed = nowUtc - StartedAtUtc;
        return Math.Clamp((float)(elapsed.TotalMilliseconds / Countdown.TotalMilliseconds), 0f, 1f);
    }

    public float LiveProgress(DateTime nowUtc)
    {
        if (Duration.TotalMilliseconds <= 0)
            return 1;

        var elapsed = nowUtc - CueAtUtc;
        return Math.Clamp((float)(elapsed.TotalMilliseconds / Duration.TotalMilliseconds), 0f, 1f);
    }
}

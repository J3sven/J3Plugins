namespace Chocobot;

internal sealed class ActiveTimeline
{
    public ActiveTimeline(TimelineDefinition definition, DateTime anchorUtc)
    {
        Definition = definition;
        AnchorUtc = anchorUtc;
    }

    public TimelineDefinition Definition { get; }
    public DateTime AnchorUtc { get; private set; }

    public void Resync(DateTime anchorUtc)
    {
        AnchorUtc = anchorUtc;
    }
}

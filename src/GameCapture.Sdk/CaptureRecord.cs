namespace GameCapture.Sdk
{
    /// <summary>One captured event emitted by a tracker.</summary>
    public sealed record CaptureRecord(DateTime Timestamp, string Plugin, TriggerKind Trigger, string RawText)
    {
        /// <summary>Observation (default) or Cleared. Extend-only addition; pre-existing emits are Observation.</summary>
        public RecordKind Kind { get; init; } = RecordKind.Observation;

        /// <summary>Optional structured payload sinks serialize as columns/JSON props/overlay template
        /// values. Null (default) = only RawText is meaningful, the pre-existing behavior.</summary>
        public IReadOnlyDictionary<string, string>? Fields { get; init; }
    }
}

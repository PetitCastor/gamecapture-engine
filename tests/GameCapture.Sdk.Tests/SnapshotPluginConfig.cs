namespace GameCapture.Sdk.Tests;

internal sealed class SnapshotPluginConfig : PluginConfig
{
    public string LedgerPath { get; set; } = "ledger.csv";

    public SnapshotPluginConfig()
    {
        Outputs =
        [
            new SinkSpec
            {
                Type = "json",
                Path = "captures/records.jsonl",
            },
            new SinkSpec
            {
                Type = "csv",
                Path = "captures/records.csv",
                Columns = ["timestamp", "plugin", "trigger", "rawText"],
            },
            new SinkSpec
            {
                Type = "http",
                Url = "https://example.test/records",
                TimeoutSeconds = 7,
                RecordClears = true,
            },
            new SinkSpec
            {
                Type = "overlay",
                DedupeOnChange = false,
                Overlay = new OverlaySpec
                {
                    Anchor = OverlayAnchor.Custom,
                    X = 32,
                    Y = 48,
                    Width = 420,
                    Height = 96,
                    Template = "{plugin}: {rawText}",
                    LingerMs = 2500,
                },
            },
        ];
    }
}

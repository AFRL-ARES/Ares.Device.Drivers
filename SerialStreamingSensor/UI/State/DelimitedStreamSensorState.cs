namespace SerialStreamingSensor.UI.State
{
    public sealed class DelimitedStreamSensorState
    {
        public string Name { get; init; } = string.Empty;

        public IReadOnlyList<DelimitedStreamSensorLiveDataEntry> LiveData
        {
            get;
            init;
        } = Array.Empty<DelimitedStreamSensorLiveDataEntry>();
    }

    public sealed class DelimitedStreamSensorLiveDataEntry
    {
        public string Name { get; init; } = string.Empty;

        public double? Value { get; init; }
    }
}
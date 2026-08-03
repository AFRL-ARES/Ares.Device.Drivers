using StreamHelper;

namespace SerialStreamingSensor.Models
{
    public sealed class StreamingField
    {
        public required string Name { get; init; }

        /// <summary>
        /// Position of this field in the incoming delimited data line.
        /// Empty entries in the data format are counted when assigning
        /// this index, even though they are not represented as fields.
        /// </summary>
        public int DataIndex { get; init; }

        /// <summary>
        /// Most recently parsed value.
        /// </summary>
        public double? Value { get; set; }

        /// <summary>
        /// Determines whether incoming values are added to Stats.
        /// </summary>
        public bool StatsActive { get; set; }

        public SimpleStreamingStats Stats { get; } = new();
    }
}
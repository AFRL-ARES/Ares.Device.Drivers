using Ares.Datamodel;
using Ares.Datamodel.Extensions;

namespace SerialStreamingSensor.UI.State
{
    public static class DelimitedStreamSensorStateMapper
    {
        public static DelimitedStreamSensorState FromAresStruct(
            AresStruct state)
        {
            var model = new DelimitedStreamSensorState
            {

                Name = state.Fields
                    .GetValueOrDefault("Name")?
                    .StringValue ?? string.Empty
            };

            if (state.Fields.TryGetValue(
                    "LiveData",
                    out var liveDataValue) &&
                liveDataValue.StructValue is not null)
            {
                model = new DelimitedStreamSensorState
                {
                    Name = model.Name,

                    LiveData = liveDataValue.StructValue.Fields
                        .Where(field =>
                            !string.IsNullOrWhiteSpace(field.Key))
                        .Select(field =>
                            new DelimitedStreamSensorLiveDataEntry
                            {
                                Name = field.Key,

                                Value = field.Value.HasNumberValue
                                    ? field.Value.NumberValue
                                    : null
                            })
                        .ToArray()
                };
            }

            return model;
        }
    }
}
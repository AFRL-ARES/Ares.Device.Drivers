using Ares.Datamodel;
using Ares.Datamodel.Extensions;
using SpectralCameraRemastered.Models;
using SpectralCameraRemastered.UI.Models;

namespace SpectralCameraRemastered.UI.Mappers;

public static class SpectralCameraStateMapper
{
    public static SpectralCameraUIState FromAresStruct(AresStruct state)
    {
        var uiState = new SpectralCameraUIState
        {
            Status = state.Fields.GetValueOrDefault("Status")?.StringValue ?? "Unknown",
            Temperature = state.Fields.GetValueOrDefault("Temperature")?.NumberValue ?? 0,
            TemperatureSetpoint = state.Fields.GetValueOrDefault("TemperatureSetpoint")?.NumberValue ?? 0,
            IsScanning = state.Fields.GetValueOrDefault("IsScanning")?.BoolValue ?? false,
            ExposureTime = state.Fields.GetValueOrDefault("ExposureTime")?.NumberValue ?? 0
        };

        if (state.Fields.TryGetValue("LiveData", out var liveData) && liveData.StructValue != null)
        {
            uiState.RawData = liveData.StructValue.Fields.GetValueOrDefault("RawData")?.ListValue?.Values
                .Select(v => (int)v.NumberValue).ToList() ?? new();

            uiState.CalibratedData = liveData.StructValue.Fields.GetValueOrDefault("CalibratedData")?.ListValue?.Values
                .Where(v => v.StructValue != null)
                .Select(v => new RamanDataPoint(
                    v.StructValue!.Fields.GetValueOrDefault("X")?.NumberValue ?? 0,
                    v.StructValue!.Fields.GetValueOrDefault("Y")?.NumberValue ?? 0))
                .ToList() ?? new();
        }

        if (state.Fields.TryGetValue("Calibration", out var cal) && cal.StructValue != null)
        {
            uiState.CalibrationCoefficients = cal.StructValue.Fields.GetValueOrDefault("Coefficients")?.ListValue?.Values
                .Select(v => v.NumberValue).ToList() ?? new();

            uiState.CalibrationPeaks = cal.StructValue.Fields.GetValueOrDefault("Peaks")?.ListValue?.Values
                .Where(v => v.StructValue != null)
                .Select(v => new CalibrationPeak(
                    v.StructValue!.Fields.GetValueOrDefault("Name")?.StringValue ?? "",
                    v.StructValue!.Fields.GetValueOrDefault("WaveNumber")?.NumberValue ?? 0,
                    v.StructValue!.Fields.GetValueOrDefault("PixelNumber")?.NumberValue ?? 0,
                    v.StructValue!.Fields.GetValueOrDefault("DrawOnMain")?.BoolValue ?? true,
                    v.StructValue!.Fields.GetValueOrDefault("ColorHex")?.StringValue ?? "#808080"
                )).ToList() ?? new();
        }

        return uiState;
    }
}

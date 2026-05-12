using SpectralCameraRemastered.Enums;
using SpectralCameraRemastered.Models;

namespace SpectralCameraRemastered.UI.Models;

public class SpectralCameraUIState
{
    public string Status { get; set; } = "Unknown";
    public double Temperature { get; set; }
    public double TemperatureSetpoint { get; set; }
    public bool IsScanning { get; set; }
    public double ExposureTime { get; set; }

    public List<int> RawData { get; set; } = new();
    public List<RamanDataPoint> CalibratedData { get; set; } = new();

    public List<double> CalibrationCoefficients { get; set; } = new();
    public List<CalibrationPeak> CalibrationPeaks { get; set; } = new();
}

public record RamanDataPoint(double X, double Y);

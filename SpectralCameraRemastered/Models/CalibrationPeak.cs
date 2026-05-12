namespace SpectralCameraRemastered.Models;

public record CalibrationPeak(
    string Name,
    double WaveNumber,
    double PixelNumber,
    bool DrawOnMain = true,
    string ColorHex = "#808080" // Default Gray
);

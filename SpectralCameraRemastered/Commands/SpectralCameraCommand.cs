namespace SpectralCameraRemastered.Commands;

public enum SpectralCameraCommand
{
    GetAcqData,
    GetStatus,
    StartAcq,
    AbortAcq,
    SetReadMode,
    SetAcqMode,
    SetTriggerMode,
    SetExposureTime,
    SetTemperature,
    GetTemperature,
    SetCooler,
    SetCoolerMode,
    SetFanMode,
    SetSingleTrack,
    SetTransferMode,
    SetFilterMode,
    SetBaselineClampMode,
    RamanContScan,
    RamanSingAvgScan,
    RamanSingScan,
}

public enum SpectralCameraCommandParameter
{
    Mode,
    Clamp,
    CoolerState,
    CoolerMode,
    ExposureTime,
    FanMode,
    FilterMode,
    ReadMode,
    BinningCenter,
    BinningHeight,
    Temperature,
    TransferMode,
    TriggerMode,
    Iterations
}

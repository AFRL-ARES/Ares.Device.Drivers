namespace SpectralCameraRemastered.Enums;

public static class NewtonCommandTypes
{
    public enum FanModeType { FULL = 1 }
    public enum FilterModeType { OFF = 0, ON = 2 }
    public enum FrameTransferModeType { OFF = 0, ON = 1 }
    public enum BaselineClampType { DISABLE = 0, ENABLED = 1 }
    public enum CoolerModeType { TO_AMBIENT = 0, MAINTAIN = 1 }
    public enum ReadModeType { FULL_VERTICAL = 0, MULTI_TRACK = 1, RANDOM_TRACK = 2, SINGLE_TRACK = 3, IMAGE = 4 }
    public enum TriggerModeType { INTERNAL = 0, EXTERNAL = 1, EXTERNAL_START = 6, EXTERNAL_EXPOSURE = 7, EXTERNAL_FVB_EM = 9, SOFTWARE = 10 }
    public enum AcquisitionModeType { SINGLE_SCAN = 1, ACCUMULATE = 2, KINETICS = 3, FAST_KINETICS = 4, RUN_TIL_ABORT = 5, TIME_DELAYED_INTEGRATION = 9 }
}

public enum RamanScanType
{
    Single,
    Continuous
}

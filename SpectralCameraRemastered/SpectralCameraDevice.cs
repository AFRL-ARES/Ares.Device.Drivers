using Ares.Datamodel;
using Ares.Datamodel.Device;
using Ares.Datamodel.Extensions;
using Ares.Datamodel.Factories;
using Ares.Device;
using Microsoft.Extensions.Logging;
using SpectralCameraRemastered.Commands;
using SpectralCameraRemastered.Enums;
using SpectralCameraRemastered.Models;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MathNet.Numerics;

#if !SIMULATED
using ATMCD64CS;
#endif

namespace SpectralCameraRemastered;

public class SpectralCameraDevice : AresDevice
{
    private readonly BehaviorSubject<AresStruct> _stateSubject = new(new AresStruct());
    private CancellationTokenSource _pollingLoopTokenSource = new();
    private Task _pollingTask = Task.CompletedTask;
    private readonly ILogger _logger;

    private int _ccdWidth;
    private int _ccdHeight;
    private int _binningHeight;
    private int _binningCenter;
    private float _exposureTime = 1.0f;
    private double _tempSetpoint = -40;

    private bool _isScanning;
    private Task<string?> _currentScanTask = Task.FromResult<string?>(null);
    private CancellationTokenSource? _scanCts;

    private int[] _rawSpectrum = Array.Empty<int>();
    private List<CalibrationPeak> _calibrationPeaks = new();
    private double[] _calibrationCoeffs = Array.Empty<double>();
    private Func<double, double> _calibrationFunc = x => x;

    public void UpdateCalibration(List<CalibrationPeak> peaks, int order = 2)
    {
        _calibrationPeaks = peaks;
        if (peaks.Count > 1)
        {
            var actualOrder = Math.Min(order, peaks.Count - 1);
            // Use QR decomposition for numerical stability
            _calibrationCoeffs = Fit.Polynomial(
                peaks.Select(p => p.PixelNumber).ToArray(),
                peaks.Select(p => p.WaveNumber).ToArray(),
                actualOrder);
            
            _calibrationFunc = x => EvaluatePolynomial(_calibrationCoeffs, x);
        }
        else
        {
            _calibrationCoeffs = Array.Empty<double>();
            _calibrationFunc = x => x;
        }
        _ = UpdateDeviceState();
    }

    private static double EvaluatePolynomial(double[] coefficients, double x)
    {
        double result = 0;
        for (int i = 0; i < coefficients.Length; i++)
        {
            result += coefficients[i] * Math.Pow(x, i);
        }
        return result;
    }

#if !SIMULATED
    private AndorSDK? _andorSdk;
    private int _cameraHandle;
#endif

    public SpectralCameraDevice(DeviceConnectionInfo connectionInfo, ILogger logger) : base(connectionInfo)
    {
        _logger = logger;
        StateStream = _stateSubject.AsObservable();

        _ccdWidth = (int)(connectionInfo.DeviceSettings.Fields.GetValueOrDefault("CCDWidth")?.NumberValue ?? 2048);
        _ccdHeight = (int)(connectionInfo.DeviceSettings.Fields.GetValueOrDefault("CCDHeight")?.NumberValue ?? 512);
        _binningHeight = (int)(connectionInfo.DeviceSettings.Fields.GetValueOrDefault("CCDBinningHeight")?.NumberValue ?? 15);
        _binningCenter = (int)(connectionInfo.DeviceSettings.Fields.GetValueOrDefault("CCDBinningCenter")?.NumberValue ?? 256);
        _tempSetpoint = connectionInfo.DeviceSettings.Fields.GetValueOrDefault("TempSetPoint")?.NumberValue ?? -40;

        _rawSpectrum = new int[_ccdWidth];

        _calibrationPeaks = new List<CalibrationPeak>
        {
            new("SiS", 520.5, 490),
            new("Neon 1", 1710.3, 1703),
            new("Neon 2", 1975.6, 2001)
        };
        UpdateCalibration(_calibrationPeaks);

        DefineSchemas();
    }

    private void DefineSchemas()
    {
        StateSchema = AresSchemaBuilder.Empty()
            .AddEntry("Status", AresSchemaBuilder.StringEntry().Build())
            .AddEntry("Temperature", AresSchemaBuilder.NumberEntry().Build())
            .AddEntry("TemperatureSetpoint", AresSchemaBuilder.NumberEntry().Build())
            .AddEntry("IsScanning", AresSchemaBuilder.BooleanEntry().Build())
            .AddEntry("ExposureTime", AresSchemaBuilder.NumberEntry().Build())
            .AddEntry("LiveData", AresSchemaBuilder.StructEntry()
                .WithStructSchema(s =>
                {
                    s.Fields.Add("RawData", AresSchemaBuilder.ListEntry().WithListElementSchema(AresDataType.Number).Build());
                    s.Fields.Add("CalibratedData", AresSchemaBuilder.ListEntry()
                        .WithListElementSchema(e => e.WithStructSchema(p =>
                        {
                            p.Fields.Add("X", AresSchemaBuilder.NumberEntry().Build());
                            p.Fields.Add("Y", AresSchemaBuilder.NumberEntry().Build());
                        })).Build());
                }).Build())
            .AddEntry("Calibration", AresSchemaBuilder.StructEntry()
                .WithStructSchema(s =>
                {
                    s.Fields.Add("Coefficients", AresSchemaBuilder.ListEntry().WithListElementSchema(AresDataType.Number).Build());
                    s.Fields.Add("Peaks", AresSchemaBuilder.ListEntry()
                        .WithListElementSchema(e => e.WithStructSchema(p =>
                        {
                            p.Fields.Add("Name", AresSchemaBuilder.StringEntry().Build());
                            p.Fields.Add("WaveNumber", AresSchemaBuilder.NumberEntry().Build());
                            p.Fields.Add("PixelNumber", AresSchemaBuilder.NumberEntry().Build());
                            p.Fields.Add("DrawOnMain", AresSchemaBuilder.BooleanEntry().Build());
                            p.Fields.Add("ColorHex", AresSchemaBuilder.StringEntry().Build());
                        })).Build());
                }).Build())
            .Build();

        SettingSchema = AresSchemaBuilder.Empty()
            .AddEntry("ExposureTime", AresSchemaBuilder.NumberEntry().Build())
            .AddEntry("TempSetpoint", AresSchemaBuilder.NumberEntry().Build())
            .AddEntry("FanMode", AresSchemaBuilder.StringEntry().WithAllowedValues(Enum.GetNames<NewtonCommandTypes.FanModeType>()).Build())
            .AddEntry("ReadMode", AresSchemaBuilder.StringEntry().WithAllowedValues(Enum.GetNames<NewtonCommandTypes.ReadModeType>()).Build())
            .AddEntry("TriggerMode", AresSchemaBuilder.StringEntry().WithAllowedValues(Enum.GetNames<NewtonCommandTypes.TriggerModeType>()).Build())
            .AddEntry("AcquisitionMode", AresSchemaBuilder.StringEntry().WithAllowedValues(Enum.GetNames<NewtonCommandTypes.AcquisitionModeType>()).Build())
            .Build();
    }

    public override IObservable<AresStruct> StateStream { get; }

    public override Task<AresStruct> GetState() => Task.FromResult(_stateSubject.Value);

    public override async Task<bool> Activate(CancellationToken ct)
    {
        _logger.LogInformation($"Activating Spectral Camera: {Name}");

        bool success = await InitializeHardware();
        if (success)
        {
            Status = new DeviceOperationalStatus { OperationalState = OperationalState.Active, Message = "Spectral Camera connected and initialized." };
            StartPollingLoop();
        }
        else
        {
            Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = "Failed to initialize Andor SDK." };
        }

        return success;
    }

    private async Task<bool> InitializeHardware()
    {
#if SIMULATED
        _logger.LogInformation("Newton in Simulated Mode.");
        return true;
#else
        try
        {
            _andorSdk = new AndorSDK();
            int cameraCount = 0;
            if (_andorSdk.GetAvailableCameras(ref cameraCount) < 1)
            {
                _logger.LogError("No Andor cameras found.");
                return false;
            }

            // For now, take the first Newton found
            bool found = false;
            for (int i = 0; i < cameraCount; i++)
            {
                int handle = 0;
                _andorSdk.GetCameraHandle(i, ref handle);
                _andorSdk.SetCurrentCamera(handle);
                _andorSdk.Initialize(null);

                var caps = new AndorSDK.AndorCapabilities();
                caps.ulSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(caps);
                _andorSdk.GetCapabilities(ref caps);

                if (caps.ulCameraType == 8) // NEWTON
                {
                    _cameraHandle = handle;
                    found = true;
                    break;
                }
            }

            if (!found) return false;

            // Basic initialization from legacy code
            _andorSdk.SetHighCapacity(0);
            _andorSdk.SetHSSpeed(1, 0);
            _andorSdk.SetVSSpeed(1);
            
            // Apply initial settings
            _andorSdk.SetTemperature((int)_tempSetpoint);
            _andorSdk.SetExposureTime(_exposureTime);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Andor SDK");
            return false;
        }
#endif
    }

    private void StartPollingLoop()
    {
        _pollingLoopTokenSource = new CancellationTokenSource();
        _pollingTask = Task.Run(async () =>
        {
            while (!_pollingLoopTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await UpdateDeviceState();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error in Spectral Camera polling loop");
                }
                await Task.Delay(500, _pollingLoopTokenSource.Token);
            }
        }, _pollingLoopTokenSource.Token);
    }

    private async Task UpdateDeviceState()
    {
        int temp = 0;
        string statusStr = "Unknown";

#if SIMULATED
        temp = (int)_tempSetpoint + Random.Shared.Next(-2, 3);
        statusStr = _isScanning ? "Acquiring" : "Idle";
#else
        if (_andorSdk != null)
        {
            _andorSdk.GetTemperature(ref temp);
            int sdkStatus = 0;
            _andorSdk.GetStatus(ref sdkStatus);
            statusStr = sdkStatus switch
            {
                20073 => "Idle", // DRV_IDLE
                20072 => "Acquiring", // DRV_ACQUIRING
                _ => $"Status Code: {sdkStatus}"
            };
        }
#endif

        var builder = AresStateBuilder.Create()
            .Add("Status", statusStr)
            .Add("Temperature", (double)temp)
            .Add("TemperatureSetpoint", _tempSetpoint)
            .Add("IsScanning", _isScanning)
            .Add("ExposureTime", (double)_exposureTime);

        // Add Live Data
        builder.AddStruct("LiveData", s =>
        {
            s.AddList("RawData", _rawSpectrum.Select(v => new AresValue { NumberValue = v }));
            s.AddList("CalibratedData", _rawSpectrum.Select((v, i) =>
            {
                return new AresValue
                {
                    StructValue = AresStateBuilder.Create()
                        .Add("X", _calibrationFunc(i))
                        .Add("Y", (double)v)
                        .Build()
                };
            }));
        });

        // Add Calibration
        builder.AddStruct("Calibration", s =>
        {
            s.AddList("Coefficients", _calibrationCoeffs.Select(c => new AresValue { NumberValue = c }));
            s.AddList("Peaks", _calibrationPeaks.Select(p => new AresValue
            {
                StructValue = AresStateBuilder.Create()
                    .Add("Name", p.Name)
                    .Add("WaveNumber", p.WaveNumber)
                    .Add("PixelNumber", p.PixelNumber)
                    .Add("DrawOnMain", p.DrawOnMain)
                    .Add("ColorHex", p.ColorHex)
                    .Build()
            }));
        });

        _stateSubject.OnNext(builder.Build());
    }

    public override async Task EnterSafeMode(CancellationToken ct)
    {
        await AbortAcquisition();
#if !SIMULATED
        _andorSdk?.CoolerOFF();
#endif
    }

    private async Task AbortAcquisition()
    {
        _scanCts?.Cancel();
        _isScanning = false;
#if !SIMULATED
        _andorSdk?.AbortAcquisition();
#endif
        await _currentScanTask;
    }

    public override async Task<CommandResult> ExecuteCommand(string command, List<DeviceCommandArgument> arguments, CancellationToken token)
    {
        if (!Enum.TryParse<SpectralCameraCommand>(command, out var cmd))
        {
            return new CommandResult { Success = false, Error = $"Unsupported command: {command}" };
        }

        try
        {
            switch (cmd)
            {
                case SpectralCameraCommand.StartAcq:
                    await StartAcquisition();
                    break;
                case SpectralCameraCommand.AbortAcq:
                    await AbortAcquisition();
                    break;
                case SpectralCameraCommand.SetExposureTime:
                    var exp = arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.ExposureTime.ToString())?.ArgValue.NumberValue;
                    if (exp.HasValue) await SetExposureTime((float)exp.Value);
                    break;
                case SpectralCameraCommand.RamanSingScan:
                    var iters = (int)(arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.Iterations.ToString())?.ArgValue.NumberValue ?? 1);
                    await RunRamanScan(false, iters, token);
                    break;
                case SpectralCameraCommand.RamanContScan:
                    await RunRamanScan(true, 1, token);
                    break;
                case SpectralCameraCommand.SetCooler:
                    var coolerOn = arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.CoolerState.ToString())?.ArgValue.BoolValue ?? false;
#if !SIMULATED
                    if (coolerOn) _andorSdk?.CoolerON(); else _andorSdk?.CoolerOFF();
#endif
                    break;
                case SpectralCameraCommand.SetFanMode:
                    var fanModeStr = arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.FanMode.ToString())?.ArgValue.StringValue;
                    if (Enum.TryParse<NewtonCommandTypes.FanModeType>(fanModeStr, out var fanMode))
#if !SIMULATED
                        _andorSdk?.SetFanMode((int)fanMode);
#endif
                    break;
                case SpectralCameraCommand.SetReadMode:
                    var readModeStr = arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.ReadMode.ToString())?.ArgValue.StringValue;
                    if (Enum.TryParse<NewtonCommandTypes.ReadModeType>(readModeStr, out var readMode))
#if !SIMULATED
                        _andorSdk?.SetReadMode((int)readMode);
#endif
                    break;
                case SpectralCameraCommand.SetAcqMode:
                    var acqModeStr = arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.Mode.ToString())?.ArgValue.StringValue;
                    if (Enum.TryParse<NewtonCommandTypes.AcquisitionModeType>(acqModeStr, out var acqMode))
                        await SetAcquisitionMode(acqMode);
                    break;
                case SpectralCameraCommand.SetTriggerMode:
                    var trigModeStr = arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.TriggerMode.ToString())?.ArgValue.StringValue;
                    if (Enum.TryParse<NewtonCommandTypes.TriggerModeType>(trigModeStr, out var trigMode))
                        await SetTriggerMode(trigMode);
                    break;
                case SpectralCameraCommand.SetTemperature:
                    var temp = arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.Temperature.ToString())?.ArgValue.NumberValue;
                    if (temp.HasValue)
                    {
                        _tempSetpoint = temp.Value;
#if !SIMULATED
                        _andorSdk?.SetTemperature((int)_tempSetpoint);
#endif
                    }
                    break;
                case SpectralCameraCommand.SetBaselineClampMode:
                    var clampStr = arguments.FirstOrDefault(a => a.ArgName == SpectralCameraCommandParameter.Clamp.ToString())?.ArgValue.StringValue;
                    if (Enum.TryParse<NewtonCommandTypes.BaselineClampType>(clampStr, out var clamp))
                        await SetBaselineClampMode(clamp);
                    break;
                default:
                    return new CommandResult { Success = false, Error = $"Command {cmd} is defined but execution logic is missing." };
            }
            return new CommandResult { Success = true };
        }
        catch (Exception ex)
        {
            return new CommandResult { Success = false, Error = ex.Message };
        }
    }

    private async Task StartAcquisition()
    {
        _isScanning = true;
#if !SIMULATED
        _andorSdk?.StartAcquisition();
#endif
        await Task.CompletedTask;
    }

    private async Task SetExposureTime(float seconds)
    {
        _exposureTime = seconds;
#if !SIMULATED
        _andorSdk?.SetExposureTime(seconds);
#endif
        await Task.CompletedTask;
    }

    private async Task SetFanMode(NewtonCommandTypes.FanModeType mode)
    {
#if !SIMULATED
        _andorSdk?.SetFanMode((int)mode);
#endif
        await Task.CompletedTask;
    }

    private async Task SetReadMode(NewtonCommandTypes.ReadModeType mode)
    {
#if !SIMULATED
        _andorSdk?.SetReadMode((int)mode);
#endif
        await Task.CompletedTask;
    }

    private async Task SetAcquisitionMode(NewtonCommandTypes.AcquisitionModeType mode)
    {
#if !SIMULATED
        _andorSdk?.SetAcquisitionMode((int)mode);
#endif
        await Task.CompletedTask;
    }

    private async Task SetTriggerMode(NewtonCommandTypes.TriggerModeType mode)
    {
#if !SIMULATED
        _andorSdk?.SetTriggerMode((int)mode);
#endif
        await Task.CompletedTask;
    }

    private async Task SetBaselineClampMode(NewtonCommandTypes.BaselineClampType mode)
    {
#if !SIMULATED
        _andorSdk?.SetBaselineClamp((int)mode);
#endif
        await Task.CompletedTask;
    }

    private async Task RunRamanScan(bool continuous, int iterations, CancellationToken ct)
    {
        await AbortAcquisition();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isScanning = true;

        _currentScanTask = Task.Run(async () =>
        {
            try
            {
                while (!_scanCts.Token.IsCancellationRequested)
                {
                    int[] buffer = new int[_ccdWidth];
                    long[] sum = new long[_ccdWidth]; // Use long to avoid overflow during summation

                    for (int i = 0; i < iterations; i++)
                    {
                        if (_scanCts.Token.IsCancellationRequested) break;

#if SIMULATED
                        await Task.Delay((int)(_exposureTime * 1000), _scanCts.Token);
                        for (int j = 0; j < _ccdWidth; j++) buffer[j] = Random.Shared.Next(300, 1000);
                        
                        // Simulate Raman peaks (e.g. G and G' bands)
                        SimulatePeak(buffer, 500, 15000, 20); // Peak 1
                        SimulatePeak(buffer, 1200, 8000, 15); // Peak 2
#else
                        if (_andorSdk == null) return "SDK not initialized";
                        
                        uint startStatus = _andorSdk.StartAcquisition();
                        if (startStatus != 20002) return $"Failed to start acq: {startStatus}";

                        uint waitStatus;
                        do {
                            await Task.Delay(100, _scanCts.Token);
                            waitStatus = _andorSdk.WaitForAcquisitionTimeOut(100);
                        } while (waitStatus == 20072 && !_scanCts.Token.IsCancellationRequested); // DRV_ACQUIRING

                        if (waitStatus != 20002) return $"Wait for acq failed: {waitStatus}";

                        _andorSdk.GetAcquiredData(buffer, (uint)buffer.Length);
#endif
                        for (int j = 0; j < _ccdWidth; j++) sum[j] += buffer[j];
                    }

                    int[] finalSpectrum = new int[_ccdWidth];
                    for (int j = 0; j < _ccdWidth; j++) finalSpectrum[j] = (int)(sum[j] / iterations);
                    _rawSpectrum = finalSpectrum;

                    // Trigger immediate state update after a scan
                    _ = UpdateDeviceState();

                    if (!continuous) break;
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                return "Cancelled";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Raman scan");
                return ex.Message;
            }
            finally
            {
                _isScanning = false;
                _ = UpdateDeviceState();
            }
        }, _scanCts.Token);
    }

    private static void SimulatePeak(int[] buffer, int pos, int height, int width)
    {
        for (int j = pos - width * 3; j < pos + width * 3; j++)
        {
            if (j >= 0 && j < buffer.Length)
            {
                buffer[j] += (int)(height * Math.Exp(-Math.Pow(j - pos, 2) / (2.0 * width * width)));
            }
        }
    }

    public override async Task UpdateSettings(AresStruct settings)
    {
        if (settings.Fields.TryGetValue("ExposureTime", out var exp) && exp.HasNumberValue)
        {
            await SetExposureTime((float)exp.NumberValue);
        }
        if (settings.Fields.TryGetValue("TempSetpoint", out var tsp) && tsp.HasNumberValue)
        {
            _tempSetpoint = tsp.NumberValue;
#if !SIMULATED
            _andorSdk?.SetTemperature((int)_tempSetpoint);
#endif
        }
    }

    public override Task<AresStruct> GetSettings()
    {
        var settings = AresStructHelper.CreateEmpty();
        settings.AddNumber("ExposureTime", _exposureTime);
        settings.AddNumber("TempSetpoint", _tempSetpoint);
        return Task.FromResult(settings);
    }

    protected override Task<List<DeviceCommandDescriptor>> BuildCommandDescriptorsAsync()
    {
        var descriptors = new List<DeviceCommandDescriptor>
        {
            new() { Name = SpectralCameraCommand.StartAcq.ToString(), Description = "Starts raw acquisition." },
            new() { Name = SpectralCameraCommand.AbortAcq.ToString(), Description = "Aborts current acquisition." },
            new() { Name = SpectralCameraCommand.RamanSingScan.ToString(), Description = "Runs a single Raman scan (averaged if iterations > 1).",
                InputSchema = AresSchemaBuilder.Empty().AddEntry(SpectralCameraCommandParameter.Iterations.ToString(), AresSchemaBuilder.NumberEntry().Build()).Build() },
            new() { Name = SpectralCameraCommand.RamanContScan.ToString(), Description = "Starts a continuous Raman scan." },
            new() { Name = SpectralCameraCommand.SetCooler.ToString(), Description = "Sets the CCD cooler state.",
                InputSchema = AresSchemaBuilder.Empty().AddEntry(SpectralCameraCommandParameter.CoolerState.ToString(), AresSchemaBuilder.BooleanEntry().Build()).Build() },
            new() { Name = SpectralCameraCommand.SetFanMode.ToString(), Description = "Sets the CCD fan mode.",
                InputSchema = AresSchemaBuilder.Empty().AddEntry(SpectralCameraCommandParameter.FanMode.ToString(), AresSchemaBuilder.StringEntry().WithAllowedValues(Enum.GetNames<NewtonCommandTypes.FanModeType>()).Build()).Build() },
            new() { Name = SpectralCameraCommand.SetReadMode.ToString(), Description = "Sets the CCD read mode.",
                InputSchema = AresSchemaBuilder.Empty().AddEntry(SpectralCameraCommandParameter.ReadMode.ToString(), AresSchemaBuilder.StringEntry().WithAllowedValues(Enum.GetNames<NewtonCommandTypes.ReadModeType>()).Build()).Build() },
            new() { Name = SpectralCameraCommand.SetExposureTime.ToString(), Description = "Sets the CCD exposure time.",
                InputSchema = AresSchemaBuilder.Empty().AddEntry(SpectralCameraCommandParameter.ExposureTime.ToString(), AresSchemaBuilder.NumberEntry().Build()).Build() }
        };
        return Task.FromResult(descriptors);
    }
}
